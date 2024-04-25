use rand::Rng;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::Arc;

use rand::SeedableRng;
use tokio::{
  io::{AsyncRead, AsyncWrite},
  net::TcpStream,
};
use tokio_rustls::client::TlsStream;

use crate::endpoint::Endpoint;
use crate::lambda_request_error::LambdaRequestError;
use crate::ping;
use crate::prelude::*;
use crate::router_channel::RouterChannel;
use crate::time::current_time_millis;
use crate::{connect_to_router, messages};

// Define a Stream trait that both TlsStream and TcpStream implement
pub trait Stream: AsyncRead + AsyncWrite + Send {}
impl Stream for TlsStream<TcpStream> {}
impl Stream for TcpStream {}

/// A `LambdaRequest` handles connecting back to the router, picking up requests, sending ping
/// requests to the router, and sending the requests to the contained app When an invoke completes
/// this is torn down completely
#[derive(Debug, Clone)]
pub struct LambdaRequest {
  app_endpoint: Endpoint,
  compression: bool,
  pool_id: PoolId,
  lambda_id: LambdaId,
  channel_count: u8,
  router_endpoint: Endpoint,
  cancel_token: tokio_util::sync::CancellationToken,
  deadline_ms: u64,
  goaway_received: Arc<AtomicBool>,
  last_active: Arc<AtomicU64>,
  rng: rand::rngs::StdRng,
  requests_in_flight: Arc<AtomicUsize>,
  pub count: Arc<AtomicUsize>,
  start_time: u64,
}
impl LambdaRequest {
  /// Create a new `LambdaRequest` task with a specified deadline.
  ///
  /// # Parameters
  ///
  /// * `deadline_ms`: A timestamp in milliseconds since the Unix epoch representing when the
  /// Lambda function needs to finish execution.
  pub fn new(
    app_endpoint: Endpoint,
    compression: bool,
    pool_id: PoolId,
    lambda_id: LambdaId,
    channel_count: u8,
    router_endpoint: Endpoint,
    deadline_ms: u64,
  ) -> Self {
    LambdaRequest {
      count: Arc::new(AtomicUsize::new(0)),
      app_endpoint,
      compression,
      pool_id,
      lambda_id,
      channel_count,
      router_endpoint,
      cancel_token: tokio_util::sync::CancellationToken::new(),
      deadline_ms,
      goaway_received: Arc::new(AtomicBool::new(false)),
      last_active: Arc::new(AtomicU64::new(0)),
      rng: rand::rngs::StdRng::from_entropy(),
      requests_in_flight: Arc::new(AtomicUsize::new(0)),
      start_time: current_time_millis(),
    }
  }

  /// Executes a task with a specified deadline.
  pub async fn start(&mut self) -> Result<messages::ExitReason, LambdaRequestError> {
    let sender = match connect_to_router::connect_to_router(
      self.router_endpoint.clone(),
      Arc::clone(&self.pool_id),
      Arc::clone(&self.lambda_id),
    )
    .await
    {
      Ok(sender) => sender,
      Err(_) => return Err(LambdaRequestError::RouterUnreachable),
    };

    // Send the ping requests in background
    let ping_task = tokio::task::spawn(ping::send_ping_requests(
      Arc::clone(&self.last_active),
      Arc::clone(&self.goaway_received),
      sender.clone(),
      Arc::clone(&self.pool_id),
      Arc::clone(&self.lambda_id),
      Arc::clone(&self.count),
      self.router_endpoint.clone(),
      self.deadline_ms,
      self.cancel_token.clone(),
      Arc::clone(&self.requests_in_flight),
    ));

    // Startup the request channels
    let channel_futures = (0..self.channel_count)
      .map(|channel_number| {
        let last_active = Arc::clone(&self.last_active);
        // Create a JoinHandle and implicitly return it to be collected in the vector
        let mut router_channel = RouterChannel::new(
          Arc::clone(&self.count),
          self.compression,
          Arc::clone(&self.goaway_received),
          Arc::clone(&last_active),
          Arc::clone(&self.requests_in_flight),
          self.router_endpoint.clone(),
          self.app_endpoint.clone(),
          channel_number,
          sender.clone(),
          Arc::clone(&self.pool_id),
          Arc::clone(&self.lambda_id),
          uuid::Builder::from_random_bytes(self.rng.gen())
            .into_uuid()
            .to_string(),
        );
        let goaway_received = Arc::clone(&self.goaway_received);
        tokio::spawn(async move {
          let result = router_channel.start().await;

          // Tell the other channels to stop
          goaway_received.store(true, Ordering::Release);

          result
        })
      })
      .collect::<Vec<_>>();

    let mut exit_reason = messages::ExitReason::RouterGoAway;

    // `try_join_all` says all futures will be immediately canceled if one of them returns an error
    // However, this "cancelation" is cooperative and has to be checked by the tasks themselves
    // As a result, this just waits for all tasks to complete
    // In addition, `try_join_all` says it will return an error when a future returns an error,
    // but it's returning `Ok` even when there are `Err` in the vector (this may be because
    // try_join_all is looking for an error on the future not the task?)
    match futures::future::try_join_all(channel_futures).await {
      Ok(results) => {
        // All tasks completed successfully
        log::debug!(
          "LambdaId: {} - run - All channel tasks completed successfully",
          self.lambda_id
        );

        for result in results {
          match result {
            Ok(result) => {
              if let Some(result) = result {
                exit_reason = exit_reason.worse(result.into());
              }
            }
            Err(err) => {
              log::error!(
                "LambdaId: {} - run - Error in channel task: {:?}",
                self.lambda_id,
                err
              );

              if err.is_fatal() {
                return Err(err.into());
              }

              // Error is not fatal so just use it as an exit reason
              exit_reason = exit_reason.worse(err.into());
            }
          }
        }
      }
      Err(err) => {
        log::error!(
          "LambdaId: {} - run - Error in futures::future::try_join_all: {:?}",
          self.lambda_id,
          err
        );
        // TODO: Capture the channel exit error and return the worst one we find
        panic!(
          "LambdaId: {} - run - Error in futures::future::try_join_all: {}",
          self.lambda_id, err
        );
      }
    }

    // Wait for the ping loop to exit
    self.cancel_token.cancel();
    match ping_task.await {
      Ok(result) => {
        // Ping task completed successfully

        // If the ping task knows why we exited, use that reason
        if let Some(ping_result) = result {
          if let Some(ping_result) = ping_result.into() {
            exit_reason = exit_reason.worse(ping_result);
          }
        }
      }
      Err(e) => {
        log::error!(
          "LambdaId: {} - run - Error in ping task: {:?}",
          self.lambda_id,
          e
        );
        // We'll lump this in as a generic router connection error
        return Err(LambdaRequestError::RouterUnreachable.into());
      }
    }

    Ok(exit_reason)
  }

  pub fn elapsed(&self) -> u64 {
    current_time_millis() - self.start_time
  }

  pub fn rps(&self) -> f64 {
    self.count.load(Ordering::Acquire) as f64 / (self.elapsed() as f64 / 1000.0)
  }
}

#[cfg(test)]
mod tests {
  use super::*;

  use crate::test_http2_server::test_http2_server::run_http2_app;

  use axum::response::Response;
  use axum::routing::get;
  use axum::{extract::Path, routing::post, Router};
  use axum_extra::body::AsyncReadBody;
  use futures::stream::StreamExt;
  use httpmock::Method::GET;
  use httpmock::MockServer;
  use hyper::StatusCode;
  use tokio::io::AsyncWriteExt;

  #[tokio::test]
  async fn test_lambda_request_router_blackhole() {
    let mut lambda_request = LambdaRequest::new(
      Endpoint::new(crate::endpoint::Scheme::Http, "192.0.2.0", 12345),
      false,
      "pool_id".into(),
      "lambda_id".into(),
      1,
      Endpoint::new(crate::endpoint::Scheme::Http, "192.0.2.0", 54321),
      current_time_millis() + 60 * 1000,
    );

    // Act
    let start = std::time::Instant::now();
    let result = lambda_request.start().await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    if let Err(err) = result {
      assert_eq!(
        err,
        LambdaRequestError::RouterUnreachable,
        "Expected LambdaRequestError::RouterUnreachable"
      );
    } else {
      assert!(false, "Expected an error result");
    }
    assert!(
      duration > std::time::Duration::from_secs(5),
      "Connection should take at least 5 seconds"
    );
    assert!(
      duration <= std::time::Duration::from_secs(6),
      "Connection should take at most 6 seconds"
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
    let mock_router_endpoint = Endpoint::new(
      crate::endpoint::Scheme::Http,
      "127.0.0.1",
      mock_router_server.addr.port(),
    );

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
    );

    // Blow up the mock router server
    // Release the request after a few seconds
    tokio::spawn(async move {
      tokio::time::sleep(tokio::time::Duration::from_secs(6)).await;
      release_request_tx.send(()).await.unwrap();
    });

    // Act
    let start = std::time::Instant::now();
    let result = lambda_request.start().await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    match result {
      Ok(exit_reason) => {
        assert_eq!(exit_reason, messages::ExitReason::RouterConnectionError);
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
}
