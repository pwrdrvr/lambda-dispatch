use crate::{lambda_service::AppClient, prelude::*};

use std::sync::atomic::AtomicBool;
use std::sync::Arc;
use std::time::Duration;

use http_body_util::Empty;
use hyper::{
  body::{Bytes, Incoming},
  client::conn::http1,
  Request, Uri,
};
use hyper_util::rt::TokioIo;
use tokio::net::TcpStream;

async fn create_connection(
  healthcheck_addr: &str,
) -> Result<(
  http1::SendRequest<Empty<Bytes>>,
  http1::Connection<TokioIo<TcpStream>, Empty<Bytes>>,
)> {
  // Setup the contained app connection
  // This is HTTP/1.1 so we need 1 connection for each worker
  // The endpoint is within the lambda so this should be very fast
  let timeout_duration = tokio::time::Duration::from_millis(500);
  let tcp_stream_result =
    tokio::time::timeout(timeout_duration, TcpStream::connect(healthcheck_addr)).await;
  let tcp_stream = match tcp_stream_result {
    Ok(Ok(stream)) => {
      stream.set_nodelay(true)?;
      Some(stream)
    }
    Ok(Err(err)) => {
      // Connection error
      return Err(anyhow::anyhow!(
        "Health check - TcpStream::connect error: {}",
        err
      ));
    }
    Err(err) => {
      // Timeout
      return Err(anyhow::anyhow!(
        "Health check - TcpStream::connect timed out: {}",
        err
      ));
    }
  }
  .expect("Health check - Failed to create TCP stream");

  let io: TokioIo<TcpStream> = TokioIo::new(tcp_stream);
  let http1_handshake_future = hyper::client::conn::http1::handshake(io);

  // Wait for the HTTP1 handshake to complete or timeout
  let timeout_duration = tokio::time::Duration::from_secs(2);
  let (sender, connection) =
    match tokio::time::timeout(timeout_duration, http1_handshake_future).await {
      Ok(Ok((sender, connection))) => (sender, connection), // Handshake completed successfully
      Ok(Err(err)) => {
        log::error!(
          "Health check - Contained App HTTP connection could not be established: {}, endpoint: {}",
          err,
          healthcheck_addr
        );
        return Err(anyhow::anyhow!(
          "Health check - Contained App HTTP connection could not be established: {}",
          err
        ));
      }
      Err(_) => {
        log::error!(
          "Health check - Contained App HTTP connection timed out, endpoint: {}",
          healthcheck_addr
        );
        return Err(anyhow::anyhow!(
          "Health check - Contained App HTTP connection timed out"
        ));
      }
    };

  Ok((sender, connection))
}

async fn send_request(
  sender: &mut http1::SendRequest<Empty<Bytes>>,
) -> Option<hyper::Response<Incoming>> {
  let req = Request::builder()
    .method("GET")
    .uri("/health")
    .header(hyper::header::HOST, "localhost")
    .body(http_body_util::Empty::<Bytes>::new())
    .unwrap();

  match sender.send_request(req).await {
    Err(err) => {
      log::debug!(
        "Health check - Failed to send request to contained app: {}",
        err
      );
      None
    }
    Ok(res) => {
      log::debug!("Health check - Send request to contained app success");
      Some(res)
    }
  }
}

pub async fn health_check_contained_app(
  goaway_received: Arc<AtomicBool>,
  healthcheck_url: &Uri,
  app_client: &AppClient,
) -> bool {
  let healthcheck_host = healthcheck_url.host().expect("uri has no host");
  let healthcheck_port = healthcheck_url.port_u16().unwrap_or(80);
  let healthcheck_addr = format!("{}:{}", healthcheck_host, healthcheck_port);

  log::info!(
    "Health check - Starting for contained app at: {}",
    healthcheck_url.to_string()
  );

  let mut sender = None;
  let mut conn = None;

  while !goaway_received.load(std::sync::atomic::Ordering::Acquire) {
    tokio::time::sleep(Duration::from_millis(10)).await;

    if sender.is_none() || conn.is_none() {
      match create_connection(&healthcheck_addr).await {
        Ok((s, c)) => {
          sender = Some(s);
          conn = Some(tokio::task::spawn(async move {
            if let Err(err) = c.await {
              log::error!("Health check - Connection failed: {}", err);
            }
          }));
        }
        Err(_) => {
          continue;
        }
      }
    }

    let usable_sender = sender.as_mut().unwrap();

    if usable_sender.ready().await.is_err() {
      // The connection has errored
      sender.take();
      conn.take();
      continue;
    }

    let res = send_request(usable_sender).await;

    if let Some(res) = res {
      let (parts, _) = res.into_parts();
      if parts.status == hyper::StatusCode::OK {
        log::info!("Health check - Complete - Success");
        return true;
      } else {
        log::debug!("Health check - Failed: {:?}\nHeaders:", parts.status);
        for header in parts.headers.iter() {
          log::debug!("  {}: {}", header.0, header.1.to_str().unwrap());
        }
      }
    } else {
      // The connection errored with a non-HTTP error
      sender.take();
      conn.take();
    }
  }

  log::info!("Health check - Complete - Goaway received");
  false
}

#[cfg(test)]
mod tests {
  use super::*;

  use httpmock::{Method::GET, MockServer};
  use tokio::time::timeout;
  use tokio_test::assert_err;

  use std::{
    sync::{atomic::AtomicBool, Arc},
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
  async fn test_health_check_contained_app_success() {
    // Start app server
    let mock_app_server = MockServer::start();
    let mock_app_healthcheck = mock_app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });
    let mock_app_healthcheck_url: Uri = format!("{}/health", mock_app_server.base_url())
      .parse()
      .unwrap();
    let goaway_received = Arc::new(AtomicBool::new(false));
    let app_client = setup_app_client();

    // Act
    let result =
      health_check_contained_app(goaway_received, &mock_app_healthcheck_url, &app_client).await;
    assert!(result, "Health check failed");

    // Assert app server's healthcheck endpoint got called
    mock_app_healthcheck.assert();
  }

  #[tokio::test]
  async fn test_health_check_contained_app_blackhole() {
    // 192.0.2.0/24 (TEST-NET-1)
    let mock_app_healthcheck_url = "http://192.0.2.0:54321/health".parse().unwrap();
    let goaway_received = Arc::new(AtomicBool::new(false));
    let app_client = setup_app_client();

    // Act
    let start = std::time::Instant::now();
    let result = timeout(
      Duration::from_secs(2),
      health_check_contained_app(goaway_received, &mock_app_healthcheck_url, &app_client),
    )
    .await;
    let duration = start.elapsed();

    assert_err!(result, "Health check should fail");
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

    // Act
    let start = std::time::Instant::now();
    let result = timeout(
      Duration::from_secs(2),
      health_check_contained_app(goaway_received, &mock_app_healthcheck_url, &app_client),
    )
    .await;
    let duration = start.elapsed();

    assert_err!(result, "Health check should fail");
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
