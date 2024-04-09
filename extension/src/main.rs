#![warn(rust_2018_idioms)]

use std::process;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

use env_logger;
use hyper::Uri;
use std::io::Write;
use tokio::signal::unix::{signal, SignalKind};
use tokio::time::timeout;

use crate::lambda_service::LambdaService;
use crate::options::{Options, Runtime};
use crate::prelude::*;

mod app_request;
mod app_start;
mod cert;
mod counter_drop;
mod lambda_request;
mod lambda_service;
mod messages;
mod options;
mod ping;
pub mod prelude;
mod router_channel;
mod threads;
mod time;

fn main() -> Result<()> {
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
      runtime.block_on(async_main(options))?;
    }
    Runtime::DefaultMultiThread => {
      log::info!("Using default_multi_thread runtime");
      // Let tokio decide how many threads to use
      let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .unwrap();
      runtime.block_on(async_main(options))?;
    }
    // Default or `current_thread` runtime
    Runtime::CurrentThread => {
      log::info!("Using current_thread runtime");
      let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
      runtime.block_on(async_main(options))?;
    }
  }

  Ok(())
}

async fn async_main(options: Options) -> Result<()> {
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

  let healthcheck_url: Uri = format!("http://127.0.0.1:{}/health", options.port)
    .parse()
    .expect("healthcheck url with port should always be valid");

  // Wait for the contained app to be ready
  let initialized = if options.async_init {
    let goaway_received = Arc::new(AtomicBool::new(false));
    let result = timeout(
      std::time::Duration::from_millis(9500),
      app_start::health_check_contained_app(Arc::clone(&goaway_received), &healthcheck_url),
    )
    .await;

    match result {
      Ok(success) => {
        if success == false {
          log::info!("Health check - returned false before timeout, deferring init to handler");
          goaway_received.store(true, Ordering::SeqCst);
        }
        success
      }
      Err(_) => {
        log::info!("Health check - not ready before timeout, deferring init to handler");
        false
      }
    }
  } else {
    // Wait until init finishes OR the 10 second init gets canceled and re-run as a regular request
    app_start::health_check_contained_app(Arc::new(AtomicBool::new(false)), &healthcheck_url).await
  };

  let svc = LambdaService::new(
    options,
    Arc::new(AtomicBool::new(initialized)),
    healthcheck_url,
  );

  tokio::select! {
      lambda_result = lambda_runtime::run(svc) => {
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
