use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;
use std::{pin::Pin, sync::Arc};

use futures::Future;
use hyper::Uri;
use lambda_runtime::LambdaEvent;
use tokio::io::{AsyncRead, AsyncWrite};
use tokio::net::TcpStream;
use tokio::time::timeout;
use tower::Service;

use crate::endpoint::{Endpoint, Scheme};
use crate::lambda_request::LambdaRequest;
use crate::lambda_request_error::LambdaRequestError;
use crate::messages::ExitReason;
use crate::options::Options;
use crate::prelude::*;
use crate::time::current_time_millis;
use crate::{
  app_start,
  messages::{WaiterRequest, WaiterResponse},
};

use tokio_rustls::client::TlsStream;

// Define a Stream trait that both TlsStream and TcpStream implement
pub trait Stream: AsyncRead + AsyncWrite + Send {}
impl Stream for TlsStream<TcpStream> {}
impl Stream for TcpStream {}

#[derive(Clone)]
pub struct LambdaService {
  options: Options,
  initialized: Arc<AtomicBool>,
  healthcheck_url: Uri,
}

impl LambdaService {
  pub fn new(options: Options, initialized: Arc<AtomicBool>, healthcheck_url: Uri) -> Self {
    LambdaService {
      options,
      initialized,
      healthcheck_url,
    }
  }

  //
  // This is called by the Tower.Service trait impl below
  //
  async fn fetch_response(
    &self,
    event: LambdaEvent<WaiterRequest>,
  ) -> Result<WaiterResponse, Error> {
    let start_time = current_time_millis();
    // extract some useful info from the request
    let pool_id: PoolId = event
      .payload
      .pool_id
      .unwrap_or_else(|| "default".to_string())
      .into();
    let lambda_id: LambdaId = event.payload.id.into();
    let channel_count: u8 = event.payload.number_of_channels;
    let router_endpoint = event.payload.router_url.parse()?;

    log::info!(
      "PoolId: {}, LambdaId: {} - Received request",
      pool_id,
      lambda_id
    );

    let app_endpoint = Endpoint::new(Scheme::Http, "127.0.0.1", self.options.port);

    if !self.initialized.load(Ordering::SeqCst) {
      let fake_goaway_received = Arc::new(AtomicBool::new(false));
      let result = timeout(
        self.options.async_init_timeout,
        app_start::health_check_contained_app(
          Arc::clone(&fake_goaway_received),
          &self.healthcheck_url,
        ),
      )
      .await;

      match result {
        Ok(success) => {
          if !success {
            log::error!(
              "PoolId: {}, LambdaId: {} - Async init Health check returned false before timeout, bailing",
              pool_id,
              lambda_id
            );

            // goaway_received is a private var above, it can't be set to false
            // so this can never happen
            panic!("Health check returned false before timeout");
          }
          self.initialized.store(success, Ordering::SeqCst);
        }
        Err(_) => {
          log::error!(
            "PoolId: {}, LambdaId: {} - Async init Health check not ready before timeout, bailing",
            pool_id,
            lambda_id
          );

          // Set goaway_received to true to the loop will exit
          fake_goaway_received.store(true, Ordering::SeqCst);

          // We'll panic in the tower service if this happens
          return Err(anyhow::anyhow!(
            "Health check returned false before timeout"
          ));
        }
      }

      self.initialized.store(true, Ordering::SeqCst);
    }

    // prepare the response
    let mut resp = WaiterResponse::new(pool_id.to_string(), lambda_id.to_string());

    if event.payload.init_only {
      log::info!(
        "PoolId: {}, LambdaId: {} - Returning from init-only request",
        pool_id,
        lambda_id
      );
      resp.invoke_duration = current_time_millis() - start_time;
      resp.exit_reason = ExitReason::SelfInitOnly;
      return Ok(resp);
    }

    // If the sent_time is more than 5 seconds old, just return
    // This is mostly needed locally where requests get stuck in the queue
    // Do not do this in a deployed env because an app that takes > 5 seconds to start
    // will get much longer initial request times
    let sent_time = chrono::DateTime::parse_from_rfc3339(&event.payload.sent_time)
      .context("unable to parse sent_time in lambda event payload")?;
    if self.options.local_env
      && sent_time.timestamp_millis() < (current_time_millis() - 5000).try_into().unwrap()
    {
      log::info!(
        "PoolId: {}, LambdaId: {} - Returning from stale request",
        pool_id,
        lambda_id
      );
      resp.invoke_duration = current_time_millis() - start_time;
      resp.exit_reason = ExitReason::SelfStaleRequest;
      return Ok(resp);
    }

    log::info!(
      "PoolId: {}, LambdaId: {}, Timeout: {}s - Invoked",
      pool_id,
      lambda_id,
      (event.context.deadline - current_time_millis()) / 1000
    );
    let mut deadline_ms = event.context.deadline;
    if (deadline_ms - current_time_millis()) > 15 * 60 * 1000 {
      log::warn!("Deadline is greater than 15 minutes, trimming to 1 minute");
      deadline_ms = current_time_millis() + 60 * 1000;
    }
    // check if env var is set to force deadline for testing
    if let Some(force_deadline_secs) = self.options.force_deadline_secs {
      if force_deadline_secs > Duration::from_secs(0) {
        log::warn!(
          "Forcing deadline to {} seconds",
          force_deadline_secs.as_secs()
        );
        deadline_ms = current_time_millis() + force_deadline_secs.as_millis() as u64;
      }
    }

    // run until we get a GoAway or deadline is about to be reached
    let mut lambda_request = LambdaRequest::new(
      app_endpoint,
      self.options.compression,
      Arc::clone(&pool_id),
      Arc::clone(&lambda_id),
      channel_count,
      router_endpoint,
      deadline_ms,
    );

    //
    // This is the main loop that runs until the deadline is about to be reached
    //
    let result = lambda_request.start().await;
    match result {
      Ok(exit_reason) => {
        log::info!(
          "PoolId: {}, LambdaId: {} - Lambda request completed, exit reason: {:?}",
          pool_id,
          lambda_id,
          exit_reason
        );

        resp.exit_reason = exit_reason;
      }
      Err(e) => {
        log::error!(
          "PoolId: {}, LambdaId: {} - Lambda request failed: {}",
          pool_id,
          lambda_id,
          e
        );

        if let Some(lambda_err) = e.downcast_ref::<LambdaRequestError>() {
          resp.exit_reason = lambda_err.into();
        } else {
          // Lump this in as a RouterConnectionError too?
          // TODO: This might be a case where we want to exit
          resp.exit_reason = ExitReason::RouterConnectionError;
        }
      }
    }

    // Print final stats
    log::info!(
      "LambdaId: {}, Requests: {}, Elapsed: {} ms, RPS: {} - Returning from run",
      lambda_id,
      lambda_request.count.load(Ordering::Acquire),
      lambda_request.elapsed(),
      lambda_request.rps()
    );

    resp.invoke_duration = current_time_millis() - start_time;
    resp.request_count = lambda_request.count.load(Ordering::Acquire) as u64;
    Ok(resp)
  }
}

// Tower.Service is the interface required by lambda_runtime::run
impl Service<LambdaEvent<WaiterRequest>> for LambdaService {
  type Response = WaiterResponse;
  type Error = Error;
  type Future = Pin<Box<dyn Future<Output = Result<Self::Response, Self::Error>> + Send>>;

  fn poll_ready(
    &mut self,
    _cx: &mut core::task::Context<'_>,
  ) -> core::task::Poll<Result<(), Self::Error>> {
    core::task::Poll::Ready(Ok(()))
  }

  fn call(&mut self, event: LambdaEvent<WaiterRequest>) -> Self::Future {
    let adapter = self.clone();
    Box::pin(async move {
      match adapter.fetch_response(event).await {
        Ok(response) => Ok(response),
        Err(e) => {
          panic!("Error fetching response: {}", e);
        }
      }
    })
  }
}

#[cfg(test)]
mod tests {
  use super::*;

  use crate::{messages, test_http2_server::test_http2_server::run_http2_app, test_mock_router};
  use axum::{
    extract::Path,
    routing::{get, post},
    Router,
  };
  use futures::task::noop_waker;
  use httpmock::{Method::GET, MockServer};
  use tokio_test::assert_ok;

  #[tokio::test]
  async fn test_lambda_service_call() {
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: "http://localhost:54321".to_string(),
      number_of_channels: 1,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: true,
    };
    let mut context = lambda_runtime::Context::default();
    context.deadline = current_time_millis() + 60 * 1000;
    let event = LambdaEvent {
      payload: request,
      context,
    };

    // Create the service
    let options = Options::default();
    let initialized = true;

    let mut service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      "localhost:54321".parse().unwrap(),
    );

    // Ensure the service is ready
    let waker = noop_waker();
    let mut context = core::task::Context::from_waker(&waker);
    let _ = service.poll_ready(&mut context);

    // Call the service with the mock request
    let result = service.call(event).await;

    // Assert that the response is as expected
    let response = assert_ok!(result);
    assert_eq!(
      response,
      WaiterResponse {
        pool_id: "test_pool".to_string(),
        id: "test_id".to_string(),
        request_count: 0,
        invoke_duration: 0,
        exit_reason: ExitReason::SelfInitOnly,
      }
    );
  }

  #[tokio::test]
  async fn test_lambda_service_fetch_response_not_initialized_healthcheck_200_ok() {
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
          move |Path((lambda_id, channel_id)): Path<(String, String)>| {
            let request_count = Arc::clone(&request_count_clone);
            let release_request_rx = Arc::clone(&release_request_rx);
            async move {
              log::info!(
                "Request! LambdaID: {}, ChannelID: {}",
                lambda_id,
                channel_id
              );

              // Increment the request count
              request_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);

              // Wait for the release signal
              let mut release_request_rx = release_request_rx.lock().await;
              release_request_rx.recv().await.unwrap();
              format!(
                "Request! LambdaID: {}, ChannelID: {}",
                lambda_id, channel_id
              )
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
          log::info!("Ping! LambdaID: {}", lambda_id);
          format!("Ping! LambdaID: {}", lambda_id)
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

    log::info!(
      "Router server running on port: {}",
      mock_router_server.addr.port()
    );

    // Start app server
    let mock_app_server = MockServer::start();
    let mock_app_healthcheck = mock_app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });

    let mut options = Options::default();
    // Tell the service the port of the mock contained app
    options.port = mock_app_server.address().port();
    let initialized = false;

    let mock_app_healthcheck_url: Uri = format!("{}/health", mock_app_server.base_url())
      .parse()
      .unwrap();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      mock_app_healthcheck_url,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://localhost:{}", mock_router_server.addr.port()),
      number_of_channels: 1,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: false,
    };
    let mut context = lambda_runtime::Context::default();
    context.deadline = current_time_millis() + 60 * 1000;
    let event = LambdaEvent {
      payload: request,
      context,
    };

    // Release the request after a few seconds
    tokio::spawn(async move {
      tokio::time::sleep(tokio::time::Duration::from_secs(1)).await;
      release_request_tx.send(()).await.unwrap();
    });

    // Act
    // It should not be possible to get past this unless the service
    // has decided to exit itself
    let response = service.fetch_response(event).await;

    // Assert
    // TODO: This should succeed but return an error indicating the router should backoff
    // The reason is the response from the channel request is invalid: a 200 OK with no response body
    assert!(
      response.is_ok(),
      "fetch_response failed: {:?}",
      response.err()
    );
    // Get the WaiterResponse out of the response
    let resp = response.unwrap();
    assert_eq!(resp.id, "test_id", "Lambda ID");
    assert_eq!(resp.pool_id, "test_pool", "Pool ID");
    assert_eq!(
      request_count.load(std::sync::atomic::Ordering::SeqCst),
      1,
      "Request count"
    );
    // The ping count will be 0 because we do not start the response stream and leave
    // it open, which allows the pings to start
    assert_eq!(
      ping_count.load(std::sync::atomic::Ordering::SeqCst),
      0,
      "Ping count"
    );
    // The close count will be 0 because the router response indicated we should
    // close so we do not need to call back asking to close
    assert_eq!(
      close_count.load(std::sync::atomic::Ordering::SeqCst),
      0,
      "Close count"
    );

    // Assert app server's healthcheck endpoint got called
    mock_app_healthcheck.assert();
  }

  #[tokio::test]
  async fn test_lambda_service_fetch_response_spillover_healthcheck_blackhole_timeout() {
    let mut options = Options::default();
    options.async_init_timeout = std::time::Duration::from_millis(1000);
    let initialized = false;

    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      // 192.0.2.0/24 (TEST-NET-1)
      "http://192.0.2.0:54321/health".parse().unwrap(),
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      // 192.0.2.0/24 (TEST-NET-1)
      router_url: format!("http://192.0.2.0:{}", 12345),
      number_of_channels: 1,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: false,
    };
    let mut context = lambda_runtime::Context::default();
    context.deadline = current_time_millis() + 60 * 1000;
    let event = LambdaEvent {
      payload: request,
      context,
    };

    // Act
    let start = std::time::Instant::now();
    let response = service.fetch_response(event).await;
    let duration = std::time::Instant::now().duration_since(start);

    assert!(response.is_err(), "fetch_response should have failed",);
    assert!(
      duration >= std::time::Duration::from_secs(1),
      "Connection should take at least 1 seconds"
    );
    assert!(
      duration <= std::time::Duration::from_secs(2),
      "Connection should take at most 2 seconds"
    );
  }

  #[tokio::test]
  async fn test_lambda_service_fetch_response_local_env_stale_request_reject() {
    let mut options = Options::default();
    options.local_env = true;
    let initialized = true;

    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      // 192.0.2.0/24 (TEST-NET-1)
      "http://192.0.2.0:54321/health".parse().unwrap(),
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      // 192.0.2.0/24 (TEST-NET-1)
      router_url: format!("http://192.0.2.0:{}", 12345),
      number_of_channels: 1,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: false,
    };
    let mut context = lambda_runtime::Context::default();
    context.deadline = current_time_millis() + 60 * 1000;
    let event = LambdaEvent {
      payload: request,
      context,
    };

    // Act
    let start = std::time::Instant::now();
    let response = service.fetch_response(event).await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    // This only succeeds because we're rejecting the request without connecting to the router
    assert_ok!(response, "fetch_response success",);
    assert!(
      duration <= std::time::Duration::from_secs(1),
      "Connection should take at most 1 seconds"
    );
  }

  #[tokio::test]
  async fn test_lambda_service_fetch_response_init_only_request_already_inited() {
    let options = Options::default();
    let initialized = true;

    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      // 192.0.2.0/24 (TEST-NET-1)
      "http://192.0.2.0:54321/health".parse().unwrap(),
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      // 192.0.2.0/24 (TEST-NET-1)
      router_url: format!("http://192.0.2.0:{}", 12345),
      number_of_channels: 1,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: true,
    };
    let mut context = lambda_runtime::Context::default();
    context.deadline = current_time_millis() + 60 * 1000;
    let event = LambdaEvent {
      payload: request,
      context,
    };

    // Act
    let start = std::time::Instant::now();
    let response = service.fetch_response(event).await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    // This only succeeds because we're rejecting the request without connecting to the router
    assert_ok!(response, "fetch_response success");
    assert!(
      duration <= std::time::Duration::from_secs(1),
      "Connection should take at most 1 seconds"
    );
  }

  #[tokio::test]
  async fn test_lambda_service_request_already_inited_router_blackhole() {
    let mut options = Options::default();
    options.force_deadline_secs = Some(std::time::Duration::from_secs(15));
    let initialized = true;

    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      // 192.0.2.0/24 (TEST-NET-1)
      "http://192.0.2.0:54321/health".parse().unwrap(),
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      // 192.0.2.0/24 (TEST-NET-1)
      router_url: format!("http://192.0.2.0:{}", 12345),
      number_of_channels: 1,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: false,
    };
    let mut context = lambda_runtime::Context::default();
    // Test an overly large value to exercise trimming code
    context.deadline = current_time_millis() + 60 * 1000 * 1000;
    let event = LambdaEvent {
      payload: request,
      context,
    };

    // Act
    let start = std::time::Instant::now();
    let response = service.fetch_response(event).await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    assert!(response.is_ok(), "fetch_response should succeed");
    match response {
      Ok(waiter_response) => {
        assert_eq!(
          waiter_response.exit_reason,
          messages::ExitReason::RouterUnreachable,
        );
      }
      Err(err) => {
        assert!(false, "Expected Ok with ExitReason, got Err: {:?}", err);
      }
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
    let mock_router_server = test_mock_router::test_mock_router::setup_router(
      test_mock_router::test_mock_router::RouterParams {
        channel_conflict_after_count: 1,
        channel_panic_response_from_extension_on_count: -1,
        channel_panic_request_to_extension_before_start: false,
        channel_panic_request_to_extension_after_start: false,
        channel_panic_request_to_extension_before_close: false,
        ping_panic_after_count: 0,
      },
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

    // Blow up the mock router server
    // Release the request after a few seconds
    tokio::spawn(async move {
      tokio::time::sleep(tokio::time::Duration::from_secs(6)).await;
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let mut options = Options::default();

    options.port = mock_app_server.address().port();
    let initialized = true;

    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.base_url())
        .parse()
        .unwrap(),
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!(
        "http://127.0.0.1:{}",
        mock_router_server.mock_router_server.addr.port()
      ),
      number_of_channels: 1,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: false,
    };
    let mut context = lambda_runtime::Context::default();
    // Test an overly large value to exercise trimming code
    context.deadline = current_time_millis() + 60 * 1000;
    let event = LambdaEvent {
      payload: request,
      context,
    };

    // Act
    let start = std::time::Instant::now();
    let response = service.fetch_response(event).await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    assert!(response.is_ok(), "fetch_response should succeed");
    match response {
      Ok(waiter_response) => {
        assert_eq!(
          waiter_response.exit_reason,
          messages::ExitReason::RouterConnectionError,
        );
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

  // This test is reporting that the app connection has errored
  #[tokio::test]
  #[ignore = "Issue-178 - This test fails because we don't try to send a close to the router or configure a timeout if the ping loop exits"]
  async fn test_lambda_request_router_connects_ping_panics_channel_stays_open() {
    // Start router server
    let mock_router_server = test_mock_router::test_mock_router::setup_router(
      test_mock_router::test_mock_router::RouterParams {
        channel_conflict_after_count: -1,
        channel_panic_response_from_extension_on_count: -1,
        channel_panic_request_to_extension_before_start: false,
        channel_panic_request_to_extension_after_start: false,
        channel_panic_request_to_extension_before_close: false,
        ping_panic_after_count: 0,
      },
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
        .body("Bananas");
    });

    // Blow up the mock router server
    // Release the request after a few seconds
    tokio::spawn(async move {
      tokio::time::sleep(tokio::time::Duration::from_secs(6)).await;
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let mut options = Options::default();

    options.port = mock_app_server.address().port();
    let initialized = true;

    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.base_url())
        .parse()
        .unwrap(),
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!(
        "http://127.0.0.1:{}",
        mock_router_server.mock_router_server.addr.port()
      ),
      number_of_channels: 1,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: false,
    };
    let mut context = lambda_runtime::Context::default();
    // Test an overly large value to exercise trimming code
    context.deadline = current_time_millis() + 60 * 1000;
    let event = LambdaEvent {
      payload: request,
      context,
    };

    // Act
    let start = std::time::Instant::now();
    let response = service.fetch_response(event).await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    assert!(response.is_ok(), "fetch_response should succeed");
    match response {
      Ok(waiter_response) => {
        assert_eq!(
          waiter_response.exit_reason,
          messages::ExitReason::RouterConnectionError,
        );
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

  #[tokio::test]
  async fn test_lambda_request_loop_100_normal_requests() {
    // Start router server
    let mock_router_server = test_mock_router::test_mock_router::setup_router(
      test_mock_router::test_mock_router::RouterParams {
        channel_conflict_after_count: 100,
        channel_panic_response_from_extension_on_count: -1,
        channel_panic_request_to_extension_before_start: false,
        channel_panic_request_to_extension_after_start: false,
        channel_panic_request_to_extension_before_close: false,
        ping_panic_after_count: -1,
      },
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
        .body("Bananas");
    });

    // Let the router run wild
    tokio::spawn(async move {
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let mut options = Options::default();

    options.port = mock_app_server.address().port();
    let initialized = true;

    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.base_url())
        .parse()
        .unwrap(),
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!(
        "http://127.0.0.1:{}",
        mock_router_server.mock_router_server.addr.port()
      ),
      number_of_channels: 1,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: false,
    };
    let mut context = lambda_runtime::Context::default();
    // Test an overly large value to exercise trimming code
    context.deadline = current_time_millis() + 60 * 1000;
    let event = LambdaEvent {
      payload: request,
      context,
    };

    // Act
    let start = std::time::Instant::now();
    let response = service.fetch_response(event).await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    assert!(response.is_ok(), "fetch_response should succeed");
    match response {
      Ok(waiter_response) => {
        assert_eq!(
          waiter_response.exit_reason,
          messages::ExitReason::RouterGoaway,
        );
        assert_eq!(waiter_response.request_count, 100);
      }
      Err(err) => {
        assert!(false, "Expected Ok with ExitReason, got Err: {:?}", err);
      }
    }
    assert!(
      duration <= std::time::Duration::from_secs(2),
      "Connection should take at most 2 seconds"
    );

    // Healthcheck not called
    mock_app_healthcheck.assert_hits(0);
    // Bananas called 100 times
    mock_app_bananas.assert_hits(100);
  }

  #[tokio::test]
  #[ignore = "Issue-178 - This test fails because we do not re-establish the contained app connections"]
  async fn test_lambda_request_loop_100_requests_contained_app_connection_close_header() {
    // Start router server
    let mock_router_server = test_mock_router::test_mock_router::setup_router(
      test_mock_router::test_mock_router::RouterParams {
        channel_conflict_after_count: 100,
        channel_panic_response_from_extension_on_count: -1,
        channel_panic_request_to_extension_before_start: false,
        channel_panic_request_to_extension_after_start: false,
        channel_panic_request_to_extension_before_close: false,
        ping_panic_after_count: -1,
      },
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
        .body("Bananas");
    });

    // Let the router run wild
    tokio::spawn(async move {
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let mut options = Options::default();

    options.port = mock_app_server.address().port();
    let initialized = true;

    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.base_url())
        .parse()
        .unwrap(),
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!(
        "http://127.0.0.1:{}",
        mock_router_server.mock_router_server.addr.port()
      ),
      number_of_channels: 1,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: false,
    };
    let mut context = lambda_runtime::Context::default();
    // Test an overly large value to exercise trimming code
    context.deadline = current_time_millis() + 60 * 1000;
    let event = LambdaEvent {
      payload: request,
      context,
    };

    // Act
    let start = std::time::Instant::now();
    let response = service.fetch_response(event).await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    assert!(response.is_ok(), "fetch_response should succeed");
    match response {
      Ok(waiter_response) => {
        assert_eq!(
          waiter_response.exit_reason,
          messages::ExitReason::RouterGoaway,
        );
        assert_eq!(waiter_response.request_count, 100);
      }
      Err(err) => {
        assert!(false, "Expected Ok with ExitReason, got Err: {:?}", err);
      }
    }
    assert!(
      duration <= std::time::Duration::from_secs(2),
      "Connection should take at most 2 seconds"
    );

    // Healthcheck not called
    mock_app_healthcheck.assert_hits(0);
    // Bananas called 100 times
    mock_app_bananas.assert_hits(100);
  }

  #[tokio::test]
  #[ignore = "Issue-178 - This test fails because 1 channel exiting does not cause the other channels to exit and because there is no propagation of the error"]
  async fn test_lambda_request_router_connects_channel_request_panics() {
    // Start router server
    let mock_router_server = test_mock_router::test_mock_router::setup_router(
      test_mock_router::test_mock_router::RouterParams {
        // We have 2 channels
        // The 2nd channel should not finish all 100 requests after the 1st channel panics
        channel_conflict_after_count: 100,
        channel_panic_response_from_extension_on_count: 1,
        channel_panic_request_to_extension_before_start: false,
        channel_panic_request_to_extension_after_start: false,
        channel_panic_request_to_extension_before_close: false,
        ping_panic_after_count: -1,
      },
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
        .body("Bananas");
    });

    // Release the request
    tokio::spawn(async move {
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let mut options = Options::default();

    options.port = mock_app_server.address().port();
    let initialized = true;

    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.base_url())
        .parse()
        .unwrap(),
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!(
        "http://127.0.0.1:{}",
        mock_router_server.mock_router_server.addr.port()
      ),
      number_of_channels: 2,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: false,
    };
    let mut context = lambda_runtime::Context::default();
    // Test an overly large value to exercise trimming code
    context.deadline = current_time_millis() + 60 * 1000;
    let event = LambdaEvent {
      payload: request,
      context,
    };

    // Act
    let start = std::time::Instant::now();
    let response = service.fetch_response(event).await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    assert!(response.is_ok(), "fetch_response should succeed");
    match response {
      Ok(waiter_response) => {
        assert_eq!(
          waiter_response.exit_reason,
          messages::ExitReason::RouterConnectionError,
        );
      }
      Err(err) => {
        assert!(false, "Expected Ok with ExitReason, got Err: {:?}", err);
      }
    }
    assert!(
      duration <= std::time::Duration::from_secs(2),
      "Connection should take at most 2 seconds"
    );

    // Healthcheck not called
    mock_app_healthcheck.assert_hits(0);
    // Bananas called less than 100 times
    assert!(
      mock_app_bananas.hits() < 100,
      "Bananas should not be called 100 times"
    );
  }
}
