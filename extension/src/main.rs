#![warn(rust_2018_idioms)]

use std::process;
use std::sync::atomic::AtomicBool;
use std::sync::Arc;

use env_logger;
use lambda_runtime::LambdaEvent;
use std::io::Write;
use tokio::signal::unix::{signal, SignalKind};

use crate::options::{Options, Runtime};
use crate::time::current_time_millis;

mod app_request;
mod app_start;
mod cert;
mod counter_drop;
mod messages;
mod options;
mod ping;
mod run;
mod threads;
mod time;

fn main() -> anyhow::Result<()> {
  env_logger::Builder::new()
    .format(|buf, record| {
      writeln!(
        buf,
        "{} [{}] - {}",
        chrono::Local::now().format("%Y-%m-%dT%H:%M:%S.%3f"),
        record.level(),
        record.args()
      )
    })
    .filter(None, log::LevelFilter::Info) // Change this to Debug or Warn as needed
    .init();

  // required to enable CloudWatch error logging by the runtime
  tracing_subscriber::fmt()
    .with_max_level(tracing::Level::INFO)
    // disable printing the name of the module in every log line.
    .with_target(false)
    // disabling time is handy because CloudWatch will add the ingestion time.
    .without_time()
    .init();

  let options = Options::default();

  // If LAMBDA_DISPATCH_RUNTIME=default_multi_thread, use a multi-threaded runtime, let tokio decide how many threads
  // If LAMBDA_DISPATCH_RUNTIME=multi_thread, use a multi-threaded runtime,
  //   but use 2 threads or whatever is set in TOKIO_WORKER_THREADS
  // If LAMBDA_DISPATCH_RUNTIME=current_thread, use a single-threaded runtime
  // If LAMBDA_DISPATCH_RUNTIME is not set, use a single-threaded runtime
  match options.runtime {
    Runtime::MultiThread => {
      log::info!("Using multi_thread runtime");
      // TOKIO_WORKER_THREADS is a standard var - we will
      // use it if it is set, otherwise we will default to 2
      // If we did nothing then it would be used if set or default to
      // the number of cores on the machine (which is too many in our case)
      let worker_threads = std::env::var("TOKIO_WORKER_THREADS")
        .ok()
        .and_then(|s| s.parse().ok())
        .unwrap_or(2);
      let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .worker_threads(worker_threads)
        .build()
        .unwrap();
      runtime.block_on(async_main(&options))?;
    }
    Runtime::DefaultMultiThread => {
      log::info!("Using default_multi_thread runtime");
      // Let tokio decide how many threads to use
      let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .unwrap();
      runtime.block_on(async_main(&options))?;
    }
    // Default or `current_thread` runtime
    Runtime::CurrentThread => {
      log::info!("Using current_thread runtime");
      let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
      runtime.block_on(async_main(&options))?;
    }
  }

  Ok(())
}

async fn async_main(options: &Options) -> anyhow::Result<()> {
  let mut term_signal = signal(SignalKind::terminate())?;

  let thread_count = threads::get_threads();
  log::info!("PID: {}, Thread count: {}", process::id(), thread_count);

  // Span a task to handle the extension
  // DO NOT await this task, otherwise the lambda will not be invoked
  // Once the extension is registered with the name matching the executable name,
  // Lambda will proceed to start the `CMD` in the Dockerfile.
  // The startup order is:
  // 1. Extension Registers with Lamba Runtime API
  // 2. Lambda starts CMD (e.g. node.js app)
  // 3. Health chuck loop waits for app to be ready
  // 4. Handler Registers with Lambda Runtime API
  // The first time the handler gets invoked: the app is already cold-started
  tokio::spawn(async move {
    let func = lambda_extension::service_fn(my_extension);
    let extension = lambda_extension::Extension::new()
      // Internal extensions only support INVOKE events.
      .with_events(&["SHUTDOWN"])
      .with_events_processor(func)
      .with_extension_name("lambda-dispatch")
      // Extensions MUST be registered before calling lambda_runtime::run(), which ends the Init
      // phase and begins the Invoke phase.
      .register()
      .await
      .unwrap();
    match extension.run().await {
      Ok(_) => {
        log::info!("Extension exited");
      }
      Err(err) => {
        log::error!("Extension.run error: {:?}", err)
      }
    }
  });

  // Wait for the contained app to be ready
  // TODO: This is where we need to bail out of waiting for the app to be ready
  // TODO: If we bail out here then we need to send another init request on the first request
  // we process
  app_start::health_check_contained_app(Arc::new(AtomicBool::new(false)), &options).await;

  let func = lambda_runtime::service_fn(move |req| my_handler(req, &options));
  tokio::select! {
      lambda_result = lambda_runtime::run(func) => {
          match lambda_result {
              Ok(_) => {}
              Err(e) => {
                log::error!("Error: {}", e);
              }
          }
      }
      _ = term_signal.recv() => {
        log::warn!("SIGTERM received, stopping...");
      }
  }

  Ok(())
}

async fn my_extension(
  event: lambda_extension::LambdaEvent,
) -> std::result::Result<(), lambda_extension::Error> {
  match event.next {
    lambda_extension::NextEvent::Shutdown(_e) => {
      // do something with the shutdown event
      log::info!("Extension - Shutdown event received");
    }
    lambda_extension::NextEvent::Invoke(_e) => {
      // do something with the invoke event
      // log::info!("Invoke event received");
    }
  }
  Ok(())
}

pub(crate) async fn my_handler(
  event: LambdaEvent<messages::WaiterRequest>,
  options: &Options,
) -> std::result::Result<messages::WaiterResponse, lambda_runtime::Error> {
  // extract some useful info from the request
  let lambda_id = event.payload.id;
  let channel_count = event.payload.number_of_channels;
  let dispatcher_url = event.payload.dispatcher_url;

  // prepare the response
  let resp = messages::WaiterResponse {
    id: lambda_id.to_string(),
  };

  if event.payload.init_only {
    log::info!(
      "LambdaId: {} - Returning from init-only request",
      lambda_id.clone()
    );
    return Ok(resp);
  }

  // If the sent_time is more than a second old, just return
  // This is mostly needed locally where requests get stuck in the queue
  let sent_time = chrono::DateTime::parse_from_rfc3339(&event.payload.sent_time).unwrap();
  if sent_time.timestamp_millis() < (current_time_millis() - 5000).try_into().unwrap() {
    log::info!(
      "LambdaId: {} - Returning from stale request",
      lambda_id.clone()
    );
    return Ok(resp);
  }

  log::info!(
    "LambdaId: {}, Timeout: {}s - Invoked",
    lambda_id,
    (event.context.deadline - current_time_millis()) / 1000
  );
  let mut deadline_ms = event.context.deadline;
  if (deadline_ms - current_time_millis()) > 15 * 60 * 1000 {
    log::warn!("Deadline is greater than 15 minutes, trimming to 1 minute");
    deadline_ms = current_time_millis() + 60 * 1000;
  }
  // check if env var is set to force deadline for testing
  if let Ok(force_deadline_secs) = std::env::var("LAMBDA_DISPATCH_FORCE_DEADLINE") {
    log::warn!("Forcing deadline to {} seconds", force_deadline_secs);
    let force_deadline_secs: u64 = force_deadline_secs.parse().unwrap();
    deadline_ms = current_time_millis() + force_deadline_secs * 1000;
  }

  // run until we get a GoAway
  run::run(
    lambda_id.clone(),
    channel_count,
    dispatcher_url.parse().unwrap(),
    deadline_ms,
    options,
  )
  .await?;

  Ok(resp)
}
