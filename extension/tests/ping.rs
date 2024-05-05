use axum::{extract::Path, routing::get, Router};
use hyper::StatusCode;
use std::{sync::Arc, time::Duration};

mod support;
use support::http2_server::run_http2_app;

use extension::{
  endpoint::Endpoint, ping::*, prelude::*, router_client::create_router_client,
  time::current_time_millis,
};

#[tokio::test]
async fn test_ping_immediate_exit_deadline() {
  let lambda_id = "lambda_id".to_string();
  let pool_id = "pool_id".to_string();

  // Start router server
  // Use an arc int to count how many times the request endpoint was called
  let ping_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let ping_count_clone = Arc::clone(&ping_count);
  let close_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let close_count_clone = Arc::clone(&close_count);
  let app = Router::new()
    .route(
      "/api/chunked/ping/:lambda_id",
      get(|Path(lambda_id): Path<String>| async move {
        let ping_count = Arc::clone(&ping_count_clone);
        // Increment
        ping_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        format!("Ping! LambdaID: {}", lambda_id)
      }),
    )
    .route(
      "/api/chunked/close/:lambda_id",
      get(|Path(lambda_id): Path<String>| async move {
        let close_count = Arc::clone(&close_count_clone);
        // Increment
        close_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        format!("Close! LambdaID: {}", lambda_id)
      }),
    );
  let mock_router_server = run_http2_app(app);

  let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
    .parse()
    .unwrap();

  let pool_id_arc: PoolId = pool_id.clone().into();
  let lambda_id_arc: LambdaId = lambda_id.clone().into();

  // Declare the counts
  let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
  let last_active = Arc::new(std::sync::atomic::AtomicU64::new(0));
  let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let cancel_token = tokio_util::sync::CancellationToken::new();
  let deadline_ms = 2000;

  // Act
  let result = send_ping_requests(
    Arc::clone(&last_active),
    Arc::clone(&goaway_received),
    create_router_client(),
    Arc::clone(&pool_id_arc),
    Arc::clone(&lambda_id_arc),
    Arc::new(std::sync::atomic::AtomicUsize::new(0)), // count is only used in log messages
    router_endpoint,
    deadline_ms,
    cancel_token,
    Arc::clone(&requests_in_flight),
    250,
  )
  .await;

  // Assert
  assert_eq!(
    result,
    Some(PingResult::Deadline),
    "result should be Deadline"
  );
  assert_eq!(
    ping_count.load(std::sync::atomic::Ordering::SeqCst),
    0,
    "ping count should be 0"
  );
  assert_eq!(
    close_count.load(std::sync::atomic::Ordering::SeqCst),
    1,
    "close count should be 1"
  );
  assert_eq!(
    requests_in_flight.load(std::sync::atomic::Ordering::SeqCst),
    0,
    "requests in flight should be 0"
  );
  assert_eq!(
    last_active.load(std::sync::atomic::Ordering::SeqCst),
    0,
    "last active should be 0"
  );
  assert_eq!(
    goaway_received.load(std::sync::atomic::Ordering::SeqCst),
    true,
    "goaway received should be true"
  );
}

#[tokio::test]
async fn test_ping_channel_last_active() {
  let lambda_id = "lambda_id".to_string();
  let pool_id = "pool_id".to_string();

  // Start router server
  // Use an arc int to count how many times the request endpoint was called
  let ping_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let ping_count_clone = Arc::clone(&ping_count);
  let close_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let close_count_clone = Arc::clone(&close_count);
  let app = Router::new()
    .route(
      "/api/chunked/ping/:lambda_id",
      get(|Path(lambda_id): Path<String>| async move {
        let ping_count = Arc::clone(&ping_count_clone);
        // Increment
        ping_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        format!("Ping! LambdaID: {}", lambda_id)
      }),
    )
    .route(
      "/api/chunked/close/:lambda_id",
      get(|Path(lambda_id): Path<String>| async move {
        let close_count = Arc::clone(&close_count_clone);
        // Increment
        close_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        format!("Close! LambdaID: {}", lambda_id)
      }),
    );
  let mock_router_server = run_http2_app(app);

  let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
    .parse()
    .unwrap();

  let pool_id_arc: PoolId = pool_id.clone().into();
  let lambda_id_arc: LambdaId = lambda_id.clone().into();

  // Declare the counts
  let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
  let last_active_intial = current_time_millis();
  let last_active = Arc::new(std::sync::atomic::AtomicU64::new(last_active_intial));
  let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let cancel_token = tokio_util::sync::CancellationToken::new();
  let deadline_ms = current_time_millis() + 30000;

  // Act
  let result = send_ping_requests(
    Arc::clone(&last_active),
    Arc::clone(&goaway_received),
    create_router_client(),
    Arc::clone(&pool_id_arc),
    Arc::clone(&lambda_id_arc),
    Arc::new(std::sync::atomic::AtomicUsize::new(0)), // count is only used in log messages
    router_endpoint,
    deadline_ms,
    cancel_token,
    Arc::clone(&requests_in_flight),
    250,
  )
  .await;

  // Assert
  assert_eq!(
    result,
    Some(PingResult::LastActive),
    "result should be LastActive"
  );
  assert_eq!(
    ping_count.load(std::sync::atomic::Ordering::SeqCst),
    0,
    "ping count should be 0"
  );
  assert_eq!(
    close_count.load(std::sync::atomic::Ordering::SeqCst),
    1,
    "close count should be 1"
  );
  assert_eq!(
    requests_in_flight.load(std::sync::atomic::Ordering::SeqCst),
    0,
    "requests in flight should be 0"
  );
  assert_eq!(
    last_active.load(std::sync::atomic::Ordering::SeqCst),
    last_active_intial,
    "last active should not be 0"
  );
  assert_eq!(
    goaway_received.load(std::sync::atomic::Ordering::SeqCst),
    true,
    "goaway received should be true"
  );
}

#[tokio::test]
async fn test_ping_channel_cancel_token() {
  let lambda_id = "lambda_id".to_string();
  let pool_id = "pool_id".to_string();

  // Start router server
  // Use an arc int to count how many times the request endpoint was called
  let ping_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let ping_count_clone = Arc::clone(&ping_count);
  let close_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let close_count_clone = Arc::clone(&close_count);
  let app = Router::new()
    .route(
      "/api/chunked/ping/:lambda_id",
      get(|Path(lambda_id): Path<String>| async move {
        let ping_count = Arc::clone(&ping_count_clone);
        // Increment
        ping_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        format!("Ping! LambdaID: {}", lambda_id)
      }),
    )
    .route(
      "/api/chunked/close/:lambda_id",
      get(|Path(lambda_id): Path<String>| async move {
        let close_count = Arc::clone(&close_count_clone);
        // Increment
        close_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        format!("Close! LambdaID: {}", lambda_id)
      }),
    );
  let mock_router_server = run_http2_app(app);

  let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
    .parse()
    .unwrap();

  let pool_id_arc: PoolId = pool_id.clone().into();
  let lambda_id_arc: LambdaId = lambda_id.clone().into();

  // Declare the counts
  let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
  let last_active_intial = current_time_millis();
  let last_active = Arc::new(std::sync::atomic::AtomicU64::new(last_active_intial));
  let requests_in_flight_initial = 3;
  let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(
    requests_in_flight_initial,
  ));
  let cancel_token = tokio_util::sync::CancellationToken::new();
  let deadline_ms = current_time_millis() + 30000;

  // Spawn a task to flip the cancel token in 1 second
  let cancel_token_clone = cancel_token.clone();
  tokio::spawn(async move {
    tokio::time::sleep(Duration::from_millis(1000)).await;
    cancel_token_clone.cancel();
  });

  // Act
  let result = send_ping_requests(
    Arc::clone(&last_active),
    Arc::clone(&goaway_received),
    create_router_client(),
    Arc::clone(&pool_id_arc),
    Arc::clone(&lambda_id_arc),
    Arc::new(std::sync::atomic::AtomicUsize::new(0)), // count is only used in log messages
    router_endpoint,
    deadline_ms,
    cancel_token,
    Arc::clone(&requests_in_flight),
    250,
  )
  .await;

  // Assert
  assert_eq!(
    result,
    Some(PingResult::CancelToken),
    "result should be CancelToken"
  );
  assert_eq!(
    ping_count.load(std::sync::atomic::Ordering::SeqCst),
    0,
    "ping count should be 0"
  );
  assert_eq!(
    close_count.load(std::sync::atomic::Ordering::SeqCst),
    0,
    "close count should be 0"
  );
  assert_eq!(
    requests_in_flight.load(std::sync::atomic::Ordering::SeqCst),
    requests_in_flight_initial,
    "requests in flight"
  );
  assert_eq!(
    last_active.load(std::sync::atomic::Ordering::SeqCst),
    last_active_intial,
    "last active should not be 0"
  );
  assert_eq!(
    goaway_received.load(std::sync::atomic::Ordering::SeqCst),
    false,
    "goaway received should be false"
  );
}

#[tokio::test]
async fn test_ping_channel_status_305() {
  test_ping_status_code(StatusCode::USE_PROXY, Some(PingResult::StatusCodeOther)).await;
}

#[tokio::test]
async fn test_ping_channel_status_400() {
  test_ping_status_code(StatusCode::BAD_REQUEST, Some(PingResult::StatusCode4xx)).await;
}

#[tokio::test]
async fn test_ping_channel_status_409() {
  test_ping_status_code(StatusCode::CONFLICT, Some(PingResult::GoAway)).await;
}

#[tokio::test]
async fn test_ping_channel_status_500() {
  test_ping_status_code(
    StatusCode::INTERNAL_SERVER_ERROR,
    Some(PingResult::StatusCode5xx),
  )
  .await;
}

async fn test_ping_status_code(status_code: StatusCode, expected_result: Option<PingResult>) {
  let lambda_id = "lambda_id".to_string();
  let pool_id = "pool_id".to_string();

  // Start router server
  // Use an arc int to count how many times the request endpoint was called
  let ping_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let ping_count_clone = Arc::clone(&ping_count);
  let close_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let close_count_clone = Arc::clone(&close_count);
  let app = Router::new()
    .route(
      "/api/chunked/ping/:lambda_id",
      get(move |Path(lambda_id): Path<String>| async move {
        let ping_count = Arc::clone(&ping_count_clone);
        // Increment
        ping_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        (status_code, format!("Ping! LambdaID: {}", lambda_id))
      }),
    )
    .route(
      "/api/chunked/close/:lambda_id",
      get(|Path(lambda_id): Path<String>| async move {
        let close_count = Arc::clone(&close_count_clone);
        // Increment
        close_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        format!("Close! LambdaID: {}", lambda_id)
      }),
    );
  let mock_router_server = run_http2_app(app);

  let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
    .parse()
    .unwrap();

  let pool_id_arc: PoolId = pool_id.clone().into();
  let lambda_id_arc: LambdaId = lambda_id.clone().into();

  // Declare the counts
  let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
  let last_active_intial = current_time_millis();
  let last_active = Arc::new(std::sync::atomic::AtomicU64::new(last_active_intial));
  let requests_in_flight_initial = 3;
  let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(
    requests_in_flight_initial,
  ));
  let cancel_token = tokio_util::sync::CancellationToken::new();
  let deadline_ms = current_time_millis() + 30000;

  // Spawn a task to flip the cancel token in 1 second
  let cancel_token_clone = cancel_token.clone();
  tokio::spawn(async move {
    tokio::time::sleep(Duration::from_millis(5500)).await;
    cancel_token_clone.cancel();
  });

  // Act
  let result = send_ping_requests(
    Arc::clone(&last_active),
    Arc::clone(&goaway_received),
    create_router_client(),
    Arc::clone(&pool_id_arc),
    Arc::clone(&lambda_id_arc),
    Arc::new(std::sync::atomic::AtomicUsize::new(0)), // count is only used in log messages
    router_endpoint,
    deadline_ms,
    cancel_token,
    Arc::clone(&requests_in_flight),
    250,
  )
  .await;

  // Assert
  assert_eq!(result, expected_result, "result expected");
  assert_eq!(
    ping_count.load(std::sync::atomic::Ordering::SeqCst),
    1,
    "ping count should be 1"
  );
  assert_eq!(
    close_count.load(std::sync::atomic::Ordering::SeqCst),
    0,
    "close count should be 0"
  );
  assert_eq!(
    requests_in_flight.load(std::sync::atomic::Ordering::SeqCst),
    requests_in_flight_initial,
    "requests in flight"
  );
  assert_eq!(
    last_active.load(std::sync::atomic::Ordering::SeqCst),
    last_active_intial,
    "last active should not be 0"
  );
  assert_eq!(
    goaway_received.load(std::sync::atomic::Ordering::SeqCst),
    true,
    "goaway received should be true"
  );
}

#[tokio::test]
async fn test_ping_channel_connection_closed() {
  let lambda_id = "lambda_id".to_string();
  let pool_id = "pool_id".to_string();

  // Start router server
  // Use an arc int to count how many times the request endpoint was called
  let ping_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let ping_count_clone = Arc::clone(&ping_count);
  let close_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
  let close_count_clone = Arc::clone(&close_count);
  let app = Router::new()
    .route(
      "/api/chunked/ping/:lambda_id",
      get(|Path(_lambda_id): Path<String>| async move {
        let ping_count = Arc::clone(&ping_count_clone);
        // Increment
        ping_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        panic!("Connection closed")
      }),
    )
    .route(
      "/api/chunked/close/:lambda_id",
      get(|Path(lambda_id): Path<String>| async move {
        let close_count = Arc::clone(&close_count_clone);
        // Increment
        close_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        format!("Close! LambdaID: {}", lambda_id)
      }),
    );
  let mock_router_server = run_http2_app(app);

  let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
    .parse()
    .unwrap();

  let pool_id_arc: PoolId = pool_id.clone().into();
  let lambda_id_arc: LambdaId = lambda_id.clone().into();

  // Declare the counts
  let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
  let last_active_intial = current_time_millis();
  let last_active = Arc::new(std::sync::atomic::AtomicU64::new(last_active_intial));
  let requests_in_flight_initial = 3;
  let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(
    requests_in_flight_initial,
  ));
  let cancel_token = tokio_util::sync::CancellationToken::new();
  let deadline_ms = current_time_millis() + 30000;

  let router_client = create_router_client();

  // Act
  let result = send_ping_requests(
    Arc::clone(&last_active),
    Arc::clone(&goaway_received),
    router_client.clone(),
    Arc::clone(&pool_id_arc),
    Arc::clone(&lambda_id_arc),
    Arc::new(std::sync::atomic::AtomicUsize::new(0)), // count is only used in log messages
    router_endpoint,
    deadline_ms,
    cancel_token,
    Arc::clone(&requests_in_flight),
    250,
  )
  .await;

  // Assert
  assert_eq!(
    result,
    Some(PingResult::ConnectionError),
    "result should be ConnectionError"
  );
  assert_eq!(
    ping_count.load(std::sync::atomic::Ordering::SeqCst),
    1,
    "ping count should be 1"
  );
  assert_eq!(
    close_count.load(std::sync::atomic::Ordering::SeqCst),
    0,
    "close count should be 0"
  );
  assert_eq!(
    requests_in_flight.load(std::sync::atomic::Ordering::SeqCst),
    requests_in_flight_initial,
    "requests in flight"
  );
  assert_eq!(
    last_active.load(std::sync::atomic::Ordering::SeqCst),
    last_active_intial,
    "last active should not be 0"
  );
  assert_eq!(
    goaway_received.load(std::sync::atomic::Ordering::SeqCst),
    true,
    "goaway received should be true"
  );
}
