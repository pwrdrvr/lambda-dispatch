use httpmock::{Method::GET, MockServer};
use hyper::Uri;
use hyper_util::{
  client::legacy::{connect::HttpConnector, Client},
  rt::{TokioExecutor, TokioTimer},
};
use std::{
  sync::{
    atomic::{AtomicBool, AtomicUsize, Ordering},
    Arc,
  },
  time::Duration,
};

use extension::{app_client::AppClient, app_start::*};

fn setup_app_client() -> AppClient {
  let mut http_connector = HttpConnector::new();
  http_connector.set_connect_timeout(Some(Duration::from_millis(500)));
  http_connector.set_nodelay(true);
  Client::builder(TokioExecutor::new())
    .pool_idle_timeout(Duration::from_secs(5))
    .pool_max_idle_per_host(100)
    .pool_timer(TokioTimer::new())
    .retry_canceled_requests(false)
    .build(http_connector)
}

#[tokio::test]
async fn test_health_check_contained_app_success_immediate() {
  // Start app server
  let mock_app_server = MockServer::start();
  let mock_app_healthcheck = mock_app_server.mock(|when, then| {
    when.method(GET).path("/health");
    then.status(200).body("OK");
  });
  let mock_app_root = mock_app_server.mock(|when, then| {
    when.method(GET).path("/");
    then.status(200).body("OK");
  });
  let mock_app_healthcheck_url: Uri = format!("{}/health", mock_app_server.base_url())
    .parse()
    .unwrap();
  let goaway_received = Arc::new(AtomicBool::new(false));
  let app_client = setup_app_client();

  // Act
  let start = std::time::Instant::now();
  let result =
    health_check_contained_app(goaway_received, &mock_app_healthcheck_url, &app_client).await;
  let duration = start.elapsed();

  // Assert
  assert!(result, "Health check failed");
  mock_app_healthcheck.assert_hits(1);
  mock_app_root.assert_hits(0);
  assert!(
    duration <= std::time::Duration::from_secs(1),
    "Connection should take at most 1 seconds, took: {:?}",
    duration
  );
}

#[tokio::test]
async fn test_health_check_contained_app_success_delayed_ok() {
  // Start app server
  let mock_app_server = wiremock::MockServer::start().await;

  // Arrange the behaviour of the MockServer adding a Mock:
  // when it receives a GET request on '/hello' it will respond with a 200.
  let hits = Arc::new(AtomicUsize::new(0));
  let hits_clone = Arc::clone(&hits);
  wiremock::Mock::given(wiremock::matchers::method("GET"))
    .and(wiremock::matchers::path("/health"))
    .respond_with(move |_request: &'_ wiremock::Request| {
      // Increment the hit count
      hits_clone.fetch_add(1, Ordering::SeqCst);

      // Do not respond with healthy until we have hit the healthcheck endpoint 400 times
      if hits_clone.load(Ordering::SeqCst) <= 400 {
        wiremock::ResponseTemplate::new(404).set_body_raw("Not Yet", "text/plain")
      } else {
        wiremock::ResponseTemplate::new(200).set_body_raw("OK", "text/plain")
      }
    })
    .expect(401)
    .mount(&mock_app_server)
    .await;

  let mock_app_healthcheck_url: Uri = format!("{}/health", mock_app_server.uri()).parse().unwrap();
  let goaway_received = Arc::new(AtomicBool::new(false));
  let app_client = setup_app_client();

  // Act
  let start = std::time::Instant::now();
  let result =
    health_check_contained_app(goaway_received, &mock_app_healthcheck_url, &app_client).await;
  let duration = start.elapsed();

  // Assert
  assert!(result, "Health check failed");
  assert!(
    duration >= std::time::Duration::from_secs(4),
    "Connection should take at least 4 seconds, took: {:?}",
    duration
  );
  assert!(
    duration <= std::time::Duration::from_secs(6),
    "Connection should take at most 6 seconds, took: {:?}",
    duration
  );
}

#[tokio::test]
async fn test_health_check_contained_app_blackhole() {
  // 192.0.2.0/24 (TEST-NET-1)
  let mock_app_healthcheck_url = "http://192.0.2.0:54321/health".parse().unwrap();
  let goaway_received = Arc::new(AtomicBool::new(false));
  let app_client = setup_app_client();

  // Setup async firing of goaway
  let goaway_received_clone = Arc::clone(&goaway_received);
  tokio::spawn(async move {
    tokio::time::sleep(Duration::from_secs(2)).await;
    goaway_received_clone.store(true, std::sync::atomic::Ordering::Release);
  });

  // Act
  let start = std::time::Instant::now();
  let result =
    health_check_contained_app(goaway_received, &mock_app_healthcheck_url, &app_client).await;
  let duration = start.elapsed();

  assert!(!result, "Health check should fail");
  assert!(
    duration >= std::time::Duration::from_secs(2),
    "Connection should take at least 2 seconds"
  );
  assert!(
    duration <= std::time::Duration::from_secs(3),
    "Connection should take at most 3 seconds"
  );
}

#[tokio::test]
async fn test_health_check_contained_app_error() {
  let mock_app_healthcheck_url = "http://127.0.0.1:54321/health".parse().unwrap();
  let goaway_received = Arc::new(AtomicBool::new(false));
  let app_client = setup_app_client();

  // Setup async firing of goaway
  let goaway_received_clone = Arc::clone(&goaway_received);
  tokio::spawn(async move {
    tokio::time::sleep(Duration::from_secs(2)).await;
    goaway_received_clone.store(true, std::sync::atomic::Ordering::Release);
  });

  // Act
  let start = std::time::Instant::now();
  let result =
    health_check_contained_app(goaway_received, &mock_app_healthcheck_url, &app_client).await;
  let duration = start.elapsed();

  assert!(!result, "Health check should fail");
  assert!(
    duration >= std::time::Duration::from_secs(2),
    "Connection should take at least 2 seconds"
  );
  assert!(
    duration <= std::time::Duration::from_secs(3),
    "Connection should take at most 3 seconds"
  );
}
