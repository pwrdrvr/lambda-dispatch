use axum::{body::Body, http::HeaderValue, routing::get, Router};
use futures::task::noop_waker;
use httpmock::{Method::GET, MockServer};
use hyper::{HeaderMap, StatusCode, Uri};
use lambda_runtime::LambdaEvent;
use std::sync::{atomic::AtomicBool, Arc};
use tokio::net::TcpListener;
use tokio_test::assert_ok;
use tower::Service;

mod support;
use support::mock_router;

use extension::{
  app_client::create_app_client,
  lambda_request_error::LambdaRequestError,
  lambda_service::*,
  messages::{ExitReason, WaiterRequest, WaiterResponse},
  options::Options,
  time::current_time_millis,
};

use crate::support::http2_server::run_http1_app;

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
  assert_eq!(response.pool_id, "test_pool".to_string());
  assert_eq!(response.id, "test_id".to_string());
  assert_eq!(response.request_count, 0);
  assert!(
    response.invoke_duration <= 1,
    "Expected invoke_duration to be less than or equal to 1, but was {}",
    response.invoke_duration
  );
  assert_eq!(response.exit_reason, ExitReason::SelfInitOnly);
}

#[tokio::test]
async fn test_lambda_service_tower_service_call_fatal_error_app_unreachable() {
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .channel_non_200_status_after_count(5)
      .build(),
  );

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
  assert_eq!(response.exit_reason, ExitReason::RouterLambdaInvokeInvalid);
}

#[tokio::test]
async fn test_lambda_service_fetch_response_not_initialized_healthcheck_200_ok() {
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .channel_non_200_status_after_count(0)
      .build(),
  );

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
      .lock()
      .await
      .send(())
      .await
      .unwrap();
  });

  // Act
  // It should not be possible to get past this unless the service
  // has decided to exit itself
  let response = service.fetch_response(event).await;

  // Assert
  // The reason is the response from the channel request is invalid: a 200 OK with no response body
  assert!(
    response.is_ok(),
    "fetch_response failed: {:?}",
    response.err()
  );
  // Get the WaiterResponse out of the response
  let resp = response.unwrap();
  assert_eq!(resp.exit_reason, ExitReason::RouterGoAway);
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
async fn test_lambda_service_fetch_response_async_init_healthcheck_blackhole_async_init_timeout() {
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
      assert_eq!(waiter_response.exit_reason, ExitReason::RouterUnreachable,);
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
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .channel_non_200_status_after_count(100)
      .channel_return_request_without_wait_before_count(1)
      .ping_panic_after_count(0)
      .build(),
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
  tokio::spawn(async move {
    mock_router_server
      .release_request_tx
      .lock()
      .await
      .send(())
      .await
      .unwrap();
    tokio::time::sleep(tokio::time::Duration::from_secs(6)).await;
    mock_router_server
      .release_request_tx
      .lock()
      .await
      .send(())
      .await
      .unwrap();
    tokio::time::sleep(tokio::time::Duration::from_secs(5)).await;
  });

  let mut options = Options::default();
  options.port = mock_app_server.address().port();
  options.last_active_grace_period_ms = 5000;
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
        ExitReason::RouterConnectionError,
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
  mock_app_bananas.assert_hits(2);
}

#[tokio::test]
async fn test_lambda_service_router_connects_ping_panics_channel_stays_open() {
  // Start router server
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .channel_non_200_status_after_count(2)
      .channel_return_request_without_wait_before_count(2)
      .ping_panic_after_count(0)
      .build(),
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
      .lock()
      .await
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
        ExitReason::RouterConnectionError,
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
  mock_app_bananas.assert_hits(2);
}

#[tokio::test]
async fn test_lambda_service_loop_100_valid_get_requests() {
  // Start router server
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .channel_non_200_status_after_count(100)
      .build(),
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
      .lock()
      .await
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
      assert_eq!(waiter_response.exit_reason, ExitReason::RouterGoAway,);
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
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .request_method(mock_router::RequestMethod::PostSimple)
      .channel_non_200_status_after_count(100)
      .build(),
  );

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
      .lock()
      .await
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
      assert_eq!(waiter_response.exit_reason, ExitReason::RouterGoAway,);
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
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .request_method(mock_router::RequestMethod::PostEcho)
      .channel_non_200_status_after_count(1)
      .build(),
  );

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
      .lock()
      .await
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
      assert_eq!(waiter_response.exit_reason, ExitReason::RouterGoAway,);
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
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .request_method(mock_router::RequestMethod::GetEnormousHeaders)
      .channel_non_200_status_after_count(1)
      .build(),
  );

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
      .lock()
      .await
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
      assert_eq!(waiter_response.exit_reason, ExitReason::RouterGoAway,);
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
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .request_method(mock_router::RequestMethod::GetOversizedHeader)
      .channel_non_200_status_after_count(1)
      .build(),
  );

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
      .lock()
      .await
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
      assert_eq!(waiter_response.exit_reason, ExitReason::RouterGoAway,);
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
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .request_method(mock_router::RequestMethod::GetQuerySimple)
      .channel_non_200_status_after_count(1)
      .build(),
  );

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
      .lock()
      .await
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
      assert_eq!(waiter_response.exit_reason, ExitReason::RouterGoAway,);
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
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .request_method(mock_router::RequestMethod::GetQueryRepeated)
      .channel_non_200_status_after_count(1)
      .build(),
  );

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
      .lock()
      .await
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
      assert_eq!(waiter_response.exit_reason, ExitReason::RouterGoAway,);
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
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .request_method(mock_router::RequestMethod::GetQueryEncoded)
      .channel_non_200_status_after_count(1)
      .build(),
  );

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
      .lock()
      .await
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
      assert_eq!(waiter_response.exit_reason, ExitReason::RouterGoAway,);
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
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .request_method(mock_router::RequestMethod::GetQueryUnencodedBrackets)
      .channel_non_200_status_after_count(1)
      .build(),
  );

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
      .lock()
      .await
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
      assert_eq!(waiter_response.exit_reason, ExitReason::RouterGoAway,);
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
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .channel_non_200_status_after_count(100)
      .build(),
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
      // Every app response sends `Connection: close` response header,
      // causing the router channel to have to get a new connection for each request
      .header("Connection", "close")
      .body("Bananas");
  });

  // Let the router run wild
  tokio::spawn(async move {
    mock_router_server
      .release_request_tx
      .lock()
      .await
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
      assert_eq!(waiter_response.exit_reason, ExitReason::RouterGoAway,);
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
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .channel_panic_response_from_extension_on_count(1)
      // We have 2 channels
      // The 2nd channel should not finish all 100 requests after the 1st channel panics
      .channel_non_200_status_after_count(100)
      .build(),
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
      .lock()
      .await
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
        ExitReason::RouterConnectionError,
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
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .channel_non_200_status_after_count(100)
      // We have 2 channels
      // The 2nd channel should not finish all 100 requests after the 1st channel panics
      .channel_panic_response_from_extension_on_count(1)
      .build(),
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
      .lock()
      .await
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
        ExitReason::RouterConnectionError,
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
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .channel_non_200_status_after_count(100)
      .build(),
  );

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
  fixture_lambda_service_channel_status_code(StatusCode::USE_PROXY, ExitReason::RouterStatusOther)
    .await;
}

#[tokio::test]
async fn test_lambda_service_channel_status_400() {
  fixture_lambda_service_channel_status_code(StatusCode::BAD_REQUEST, ExitReason::RouterStatus4xx)
    .await;
}

#[tokio::test]
async fn test_lambda_service_channel_status_409() {
  fixture_lambda_service_channel_status_code(StatusCode::CONFLICT, ExitReason::RouterGoAway).await;
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
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .channel_non_200_status_after_count(0)
      .channel_non_200_status_code(status_code)
      .build(),
  );

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
      .lock()
      .await
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

#[tokio::test]
async fn test_lambda_service_router_connects_app_crashes_graceful_close() {
  // Start router server
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .channel_non_200_status_after_count(100)
      // .channel_return_request_without_wait_before_count(2)
      .request_method_switch_after_count(2)
      // After the 1st response we want to change to a GoAway on the body
      .request_method_switch_to(mock_router::RequestMethod::GetGoAwayOnBody)
      .build(),
  );

  // Start app server
  let mock_app = Router::new().route(
    "/bananas",
    get(|| async {
      let mut headers = HeaderMap::new();
      // Tell the extension we're closing the socket
      headers.insert("Connection", HeaderValue::from_static("close"));
      (StatusCode::OK, headers, Body::from("bananas"))
    }),
  );
  let mock_app_server = run_http1_app(mock_app);
  let mock_app_server_port = mock_app_server.addr.port();

  // Spawn a task to drop the mock app server after a second
  let task = tokio::spawn(async move {
    println!(
      "{} Releasing 1st request",
      chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f")
    );
    mock_router_server
      .release_request_tx
      .lock()
      .await
      .send(())
      .await
      .unwrap();
    tokio::time::sleep(std::time::Duration::from_secs(1)).await;
    // Drop the mock app server so it won't accept the connection for the next request
    drop(mock_app_server);
    println!(
      "{} Dropped app",
      chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f")
    );
    // Wait a second for the drop to process
    tokio::time::sleep(std::time::Duration::from_secs(1)).await;
    println!(
      "{} Releasing 2nd request",
      chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f")
    );
    // Release one more request - This should call with an app unreachable
    // The second channel should also exit after close releases and responds with a GoAway
    mock_router_server
      .release_request_tx
      .lock()
      .await
      .send(())
      .await
      .unwrap();
    // Wait a second for the message to get read
    tokio::time::sleep(std::time::Duration::from_secs(2)).await;
    println!(
      "{} Returning from task",
      chrono::Local::now().format("%Y-%m-%d %H:%M:%S%.3f")
    );
  });

  let mut options = Options::default();
  options.port = mock_app_server_port;
  // Make sure the ping loop doesn't cause us to exit
  options.last_active_grace_period_ms = 10000;
  let initialized = true;

  let app_client = create_app_client();
  let service = LambdaService::new(
    options,
    Arc::new(AtomicBool::new(initialized)),
    format!("http://127.0.0.1:{}/health", mock_app_server_port)
      .parse()
      .unwrap(),
    app_client,
  );
  let request = WaiterRequest {
    pool_id: Some("test_pool".to_string()),
    id: "test_id".to_string(),
    router_url: format!("http://127.0.0.1:{}", mock_router_server.server.addr.port()),
    // We are using 2 channels
    // The 1st channel will give back a request to run right away
    // The 2nd channel will wait for the unlock signal, which we will unlock
    // only from a close() request, not from this test
    number_of_channels: 2,
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

  //
  // NOTE: We do NOT release the wait in the mock router
  // The wait is released by the call to `close` from `ping`
  //

  // Assert
  assert!(response.is_err(), "fetch_response should fail");
  match response {
    Ok(waiter_response) => {
      assert!(
        false,
        "Expected Ok with ExitReason, got ExitReason: {:?}",
        waiter_response.exit_reason
      );
    }
    Err(err) => {
      assert_eq!(err, LambdaRequestError::AppConnectionUnreachable,);
    }
  }
  assert!(
    duration >= std::time::Duration::from_secs(2),
    "Should take at least 2 seconds, took: {:.1}",
    duration.as_secs_f32()
  );
  assert!(
    duration <= std::time::Duration::from_secs(3),
    "Should take at most 3 seconds, took: {:.1}",
    duration.as_secs_f32()
  );
}
