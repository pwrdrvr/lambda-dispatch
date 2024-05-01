use std::{
  pin::Pin,
  sync::{
    atomic::{AtomicBool, Ordering},
    Arc,
  },
  time::Duration,
};

use futures::Future;
use hyper::Uri;
use lambda_runtime::LambdaEvent;
use tokio::time::timeout;
use tower::Service;

use crate::messages::ExitReason;
use crate::options::Options;
use crate::prelude::*;
use crate::time::current_time_millis;
use crate::{
  app_client::AppClient,
  endpoint::{Endpoint, Scheme},
};
use crate::{
  app_start,
  messages::{WaiterRequest, WaiterResponse},
};
use crate::{lambda_request::LambdaRequest, router_client::RouterClient};
use crate::{lambda_request_error::LambdaRequestError, router_client::create_router_client};

#[derive(Clone)]
pub struct LambdaService {
  options: Options,
  initialized: Arc<AtomicBool>,
  healthcheck_url: Uri,
  app_client: AppClient,
  router_client: RouterClient,
}

impl LambdaService {
  pub fn new(
    options: Options,
    initialized: Arc<AtomicBool>,
    healthcheck_url: Uri,
    app_client: AppClient,
  ) -> Self {
    LambdaService {
      options,
      initialized,
      healthcheck_url,
      app_client,
      router_client: create_router_client(),
    }
  }

  //
  // This is called by the Tower.Service trait impl below
  //
  async fn fetch_response(
    &self,
    event: LambdaEvent<WaiterRequest>,
  ) -> Result<WaiterResponse, LambdaRequestError> {
    let start_time = current_time_millis();
    let payload = event.payload.clone();
    let request = match event.payload.validate() {
      Ok(request) => request,
      Err(_) => {
        let mut resp = WaiterResponse::new(
          payload.pool_id.unwrap_or("default".to_string()),
          payload.id.to_string(),
        );
        resp.exit_reason = ExitReason::RouterLambdaInvokeInvalid;
        return Ok(resp);
      }
    };
    let channel_count: u8 = request.number_of_channels;
    let mut resp = WaiterResponse::new(request.pool_id.to_string(), request.lambda_id.to_string());

    log::info!(
      "PoolId: {}, LambdaId: {} - Received request",
      request.pool_id,
      request.lambda_id
    );

    let app_endpoint = Endpoint::new(Scheme::Http, "127.0.0.1", self.options.port);

    if !self.initialized.load(Ordering::SeqCst) {
      let fake_goaway_received = Arc::new(AtomicBool::new(false));
      let result = timeout(
        self.options.async_init_timeout,
        app_start::health_check_contained_app(
          Arc::clone(&fake_goaway_received),
          &self.healthcheck_url,
          &self.app_client,
        ),
      )
      .await;

      match result {
        Ok(success) => {
          if !success {
            log::error!(
              "PoolId: {}, LambdaId: {} - Async init Health check returned false before timeout, bailing",
              request.pool_id,
              request.lambda_id
            );

            // goaway_received is a private var above, it can't be set to false
            // so this can never happen
            return Err(LambdaRequestError::AppConnectionUnreachable);
          }
          self.initialized.store(success, Ordering::SeqCst);
        }
        Err(_) => {
          log::error!(
            "PoolId: {}, LambdaId: {} - Async init Health check not ready before timeout, bailing",
            request.pool_id,
            request.lambda_id
          );

          // Set goaway_received to true to the loop will exit
          fake_goaway_received.store(true, Ordering::SeqCst);

          // We'll panic in the tower service if this happens
          return Err(LambdaRequestError::AppConnectionUnreachable);
        }
      }

      self.initialized.store(true, Ordering::SeqCst);
    }

    if request.init_only {
      log::info!(
        "PoolId: {}, LambdaId: {} - Returning from init-only request",
        request.pool_id,
        request.lambda_id
      );
      resp.invoke_duration = current_time_millis() - start_time;
      resp.exit_reason = ExitReason::SelfInitOnly;
      return Ok(resp);
    }

    // If the sent_time is more than 5 seconds old, just return
    // This is mostly needed locally where requests get stuck in the queue
    // Do not do this in a deployed env because an app that takes > 5 seconds to start
    // will get much longer initial request times
    if self.options.local_env
      && request.sent_time.timestamp_millis() < (current_time_millis() - 5000).try_into().unwrap()
    {
      log::info!(
        "PoolId: {}, LambdaId: {} - Returning from stale request",
        request.pool_id,
        request.lambda_id
      );
      resp.invoke_duration = current_time_millis() - start_time;
      resp.exit_reason = ExitReason::SelfStaleRequest;
      return Ok(resp);
    }

    log::info!(
      "PoolId: {}, LambdaId: {}, Timeout: {}s - Invoked",
      request.pool_id,
      request.lambda_id,
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
      Arc::clone(&request.pool_id),
      Arc::clone(&request.lambda_id),
      channel_count,
      request.router_endpoint,
      deadline_ms,
    );

    //
    // This is the main loop that runs until the deadline is about to be reached
    //
    let result = lambda_request
      .start(self.app_client.clone(), self.router_client.clone())
      .await;
    match result {
      Ok(exit_reason) => {
        log::info!(
          "PoolId: {}, LambdaId: {} - Lambda request completed, exit reason: {:?}",
          request.pool_id,
          request.lambda_id,
          exit_reason
        );

        resp.exit_reason = exit_reason;
      }
      Err(err) => {
        log::error!(
          "PoolId: {}, LambdaId: {} - Lambda request failed: {}",
          request.pool_id,
          request.lambda_id,
          err
        );

        if err.is_fatal() {
          log::error!(
            "PoolId: {}, LambdaId: {} - FATAL ERROR - Exiting",
            request.pool_id,
            request.lambda_id
          );
          return Err(err);
        } else {
          // Set the exit reason for non-fatal errors
          resp.exit_reason = resp.exit_reason.worse(err.into());
        }
      }
    }

    // Print final stats
    log::info!(
      "LambdaId: {}, Requests: {}, Elapsed: {} ms, RPS: {:.1} - Returning from run",
      request.lambda_id,
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
        // We only get here if the error is fatal
        Err(e) => {
          log::error!("Fatal error in LambdaService.call: {}", e);
          Err(e.into())
        }
      }
    })
  }
}

#[cfg(test)]
mod tests {
  use super::*;

  use crate::{app_client::create_app_client, messages, test_mock_router};
  use futures::task::noop_waker;
  use httpmock::{Method::GET, MockServer};
  use hyper::StatusCode;
  use tokio_test::assert_ok;

  use tokio::net::TcpListener;

  async fn start_mock_server() -> u16 {
    let listener = TcpListener::bind(("127.0.0.1", 0)).await.unwrap();
    let port = listener.local_addr().unwrap().port();

    // If we accept but do not await or return then the connection is dropped immediately
    // tokio::spawn(async move {
    //   let (mut _socket, _) = listener.accept().await.unwrap();

    //   // let _ = socket.write_all(b"HTTP/1.1 200 OK\r\n\r\n").await;
    // });
    port
  }

  #[tokio::test]
  async fn test_lambda_service_tower_service_call_init_only_already_initialized() {
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

    let app_client = create_app_client();
    let mut service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      "localhost:54321".parse().unwrap(),
      app_client,
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
  async fn test_lambda_service_tower_service_call_fatal_error_app_unreachable() {
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::Get,
      channel_non_200_status_after_count: 5,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    log::info!(
      "Router server running on port: {}",
      mock_router_server.server.addr.port()
    );

    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://localhost:{}", mock_router_server.server.addr.port()),
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

    // Create the service
    let mut options = Options::default();
    options.port = 54321;
    let initialized = true;

    let app_client = create_app_client();
    let mut service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      "localhost:54321".parse().unwrap(),
      app_client,
    );

    // Ensure the service is ready
    let waker = noop_waker();
    let mut context = core::task::Context::from_waker(&waker);
    let _ = service.poll_ready(&mut context);

    // Call the service with the mock request
    let result = service.call(event.clone()).await;

    // Assert that the response is as expected
    assert!(result.is_err());
    if let Err(err) = &result {
      if let Some(lambda_err) = err.downcast_ref::<LambdaRequestError>() {
        assert_eq!(
          *lambda_err,
          LambdaRequestError::AppConnectionUnreachable,
          "Expected LambdaRequestError::AppConnectionUnreachable"
        );
      } else {
        assert!(false, "Expected LambdaRequestError");
      }
    } else {
      assert!(false, "Expected an error result");
    }
  }

  #[tokio::test]
  async fn test_lambda_service_fetch_response_invalid_router_url() {
    test_fixture_invalid_router_payload(WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: "catz 'n dogz".to_string(),
      number_of_channels: 1,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: false,
    })
    .await;
  }

  #[tokio::test]
  async fn test_lambda_service_fetch_response_invalid_sent_time() {
    test_fixture_invalid_router_payload(WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: "http://127.0.0.1:3001".to_string(),
      number_of_channels: 1,
      sent_time: "catz 'n dogz".to_string(),
      init_only: false,
    })
    .await;
  }

  async fn test_fixture_invalid_router_payload(request: WaiterRequest) {
    let options = Options::default();
    let initialized = true;

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      // blackhole the healthcheck
      "http://192.0.2.0:54321/health".parse().unwrap(),
      app_client,
    );
    let mut context = lambda_runtime::Context::default();
    context.deadline = current_time_millis() + 60 * 1000;
    let event = LambdaEvent {
      payload: request,
      context,
    };

    // Act
    // It should not be possible to get past this unless the service
    // has decided to exit itself
    let response = service.fetch_response(event).await;

    // Assert
    // This should succeed with a WaiterResponse that indicates the error

    assert!(
      response.is_ok(),
      "fetch_response failed: {:?}",
      response.err()
    );
    let response = response.unwrap();
    // Get the WaiterResponse out of the response
    assert_eq!(response.id, "test_id", "Lambda ID");
    assert_eq!(response.pool_id, "test_pool", "Pool ID");
    assert_eq!(
      response.exit_reason,
      messages::ExitReason::RouterLambdaInvokeInvalid
    );
  }

  #[tokio::test]
  async fn test_lambda_service_fetch_response_not_initialized_healthcheck_200_ok() {
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::Get,
      channel_non_200_status_after_count: 0,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    log::info!(
      "Router server running on port: {}",
      mock_router_server.server.addr.port()
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
    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      mock_app_healthcheck_url,
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://localhost:{}", mock_router_server.server.addr.port()),
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
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
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
      mock_router_server
        .request_count
        .load(std::sync::atomic::Ordering::SeqCst),
      1,
      "Request count"
    );
    // The ping count will be 0 because we do not start the response stream and leave
    // it open, which allows the pings to start
    assert_eq!(
      mock_router_server
        .ping_count
        .load(std::sync::atomic::Ordering::SeqCst),
      0,
      "Ping count"
    );
    // The close count will be 0 because the router response indicated we should
    // close so we do not need to call back asking to close
    assert_eq!(
      mock_router_server
        .close_count
        .load(std::sync::atomic::Ordering::SeqCst),
      0,
      "Close count"
    );

    // Assert app server's healthcheck endpoint got called
    mock_app_healthcheck.assert();
  }

  #[tokio::test]
  async fn test_lambda_service_fetch_response_async_init_healthcheck_blackhole_async_init_timeout()
  {
    // NOTE: This is testing whether the async init timeout works, not whether
    // the channel contained app connection timeout works on a blackhole
    test_lambda_service_fetch_response_async_init_healthcheck_blackhole_timeout_fixture(
      std::time::Duration::from_secs(1),
      std::time::Duration::from_secs(1),
      std::time::Duration::from_secs(2),
      54321,
    )
    .await;
  }

  #[tokio::test]
  async fn test_lambda_service_fetch_response_async_init_healthcheck_connects_no_response_async_init_timeout(
  ) {
    let mock_app_port = start_mock_server().await;

    // NOTE: This is testing whether the async init timeout works, not whether
    // the channel contained app connection timeout works on a blackhole
    test_lambda_service_fetch_response_async_init_healthcheck_blackhole_timeout_fixture(
      std::time::Duration::from_secs(2),
      std::time::Duration::from_secs(2),
      std::time::Duration::from_secs(3),
      mock_app_port,
    )
    .await;
  }

  async fn test_lambda_service_fetch_response_async_init_healthcheck_blackhole_timeout_fixture(
    async_init_timeout: std::time::Duration,
    test_min_time: std::time::Duration,
    test_max_time: std::time::Duration,
    port: u16,
  ) {
    let mut options = Options::default();
    options.async_init_timeout = async_init_timeout;
    options.async_init = true;
    options.port = port;
    let initialized = false;

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      // 192.0.2.0/24 (TEST-NET-1)
      format!("http://192.0.2.0:{}/health", port).parse().unwrap(),
      app_client,
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

    assert!(response.is_err(), "fetch_response should have failed");
    assert_eq!(
      response.err(),
      Some(LambdaRequestError::AppConnectionUnreachable)
    );
    assert!(
      duration >= test_min_time,
      "Connection should take at least {:?} seconds, took {:?}",
      test_min_time,
      duration
    );
    assert!(
      duration <= test_max_time,
      "Connection should take at most {:?} seconds, took {:?}",
      test_max_time,
      duration
    );
  }

  #[tokio::test]
  async fn test_lambda_service_fetch_response_local_env_stale_request_reject() {
    let mut options = Options::default();
    options.local_env = true;
    let initialized = true;

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      // 192.0.2.0/24 (TEST-NET-1)
      "http://192.0.2.0:54321/health".parse().unwrap(),
      app_client,
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

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      // 192.0.2.0/24 (TEST-NET-1)
      "http://192.0.2.0:54321/health".parse().unwrap(),
      app_client,
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

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      // 192.0.2.0/24 (TEST-NET-1)
      "http://192.0.2.0:54321/health".parse().unwrap(),
      app_client,
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
      duration > std::time::Duration::from_millis(500),
      "Connection should take at least 5 seconds, took {:?}",
      duration
    );
    assert!(
      duration <= std::time::Duration::from_secs(2),
      "Connection should take at most 2 seconds, took {:?}",
      duration
    );
  }

  #[tokio::test]
  async fn test_lambda_service_router_connects_ping_panics() {
    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::Get,
      channel_non_200_status_after_count: 1,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: 0,
      listener_type: test_mock_router::ListenerType::Http,
    });

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

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.base_url())
        .parse()
        .unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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
  async fn test_lambda_service_router_connects_ping_panics_channel_stays_open() {
    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::Get,
      channel_non_200_status_after_count: -1,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: 0,
      listener_type: test_mock_router::ListenerType::Http,
    });

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

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.base_url())
        .parse()
        .unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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
        assert_ne!(waiter_response.invoke_duration, 0);
        assert_ne!(waiter_response.request_count, 0);
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
  async fn test_lambda_service_loop_100_valid_get_requests() {
    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::Get,
      channel_non_200_status_after_count: 100,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

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

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.base_url())
        .parse()
        .unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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
    let response = service.fetch_response(event).await;

    // Assert
    assert!(response.is_ok(), "fetch_response should succeed");
    match response {
      Ok(waiter_response) => {
        assert_eq!(
          waiter_response.exit_reason,
          messages::ExitReason::RouterGoAway,
        );
        assert_eq!(waiter_response.request_count, 100);
        assert_ne!(waiter_response.invoke_duration, 0);
      }
      Err(err) => {
        assert!(false, "Expected Ok with ExitReason, got Err: {:?}", err);
      }
    }

    // Healthcheck not called
    mock_app_healthcheck.assert_hits(0);
    // Bananas called 100 times
    mock_app_bananas.assert_hits(100);
  }

  #[tokio::test]
  async fn test_lambda_service_loop_100_valid_post_requests() {
    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::PostSimple,
      channel_non_200_status_after_count: 100,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    // Start app server
    let mock_app_server = wiremock::MockServer::start().await;

    wiremock::Mock::given(wiremock::matchers::method("POST"))
      .and(wiremock::matchers::path("/bananas"))
      .respond_with(move |request: &'_ wiremock::Request| {
        let body_str = String::from_utf8(request.body.clone()).unwrap();
        assert_eq!(body_str, "HELLO WORLD");
        wiremock::ResponseTemplate::new(200).set_body_raw("Bananas", "text/plain")
      })
      .expect(100)
      .mount(&mock_app_server)
      .await;

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

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.uri()).parse().unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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
    let response = service.fetch_response(event).await;

    // Assert
    assert!(response.is_ok(), "fetch_response should succeed");
    match response {
      Ok(waiter_response) => {
        assert_eq!(
          waiter_response.exit_reason,
          messages::ExitReason::RouterGoAway,
        );
        assert_eq!(waiter_response.request_count, 100);
        assert_ne!(waiter_response.invoke_duration, 0);
      }
      Err(err) => {
        assert!(false, "Expected Ok with ExitReason, got Err: {:?}", err);
      }
    }
  }

  #[tokio::test]
  async fn test_lambda_service_valid_10kb_echo_post_requests() {
    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::PostEcho,
      channel_non_200_status_after_count: 1,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    // Start app server
    let mock_app_server = wiremock::MockServer::start().await;

    wiremock::Mock::given(wiremock::matchers::method("POST"))
      .and(wiremock::matchers::path("/bananas_echo"))
      .respond_with(move |request: &'_ wiremock::Request| {
        let body_str = String::from_utf8(request.body.clone()).unwrap();
        let data = "a".repeat(10 * 1024);
        assert_eq!(body_str, data, "Should get 10 KB of 'a'");
        wiremock::ResponseTemplate::new(200).set_body_raw(body_str, "text/plain")
      })
      .expect(1)
      .mount(&mock_app_server)
      .await;

    // Let the router run wild
    tokio::spawn(async move {
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let mut options = Options::default();
    options.compression = true;
    options.port = mock_app_server.address().port();
    let initialized = true;

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.uri()).parse().unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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
    let response = service.fetch_response(event).await;

    // Assert
    assert!(response.is_ok(), "fetch_response should succeed");
    match response {
      Ok(waiter_response) => {
        assert_eq!(
          waiter_response.exit_reason,
          messages::ExitReason::RouterGoAway,
        );
        assert_eq!(waiter_response.request_count, 1);
        assert_ne!(waiter_response.invoke_duration, 0);
      }
      Err(err) => {
        assert!(false, "Expected Ok with ExitReason, got Err: {:?}", err);
      }
    }
  }

  #[tokio::test]
  async fn test_lambda_service_valid_124kb_of_headers() {
    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::GetEnormousHeaders,
      channel_non_200_status_after_count: 1,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    // Start app server
    let mock_app_server = wiremock::MockServer::start().await;

    wiremock::Mock::given(wiremock::matchers::method("GET"))
      .and(wiremock::matchers::path("/bananas/enormous_headers"))
      .respond_with(move |request: &'_ wiremock::Request| {
        let value = "a".repeat(1024 * 31);
        assert_eq!(*request.headers.get("Test-Header-0").unwrap(), value);
        assert_eq!(*request.headers.get("Test-Header-1").unwrap(), value);
        assert_eq!(*request.headers.get("Test-Header-2").unwrap(), value);
        assert_eq!(*request.headers.get("Test-Header-3").unwrap(), value);

        // Copy the headers back on the response
        wiremock::ResponseTemplate::new(200)
          .append_header("Test-Header-0", &value)
          .append_header("Test-Header-1", &value)
          .append_header("Test-Header-2", &value)
          .append_header("Test-Header-3", &value)
          .set_body_string("banana_enormous_headers")
      })
      .expect(1)
      .mount(&mock_app_server)
      .await;

    // Let the router run wild
    tokio::spawn(async move {
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let mut options = Options::default();
    options.compression = true;
    options.port = mock_app_server.address().port();
    let initialized = true;

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.uri()).parse().unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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
    let response = service.fetch_response(event).await;

    // Assert
    assert!(response.is_ok(), "fetch_response should succeed");
    match response {
      Ok(waiter_response) => {
        assert_eq!(
          waiter_response.exit_reason,
          messages::ExitReason::RouterGoAway,
        );
        assert_eq!(waiter_response.request_count, 1);
        assert_ne!(waiter_response.invoke_duration, 0);
      }
      Err(err) => {
        assert!(false, "Expected Ok with ExitReason, got Err: {:?}", err);
      }
    }
  }

  #[tokio::test]
  async fn test_lambda_service_valid_oversized_headers() {
    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::GetOversizedHeader,
      channel_non_200_status_after_count: 1,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    // Start app server
    let mock_app_server = wiremock::MockServer::start().await;

    wiremock::Mock::given(wiremock::matchers::method("GET"))
      .and(wiremock::matchers::path("/bananas/oversized_header"))
      .respond_with(move |request: &'_ wiremock::Request| {
        let value = "a".repeat(1024 * 64);
        assert_eq!(*request.headers.get("Test-Header").unwrap(), value);

        // Copy the headers back on the response
        wiremock::ResponseTemplate::new(200)
          .append_header("Test-Header", &value)
          .set_body_string("banana_oversized_headers")
      })
      .expect(1)
      .mount(&mock_app_server)
      .await;

    // Let the router run wild
    tokio::spawn(async move {
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let mut options = Options::default();
    options.compression = true;
    options.port = mock_app_server.address().port();
    let initialized = true;

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.uri()).parse().unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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
    let response = service.fetch_response(event).await;

    // Assert
    assert!(response.is_ok(), "fetch_response should succeed");
    match response {
      Ok(waiter_response) => {
        assert_eq!(
          waiter_response.exit_reason,
          messages::ExitReason::RouterGoAway,
        );
        assert_eq!(waiter_response.request_count, 1);
        assert_ne!(waiter_response.invoke_duration, 0);
      }
      Err(err) => {
        assert!(false, "Expected Ok with ExitReason, got Err: {:?}", err);
      }
    }
  }

  #[tokio::test]
  async fn test_lambda_service_get_query_string_simple() {
    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::GetQuerySimple,
      channel_non_200_status_after_count: 1,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    // Start app server
    let mock_app_server = wiremock::MockServer::start().await;

    wiremock::Mock::given(wiremock::matchers::method("GET"))
      .and(wiremock::matchers::path("/bananas_query_simple"))
      .respond_with(move |request: &'_ wiremock::Request| {
        let body_str = String::from_utf8(request.body.clone()).unwrap();
        let query = request.url.query();
        assert_eq!("cat=dog&frog=log", query.unwrap(), "Query string");
        wiremock::ResponseTemplate::new(200).set_body_raw(body_str, "text/plain")
      })
      .expect(1)
      .mount(&mock_app_server)
      .await;

    // Let the router run wild
    tokio::spawn(async move {
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let mut options = Options::default();
    options.compression = true;
    options.port = mock_app_server.address().port();
    let initialized = true;

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.uri()).parse().unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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
    let response = service.fetch_response(event).await;

    // Assert
    assert!(response.is_ok(), "fetch_response should succeed");
    match response {
      Ok(waiter_response) => {
        assert_eq!(
          waiter_response.exit_reason,
          messages::ExitReason::RouterGoAway,
        );
        assert_eq!(waiter_response.request_count, 1);
        assert_ne!(waiter_response.invoke_duration, 0);
      }
      Err(err) => {
        assert!(false, "Expected Ok with ExitReason, got Err: {:?}", err);
      }
    }
  }

  #[tokio::test]
  async fn test_lambda_service_get_query_string_repeated() {
    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::GetQueryRepeated,
      channel_non_200_status_after_count: 1,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    // Start app server
    let mock_app_server = wiremock::MockServer::start().await;

    wiremock::Mock::given(wiremock::matchers::method("GET"))
      .and(wiremock::matchers::path("/bananas_query_repeated"))
      .respond_with(move |request: &'_ wiremock::Request| {
        let body_str = String::from_utf8(request.body.clone()).unwrap();
        let query = request.url.query();
        assert_eq!("cat=dog&cat=log&cat=cat", query.unwrap(), "Query string");
        wiremock::ResponseTemplate::new(200).set_body_raw(body_str, "text/plain")
      })
      .expect(1)
      .mount(&mock_app_server)
      .await;

    // Let the router run wild
    tokio::spawn(async move {
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let mut options = Options::default();
    options.compression = true;
    options.port = mock_app_server.address().port();
    let initialized = true;

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.uri()).parse().unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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
    let response = service.fetch_response(event).await;

    // Assert
    assert!(response.is_ok(), "fetch_response should succeed");
    match response {
      Ok(waiter_response) => {
        assert_eq!(
          waiter_response.exit_reason,
          messages::ExitReason::RouterGoAway,
        );
        assert_eq!(waiter_response.request_count, 1);
        assert_ne!(waiter_response.invoke_duration, 0);
      }
      Err(err) => {
        assert!(false, "Expected Ok with ExitReason, got Err: {:?}", err);
      }
    }
  }

  #[tokio::test]
  async fn test_lambda_service_get_query_string_encoded() {
    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::GetQueryEncoded,
      channel_non_200_status_after_count: 1,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    // Start app server
    let mock_app_server = wiremock::MockServer::start().await;

    wiremock::Mock::given(wiremock::matchers::method("GET"))
      .and(wiremock::matchers::path("/bananas_query_encoded"))
      .respond_with(move |request: &'_ wiremock::Request| {
        let body_str = String::from_utf8(request.body.clone()).unwrap();
        let query = request.url.query();
        assert_eq!(
          "cat=dog%25&cat=%22log%22&cat=cat",
          query.unwrap(),
          "Query string"
        );
        wiremock::ResponseTemplate::new(200).set_body_raw(body_str, "text/plain")
      })
      .expect(1)
      .mount(&mock_app_server)
      .await;

    // Let the router run wild
    tokio::spawn(async move {
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let mut options = Options::default();
    options.compression = true;
    options.port = mock_app_server.address().port();
    let initialized = true;

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.uri()).parse().unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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
    let response = service.fetch_response(event).await;

    // Assert
    assert!(response.is_ok(), "fetch_response should succeed");
    match response {
      Ok(waiter_response) => {
        assert_eq!(
          waiter_response.exit_reason,
          messages::ExitReason::RouterGoAway,
        );
        assert_eq!(waiter_response.request_count, 1);
        assert_ne!(waiter_response.invoke_duration, 0);
      }
      Err(err) => {
        assert!(false, "Expected Ok with ExitReason, got Err: {:?}", err);
      }
    }
  }

  #[tokio::test]
  async fn test_lambda_service_get_query_string_unencoded_brackets() {
    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::GetQueryUnencodedBrackets,
      channel_non_200_status_after_count: 1,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    // Start app server
    let mock_app_server = wiremock::MockServer::start().await;

    wiremock::Mock::given(wiremock::matchers::method("GET"))
      .and(wiremock::matchers::path(
        "/bananas_query_unencoded_brackets",
      ))
      .respond_with(move |request: &'_ wiremock::Request| {
        let body_str = String::from_utf8(request.body.clone()).unwrap();
        let query = request.url.query();
        assert_eq!("cat=[dog]&cat=log&cat=cat", query.unwrap(), "Query string");
        wiremock::ResponseTemplate::new(200).set_body_raw(body_str, "text/plain")
      })
      .expect(1)
      .mount(&mock_app_server)
      .await;

    // Let the router run wild
    tokio::spawn(async move {
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let mut options = Options::default();
    options.compression = true;
    options.port = mock_app_server.address().port();
    let initialized = true;

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.uri()).parse().unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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
    let response = service.fetch_response(event).await;

    // Assert
    assert!(response.is_ok(), "fetch_response should succeed");
    match response {
      Ok(waiter_response) => {
        assert_eq!(
          waiter_response.exit_reason,
          messages::ExitReason::RouterGoAway,
        );
        assert_eq!(waiter_response.request_count, 1);
        assert_ne!(waiter_response.invoke_duration, 0);
      }
      Err(err) => {
        assert!(false, "Expected Ok with ExitReason, got Err: {:?}", err);
      }
    }
  }

  #[tokio::test]
  async fn test_lambda_service_loop_100_requests_contained_app_connection_close_header() {
    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::Get,
      channel_non_200_status_after_count: 100,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

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
        // Every app response sends `Connection: close` response header,
        // causing the router channel to have to get a new connection for each request
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

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.base_url())
        .parse()
        .unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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
          messages::ExitReason::RouterGoAway,
        );
        assert_eq!(waiter_response.request_count, 100);
        assert_ne!(waiter_response.invoke_duration, 0);
      }
      Err(err) => {
        assert!(false, "Expected Ok with ExitReason, got Err: {:?}", err);
      }
    }
    assert!(
      duration <= std::time::Duration::from_secs(6),
      "Connection should take at most 6 seconds, took {:?}",
      duration
    );

    // Healthcheck not called
    mock_app_healthcheck.assert_hits(0);
    // Bananas called 100 times
    mock_app_bananas.assert_hits(100);
  }

  #[tokio::test]
  async fn test_lambda_service_router_connects_channel_request_panics() {
    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::Get,
      // We have 2 channels
      // The 2nd channel should not finish all 100 requests after the 1st channel panics
      channel_non_200_status_after_count: 100,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: 1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

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

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.base_url())
        .parse()
        .unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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

  #[tokio::test]
  async fn test_lambda_service_router_connects_channel_response_panics() {
    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::Get,
      // We have 2 channels
      // The 2nd channel should not finish all 100 requests after the 1st channel panics
      channel_non_200_status_after_count: 100,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: 1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

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

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.base_url())
        .parse()
        .unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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

  #[tokio::test]
  async fn test_lambda_service_initialized_app_unopened_port_timeout() {
    let port = 54321;

    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::Get,
      channel_non_200_status_after_count: 100,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    let mut options = Options::default();
    options.async_init_timeout = std::time::Duration::from_millis(1000);
    options.async_init = true;
    // App port is a blackhole
    options.port = port;
    let initialized = true;

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      // Healthcheck is not called in this test
      "http://192.0.2.0:54321/health".parse().unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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

    assert!(response.is_err(), "fetch_response should have failed");
    assert_eq!(
      response.err(),
      Some(LambdaRequestError::AppConnectionUnreachable),
    );
    assert!(
      duration <= std::time::Duration::from_secs(1),
      "Connection should take at most 1 seconds"
    );
  }

  #[tokio::test]
  async fn test_lambda_service_channel_status_305() {
    fixture_lambda_service_channel_status_code(
      StatusCode::USE_PROXY,
      ExitReason::RouterStatusOther,
    )
    .await;
  }

  #[tokio::test]
  async fn test_lambda_service_channel_status_400() {
    fixture_lambda_service_channel_status_code(
      StatusCode::BAD_REQUEST,
      ExitReason::RouterStatus4xx,
    )
    .await;
  }

  #[tokio::test]
  async fn test_lambda_service_channel_status_409() {
    fixture_lambda_service_channel_status_code(StatusCode::CONFLICT, ExitReason::RouterGoAway)
      .await;
  }

  #[tokio::test]
  async fn test_lambda_service_channel_status_500() {
    fixture_lambda_service_channel_status_code(
      StatusCode::INTERNAL_SERVER_ERROR,
      ExitReason::RouterStatus5xx,
    )
    .await;
  }

  async fn fixture_lambda_service_channel_status_code(
    status_code: StatusCode,
    expected_result: ExitReason,
  ) {
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::Get,
      channel_non_200_status_after_count: 0,
      channel_non_200_status_code: status_code,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    // Start app server
    let mock_app_server = MockServer::start();
    let mock_app_healthcheck = mock_app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
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

    let app_client = create_app_client();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      format!("{}/health", mock_app_server.base_url())
        .parse()
        .unwrap(),
      app_client,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
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
    let response = service.fetch_response(event).await;

    // Assert
    assert!(response.is_ok(), "response is ok");
    let response: WaiterResponse = response.unwrap();
    assert_eq!(response.exit_reason, expected_result, "result expected");
    assert_eq!(
      mock_router_server
        .request_count
        .load(std::sync::atomic::Ordering::SeqCst),
      1
    );

    // Assert app server's healthcheck endpoint did not get called
    mock_app_healthcheck.assert_hits(0);
  }
}
