use httpmock::{Method::GET, MockServer};
use hyper::StatusCode;
use std::sync::Arc;
use tokio_test::assert_ok;

mod support;
use support::mock_router;

use extension::{
  app_client::create_app_client, endpoint::Endpoint, router_channel::*,
  router_client::create_router_client, time,
};

#[tokio::test]
async fn test_channel_status_305_http() {
  fixture_channel_status_code(
    StatusCode::USE_PROXY,
    Some(ChannelResult::RouterStatusOther),
    mock_router::ListenerType::Http,
  )
  .await;
}

#[tokio::test]
async fn test_channel_status_305_https() {
  fixture_channel_status_code(
    StatusCode::USE_PROXY,
    Some(ChannelResult::RouterStatusOther),
    mock_router::ListenerType::Https,
  )
  .await;
}

#[tokio::test]
async fn test_channel_status_400_http() {
  fixture_channel_status_code(
    StatusCode::BAD_REQUEST,
    Some(ChannelResult::RouterStatus4xx),
    mock_router::ListenerType::Http,
  )
  .await;
}

#[tokio::test]
async fn test_channel_status_400_https() {
  fixture_channel_status_code(
    StatusCode::BAD_REQUEST,
    Some(ChannelResult::RouterStatus4xx),
    mock_router::ListenerType::Https,
  )
  .await;
}

#[tokio::test]
async fn test_channel_status_409_http() {
  fixture_channel_status_code(
    StatusCode::CONFLICT,
    Some(ChannelResult::GoAwayReceived),
    mock_router::ListenerType::Http,
  )
  .await;
}

#[tokio::test]
async fn test_channel_status_409_https() {
  fixture_channel_status_code(
    StatusCode::CONFLICT,
    Some(ChannelResult::GoAwayReceived),
    mock_router::ListenerType::Https,
  )
  .await;
}

#[tokio::test]
async fn test_channel_status_500_http() {
  fixture_channel_status_code(
    StatusCode::INTERNAL_SERVER_ERROR,
    Some(ChannelResult::RouterStatus5xx),
    mock_router::ListenerType::Http,
  )
  .await;
}

#[tokio::test]
async fn test_channel_status_500_https() {
  fixture_channel_status_code(
    StatusCode::INTERNAL_SERVER_ERROR,
    Some(ChannelResult::RouterStatus5xx),
    mock_router::ListenerType::Https,
  )
  .await;
}

async fn fixture_channel_status_code(
  status_code: StatusCode,
  expected_result: Option<ChannelResult>,
  listener_type: mock_router::ListenerType,
) {
  let lambda_id = "lambda_id".to_string();
  let pool_id = "pool_id".to_string();
  let channel_id = "channel_id".to_string();

  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .channel_non_200_status_after_count(0)
      .channel_non_200_status_code(status_code)
      .listener_type(listener_type)
      .build(),
  );

  let router_endpoint: Endpoint = format!(
    "{}://localhost:{}",
    listener_type.to_string(),
    mock_router_server.server.addr.port()
  )
  .parse()
  .unwrap();

  // Start app server
  let mock_app_server = MockServer::start();
  let mock_app_healthcheck = mock_app_server.mock(|when, then| {
    when.method(GET).path("/health");
    then.status(200).body("OK");
  });
  let app_endpoint: Endpoint = mock_app_server.base_url().parse().unwrap();

  let app_client = create_app_client();
  let router_client = create_router_client();

  // Declare the counts
  let channel_request_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
  let last_active = Arc::new(std::sync::atomic::AtomicU64::new(0));
  let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(0));

  // Act
  let mut channel = RouterChannel::new(
    Arc::clone(&channel_request_count),
    false,
    Arc::clone(&goaway_received),
    Arc::clone(&last_active),
    Arc::clone(&requests_in_flight),
    router_endpoint,
    app_endpoint,
    0,
    pool_id.into(),
    lambda_id.into(),
    channel_id,
  );
  let channel_start_result = channel.start(app_client, router_client).await;

  // Assert
  assert!(channel_start_result.is_ok(), "channel start result is ok");
  let channel_start_result = channel_start_result.unwrap();
  assert_eq!(channel_start_result, expected_result, "result expected");
  assert_eq!(channel.count.load(std::sync::atomic::Ordering::SeqCst), 0);
  assert_eq!(
    channel
      .requests_in_flight
      .load(std::sync::atomic::Ordering::SeqCst),
    0
  );
  assert_eq!(
    channel
      .last_active
      .load(std::sync::atomic::Ordering::SeqCst),
    0
  );
  assert_eq!(
    mock_router_server
      .request_count
      .load(std::sync::atomic::Ordering::SeqCst),
    1
  );
  assert!(goaway_received.load(std::sync::atomic::Ordering::SeqCst));

  // Assert app server's healthcheck endpoint did not get called
  mock_app_healthcheck.assert_hits(0);
}

#[tokio::test]
async fn test_channel_read_request_send_to_app_http() {
  fixture_channel_read_request_send_to_app(mock_router::ListenerType::Http).await;
}

#[tokio::test]
async fn test_channel_read_request_send_to_app_https() {
  fixture_channel_read_request_send_to_app(mock_router::ListenerType::Https).await;
}

async fn fixture_channel_read_request_send_to_app(listener_type: mock_router::ListenerType) {
  let lambda_id = "lambda_id".to_string();
  let pool_id = "pool_id".to_string();
  let channel_id = "channel_id".to_string();

  // Start router server
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .channel_non_200_status_after_count(1)
      .listener_type(listener_type)
      .build(),
  );

  let router_endpoint: Endpoint = format!(
    "{}://localhost:{}",
    listener_type.to_string(),
    mock_router_server.server.addr.port()
  )
  .parse()
  .unwrap();

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

  // Release the request after a few seconds
  tokio::spawn(async move {
    tokio::time::sleep(tokio::time::Duration::from_millis(500)).await;
    mock_router_server
      .release_request_tx
      .lock()
      .await
      .send(())
      .await
      .unwrap();
  });

  let app_client = create_app_client();
  let router_client = create_router_client();

  let channel_request_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
  let last_active = Arc::new(std::sync::atomic::AtomicU64::new(0));
  let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(0));

  // Act
  let mut channel = RouterChannel::new(
    Arc::clone(&channel_request_count),
    true, // compression
    Arc::clone(&goaway_received),
    Arc::clone(&last_active),
    Arc::clone(&requests_in_flight),
    router_endpoint,
    app_endpoint,
    0,
    pool_id.into(),
    lambda_id.into(),
    channel_id,
  );
  let channel_start_result = channel.start(app_client, router_client).await;
  // Assert
  assert_ok!(channel_start_result);
  assert_eq!(
    channel.count.load(std::sync::atomic::Ordering::SeqCst),
    1,
    "channel should have finished a request"
  );
  assert_eq!(
    channel
      .requests_in_flight
      .load(std::sync::atomic::Ordering::SeqCst),
    0
  );
  assert_ne!(
    channel
      .last_active
      .load(std::sync::atomic::Ordering::SeqCst),
    0,
    "last active should not be 0"
  );
  // last active should be close to current_time_millis
  assert!(
    time::current_time_millis()
      - channel
        .last_active
        .load(std::sync::atomic::Ordering::SeqCst)
      < 1000,
    "last active should be close to current_time_millis"
  );
  assert_eq!(
    mock_router_server
      .request_count
      .load(std::sync::atomic::Ordering::SeqCst),
    2,
    "router channel requests received should be 2"
  );
  assert!(
    goaway_received.load(std::sync::atomic::Ordering::SeqCst),
    "goaway received should be true"
  );

  // Assert app server's healthcheck endpoint did not get called
  mock_app_healthcheck.assert_hits(0);

  // Assert that the test route did get called
  mock_app_bananas.assert_hits(1);
}

#[tokio::test]
async fn test_channel_goaway_on_body_http() {
  fixture_channel_goaway_on_body(mock_router::ListenerType::Http).await;
}

#[tokio::test]
async fn test_channel_goaway_on_body_https() {
  fixture_channel_goaway_on_body(mock_router::ListenerType::Https).await;
}

async fn fixture_channel_goaway_on_body(listener_type: mock_router::ListenerType) {
  let lambda_id = "lambda_id".to_string();
  let pool_id = "pool_id".to_string();
  let channel_id = "channel_id".to_string();

  // Start router server
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .request_method(mock_router::RequestMethod::GetGoAwayOnBody)
      .channel_non_200_status_after_count(1)
      .listener_type(listener_type)
      .ping_panic_after_count(0)
      .build(),
  );
  let router_endpoint: Endpoint = format!(
    "{}://localhost:{}",
    listener_type.to_string(),
    mock_router_server.server.addr.port()
  )
  .parse()
  .unwrap();

  // Start app server
  let mock_app_server = MockServer::start();
  let mock_app_healthcheck = mock_app_server.mock(|when, then| {
    when.method(GET).path("/health");
    then.status(200).body("OK");
  });
  let app_endpoint: Endpoint = mock_app_server.base_url().parse().unwrap();

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

  let app_client = create_app_client();
  let router_client = create_router_client();

  let channel_request_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
  let last_active = Arc::new(std::sync::atomic::AtomicU64::new(0));
  let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(0));

  // Act
  let mut channel = RouterChannel::new(
    Arc::clone(&channel_request_count),
    false, // compression
    Arc::clone(&goaway_received),
    Arc::clone(&last_active),
    Arc::clone(&requests_in_flight),
    router_endpoint,
    app_endpoint,
    0,
    pool_id.into(),
    lambda_id.into(),
    channel_id,
  );
  let channel_start_result = channel.start(app_client, router_client).await;
  // Assert
  assert_ok!(channel_start_result);
  assert_eq!(
    channel.count.load(std::sync::atomic::Ordering::SeqCst),
    0,
    "channel handled request count"
  );
  assert_eq!(
    channel
      .requests_in_flight
      .load(std::sync::atomic::Ordering::SeqCst),
    0
  );
  assert_ne!(
    channel
      .last_active
      .load(std::sync::atomic::Ordering::SeqCst),
    0,
    "last active should not be 0"
  );
  // last active should be close to current_time_millis
  assert!(
    time::current_time_millis()
      - channel
        .last_active
        .load(std::sync::atomic::Ordering::SeqCst)
      < 2000,
    "last active should be close to current_time_millis"
  );
  assert_eq!(
    mock_router_server
      .request_count
      .load(std::sync::atomic::Ordering::SeqCst),
    1,
    "router channel requests received"
  );
  assert!(
    goaway_received.load(std::sync::atomic::Ordering::SeqCst),
    "goaway received should be true",
  );

  // Assert app server's healthcheck endpoint did not get called
  mock_app_healthcheck.assert_hits(0);
}

#[tokio::test]
async fn test_channel_invalid_request_headers_should_continue() {
  let lambda_id = "lambda_id".to_string();
  let pool_id = "pool_id".to_string();
  let channel_id = "channel_id".to_string();

  // Start router server
  let mock_router_server = mock_router::setup_router(
    mock_router::RouterParamsBuilder::new()
      .request_method(mock_router::RequestMethod::GetInvalidHeaders)
      .channel_non_200_status_after_count(2)
      .ping_panic_after_count(0)
      .build(),
  );
  let router_endpoint: Endpoint =
    format!("http://localhost:{}", mock_router_server.server.addr.port())
      .parse()
      .unwrap();

  // Start app server
  let mock_app_server = MockServer::start();
  let mock_app_healthcheck = mock_app_server.mock(|when, then| {
    when.method(GET).path("/health");
    then.status(200).body("OK");
  });
  let app_endpoint: Endpoint = mock_app_server.base_url().parse().unwrap();

  for _ in 0..2 {
    // Release the request after a few seconds
    tokio::time::sleep(tokio::time::Duration::from_secs(1)).await;
    mock_router_server
      .release_request_tx
      .lock()
      .await
      .send(())
      .await
      .unwrap();
  }

  let app_client = create_app_client();
  let router_client = create_router_client();

  let channel_request_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
  let last_active = Arc::new(std::sync::atomic::AtomicU64::new(0));
  let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(0));

  // Act
  let mut channel = RouterChannel::new(
    Arc::clone(&channel_request_count),
    false, // compression
    Arc::clone(&goaway_received),
    Arc::clone(&last_active),
    Arc::clone(&requests_in_flight),
    router_endpoint,
    app_endpoint,
    0,
    pool_id.into(),
    lambda_id.into(),
    channel_id,
  );
  let channel_start_result = channel.start(app_client, router_client).await;
  // Assert
  assert_eq!(
    channel_start_result,
    Ok(Some(ChannelResult::GoAwayReceived)),
  );
  assert_eq!(
    channel.count.load(std::sync::atomic::Ordering::SeqCst),
    0,
    "channel handled request count"
  );
  assert_eq!(
    channel
      .requests_in_flight
      .load(std::sync::atomic::Ordering::SeqCst),
    0
  );
  assert_ne!(
    channel
      .last_active
      .load(std::sync::atomic::Ordering::SeqCst),
    0,
    "last active should not be 0"
  );
  // last active should be close to current_time_millis
  assert!(
    time::current_time_millis()
      - channel
        .last_active
        .load(std::sync::atomic::Ordering::SeqCst)
      < 2000,
    "last active should be close to current_time_millis"
  );
  assert_eq!(
    mock_router_server
      .request_count
      .load(std::sync::atomic::Ordering::SeqCst),
    3,
    "router channel requests received"
  );
  assert_eq!(
    goaway_received.load(std::sync::atomic::Ordering::SeqCst),
    true,
    "goaway received should be true",
  );

  // Assert app server's healthcheck endpoint did not get called
  mock_app_healthcheck.assert_hits(0);
}
