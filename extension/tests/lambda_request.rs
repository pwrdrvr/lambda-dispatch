use axum::{
  extract::Path,
  response::Response,
  routing::{get, post},
  Router,
};
use axum_extra::body::AsyncReadBody;
use futures::stream::StreamExt;
use httpmock::{Method::GET, MockServer};
use hyper::StatusCode;
use std::sync::Arc;
use tokio::io::AsyncWriteExt;

mod support;
use support::http2_server::run_http2_app;

use extension::{
  app_client::create_app_client,
  endpoint::{Endpoint, Scheme},
  lambda_request::*,
  messages::ExitReason,
  router_client::create_router_client,
  time::current_time_millis,
};

#[tokio::test]
async fn test_lambda_request_router_blackhole() {
  let mut lambda_request = LambdaRequest::new(
    Endpoint::new(Scheme::Http, "192.0.2.0", 12345),
    false,
    "pool_id".into(),
    "lambda_id".into(),
    1,
    Endpoint::new(Scheme::Http, "192.0.2.0", 54321),
    current_time_millis() + 60 * 1000,
    250,
  );

  let app_client = create_app_client();
  let router_client = create_router_client();

  // Act
  let start = std::time::Instant::now();
  let result = lambda_request
    .start(app_client.clone(), router_client.clone())
    .await;
  let duration = std::time::Instant::now().duration_since(start);

  // Assert
  if let Ok(exit_reason) = result {
    assert_eq!(
      exit_reason,
      ExitReason::RouterUnreachable,
      "Expected LambdaRequestError::RouterUnreachable"
    );
  } else {
    assert!(false, "Expected Ok with ExitReason");
  }
  assert!(
    duration > std::time::Duration::from_millis(500),
    "Connection should take at least 500 ms, took: {:?}",
    duration
  );
  assert!(
    duration <= std::time::Duration::from_secs(2),
    "Connection should take at most 2 seconds, took: {:?}",
    duration
  );
}

#[tokio::test]
async fn test_lambda_request_router_connects_ping_panics() {
  // Start router server
  let (release_request_tx, release_request_rx) = tokio::sync::mpsc::channel::<()>(1);
  let release_request_rx = Arc::new(tokio::sync::Mutex::new(release_request_rx));
  // Use an arc int to count how many times the request endpoint was called
  let request_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let request_count_clone = Arc::clone(&request_count);
  let ping_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let ping_count_clone = Arc::clone(&ping_count);
  let close_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let close_count_clone = Arc::clone(&close_count);
  let app = Router::new()
      .route(
        "/api/chunked/request/:lambda_id/:channel_id",
        post(
          move |Path((_lambda_id, _channel_id)): Path<(String, String)>,
              request: axum::extract::Request| {
          let request_count = Arc::clone(&request_count_clone);
          let release_request_rx = Arc::clone(&release_request_rx);

          async move {
            // Spawn a task to write to the stream
            tokio::spawn(async move {
              let parts = request.into_parts();

              parts
                .1
                .into_data_stream()
                .for_each(|chunk| async {
                  let _chunk = chunk.unwrap();
                  // println!("Chunk: {:?}", chunk);
                })
                .await;
            });

            // Increment the request count
            request_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);

            // Bail after 1st request
            if request_count.load(std::sync::atomic::Ordering::SeqCst) > 1 {
              let body = AsyncReadBody::new(tokio::io::empty());
              let response = Response::builder()
                .status(StatusCode::CONFLICT)
                .body(body)
                .unwrap();

              return response;
            }

            // Create a channel for the stream
            let (mut tx, rx) = tokio::io::duplex(65_536);

            // Spawn a task to write to the stream
            tokio::spawn(async move {
              //
              // Tell extension to GOAWAY
              //
              let data = b"GET /bananas HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n";
              tx.write_all(data).await.unwrap();

              // Write the rest of the headers
              let data = b"Test-Headers: bar\r\nTest-Headerss: baz\r\nAccept-Encoding: gzip\r\n\r\nHELLO WORLD";
              tx.write_all(data).await.unwrap();

              // Wait for the release
              release_request_rx.lock().await.recv().await;

              // Close the body stream
              tx.shutdown().await.unwrap();
            });

            let body = AsyncReadBody::new(rx);

            let response = Response::builder()
              .header("content-type", "application/octet-stream")
              .body(body)
              .unwrap();

            response
          }
        },
        ),
      )
      .route(
        "/api/chunked/ping/:lambda_id",
        get(|Path(lambda_id): Path<String>| async move {
          let ping_count = Arc::clone(&ping_count_clone);
          // Increment
          ping_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);

          // Panic so the stream closes
          panic!("Ping! LambdaID: {}", lambda_id);
        }),
      )
      .route(
        "/api/chunked/close/:lambda_id",
        get(|Path(lambda_id): Path<String>| async move {
          let close_count = Arc::clone(&close_count_clone);
          // Increment
          close_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
          log::info!("Close! LambdaID: {}", lambda_id);
          format!("Close! LambdaID: {}", lambda_id)
        }),
      );

  let mock_router_server = run_http2_app(app);
  let mock_router_endpoint =
    Endpoint::new(Scheme::Http, "127.0.0.1", mock_router_server.addr.port());

  // Start app server
  let mock_app_server = MockServer::start();
  let mock_app_healthcheck = mock_app_server.mock(|when, then| {
    when.method(GET).path("/health");
    then.status(200).body("OK");
  });
  let mock_app_bananas = mock_app_server.mock(|when, then| {
    when.method(GET).path("/bananas");
    then
      .status(200)
      .header("Content-Type", "text/plain")
      .header("Connection", "close")
      .header("Keep-Alive", "timeout=5")
      .body("Bananas");
  });
  let app_endpoint: Endpoint = mock_app_server.base_url().parse().unwrap();

  let mut lambda_request = LambdaRequest::new(
    app_endpoint,
    false,
    "pool_id".into(),
    "lambda_id".into(),
    1,
    mock_router_endpoint,
    current_time_millis() + 60 * 1000,
    250,
  );

  // Blow up the mock router server
  // Release the request after a few seconds
  tokio::spawn(async move {
    tokio::time::sleep(tokio::time::Duration::from_secs(6)).await;
    release_request_tx.send(()).await.unwrap();
  });

  let app_client = create_app_client();
  let router_client = create_router_client();

  // Act
  let start = std::time::Instant::now();
  let result = lambda_request.start(app_client, router_client).await;
  let duration = std::time::Instant::now().duration_since(start);

  // Assert
  match result {
    Ok(exit_reason) => {
      assert_eq!(exit_reason, ExitReason::RouterConnectionError);
    }
    Err(err) => {
      assert!(false, "Expected Ok with ExitReason, got Err: {:?}", err);
    }
  }
  assert!(
    duration > std::time::Duration::from_secs(6),
    "Connection should take at least 6 seconds"
  );
  assert!(
    duration <= std::time::Duration::from_secs(7),
    "Connection should take at most 7 seconds"
  );

  // Healthcheck not called
  mock_app_healthcheck.assert_hits(0);
  // Bananas called once
  mock_app_bananas.assert_hits(1);
}
