#![warn(rust_2018_idioms)]

use std::process;
use std::sync::atomic::AtomicBool;
use std::sync::Arc;

use lambda_runtime::LambdaEvent;
use tokio::signal::unix::{signal, SignalKind};

mod app_request;
mod app_start;
mod cert;
mod messages;
mod ping;
mod run;
mod threads;
mod time;

fn main() -> anyhow::Result<()> {
  // required to enable CloudWatch error logging by the runtime
  tracing_subscriber::fmt()
    .with_max_level(tracing::Level::INFO)
    // disable printing the name of the module in every log line.
    .with_target(false)
    // disabling time is handy because CloudWatch will add the ingestion time.
    .without_time()
    .init();

  // If LAMBDA_DISPATCH_RUNTIME=default_multi_thread, use a multi-threaded runtime, let tokio decide how many threads
  // If LAMBDA_DISPATCH_RUNTIME=multi_thread, use a multi-threaded runtime,
  //   but use 2 threads or whatever is set in TOKIO_WORKER_THREADS
  // If LAMBDA_DISPATCH_RUNTIME=current_thread, use a single-threaded runtime
  // If LAMBDA_DISPATCH_RUNTIME is not set, use a single-threaded runtime
  // Get the var
  let runtime_var = std::env::var("LAMBDA_DISPATCH_RUNTIME");
  match runtime_var {
    Ok(runtime) if runtime == "multi_thread" => {
      println!("Using multi_thread runtime");
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
      runtime.block_on(async_main())?;
    }
    Ok(runtime) if runtime == "default_multi_thread" => {
      println!("Using default_multi_thread runtime");
      // Let tokio decide how many threads to use
      let runtime = tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
        .unwrap();
      runtime.block_on(async_main())?;
    }
    _ => {
      println!("Using current_thread runtime");
      let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
      runtime.block_on(async_main())?;
    }
  }

  Ok(())
}

async fn async_main() -> anyhow::Result<()> {
  let mut term_signal = signal(SignalKind::terminate())?;

  let thread_count = threads::get_threads();
  println!("PID: {}, Thread count: {}", process::id(), thread_count);

  // Span a task to handle the extension
  // DO NOT await this task, otherwise the lambda will not be invoked
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
        println!("Extension exited");
      }
      Err(err) => {
        println!("Extension.run error: {:?}", err)
      }
    }
  });

  // Wait for the contained app to be ready
  app_start::health_check_contained_app(Arc::new(AtomicBool::new(false))).await;

  let func = lambda_runtime::service_fn(my_handler);
  tokio::select! {
      lambda_result = lambda_runtime::run(func) => {
          match lambda_result {
              Ok(_) => {}
              Err(e) => {
                  println!("Error: {}", e);
              }
          }
      }
      _ = term_signal.recv() => {
          println!("SIGTERM received, stopping...");
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
    }
    lambda_extension::NextEvent::Invoke(_e) => {
      // do something with the invoke event
    }
  }
  Ok(())
}

pub(crate) async fn my_handler(
  event: LambdaEvent<messages::WaiterRequest>,
) -> std::result::Result<messages::WaiterResponse, lambda_runtime::Error> {
  // extract some useful info from the request
  let lambda_id = event.payload.id;
  let channel_count = event.payload.number_of_channels;
  let dispatcher_url = event.payload.dispatcher_url;

  // prepare the response
  let resp = messages::WaiterResponse {
    id: lambda_id.to_string(),
  };

  println!("LambdaId: {} - Invoked", lambda_id);

  // run until we get a GoAway
  run::run(
    lambda_id.clone(),
    channel_count,
    dispatcher_url.parse().unwrap(),
    event.context.deadline,
  )
  .await?;

  println!("LambdaId: {} - Returning from invoke", lambda_id.clone());
  Ok(resp)
}
