use crate::{lambda_request_error::LambdaRequestError, lambda_service::AppClient, prelude::*};

use std::sync::atomic::AtomicBool;
use std::sync::Arc;
use std::time::Duration;

use futures::channel::mpsc;
use http_body_util::{BodyExt, StreamBody};
use hyper::{
  body::{Bytes, Frame},
  Request, StatusCode, Uri,
};

async fn send_healthcheck(
  app_client: &AppClient,
  healthcheck_url: &Uri,
) -> Result<(), LambdaRequestError> {
  let (mut app_req_tx, app_req_recv) = mpsc::channel::<Result<Frame<Bytes>>>(32 * 1024);
  let app_req = Request::builder()
    .method("GET")
    .uri(healthcheck_url)
    .header(hyper::header::HOST, "localhost")
    .body(StreamBody::new(app_req_recv))
    .map_err(|_| LambdaRequestError::ChannelErrorOther)?;

  // Close the body - we are not sending one
  let _ = app_req_tx.close_channel();

  match app_client.request(app_req).await {
    Ok(app_res) => {
      let (parts, mut res_stream) = app_res.into_parts();

      // Rip through and discard so the response stream is closed
      while res_stream.frame().await.is_some() {}

      // Check status code and discard body
      if parts.status != StatusCode::OK {
        log::debug!("Health check - Failed: {:?}\nHeaders:", parts.status);
        for header in parts.headers.iter() {
          log::debug!("  {}: {}", header.0, header.1.to_str().unwrap());
        }

        return Err(LambdaRequestError::AppConnectionError);
      }

      log::debug!("Health check - Send request to contained app success");

      Ok(())
    }
    Err(err) => {
      if err.is_connect() {
        log::debug!("Health check - Failed to connect to contained app: {}", err);
        Err(LambdaRequestError::AppConnectionError)
      } else {
        log::debug!(
          "Health check - Failed to send request to contained app: {}",
          err
        );
        Err(LambdaRequestError::ChannelErrorOther)
      }
    }
  }
}

pub async fn health_check_contained_app(
  goaway_received: Arc<AtomicBool>,
  healthcheck_url: &Uri,
  app_client: &AppClient,
) -> bool {
  log::info!(
    "Health check - Starting for contained app at: {}",
    healthcheck_url.to_string()
  );

  while !goaway_received.load(std::sync::atomic::Ordering::Acquire) {
    tokio::time::sleep(Duration::from_millis(10)).await;

    match send_healthcheck(app_client, healthcheck_url).await {
      Ok(_) => {
        log::info!("Health check - Complete - Success");
        return true;
      }
      Err(_) => {
        log::debug!("Health check - Failed");
      }
    }
  }

  log::info!("Health check - Complete - Goaway received");
  false
}

#[cfg(test)]
mod tests {
  use super::*;

  use httpmock::{Method::GET, MockServer};

  use std::{
    sync::{
      atomic::{AtomicBool, AtomicUsize, Ordering},
      Arc,
    },
    time::Duration,
  };

  use bytes::Bytes;
  use futures::channel::mpsc::Receiver;
  use http_body_util::StreamBody;
  use hyper::{body::Frame, Uri};
  use hyper_util::{
    client::legacy::{connect::HttpConnector, Client},
    rt::{TokioExecutor, TokioTimer},
  };

  fn setup_app_client() -> Client<HttpConnector, StreamBody<Receiver<Result<Frame<Bytes>, Error>>>>
  {
    let mut http_connector = HttpConnector::new();
    http_connector.set_connect_timeout(Some(Duration::from_secs(2)));
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

    let mock_app_healthcheck_url: Uri =
      format!("{}/health", mock_app_server.uri()).parse().unwrap();
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

    assert_eq!(result, false, "Health check should fail");
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

    assert_eq!(result, false, "Health check should fail");
    assert!(
      duration >= std::time::Duration::from_secs(2),
      "Connection should take at least 2 seconds"
    );
    assert!(
      duration <= std::time::Duration::from_secs(3),
      "Connection should take at most 3 seconds"
    );
  }
}
