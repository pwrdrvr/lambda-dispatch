use crate::prelude::*;

use futures::channel::mpsc;
use http_body_util::StreamBody;
use hyper::{
  body::{Bytes, Frame},
  client::conn::http1,
};
use hyper_util::rt::TokioIo;
use mpsc::Receiver;
use tokio::io::Interest;
use tokio::net::TcpStream;

use crate::endpoint::Endpoint;

pub async fn connect_to_app(
  app_endpoint: &Endpoint,
  pool_id: PoolId,
  lambda_id: LambdaId,
  channel_id: ChannelId,
) -> Result<http1::SendRequest<StreamBody<Receiver<Result<Frame<Bytes>>>>>> {
  // Setup the contained app connection
  // This is HTTP/1.1 so we need 1 connection for each worker
  let timeout_duration = tokio::time::Duration::from_secs(5);
  let tcp_stream_result = tokio::time::timeout(
    timeout_duration,
    TcpStream::connect(app_endpoint.socket_addr_coercable()),
  )
  .await;
  let tcp_stream = match tcp_stream_result {
    Ok(Ok(stream)) => {
      stream.set_nodelay(true)?;
      Some(stream)
    }
    Ok(Err(err)) => {
      // Connection error
      log::error!(
        "PoolId: {}, LambdaId: {}, ChannelId: {} - Contained App TcpStream::connect error: {}, endpoint: {}",
        pool_id,
        lambda_id,
        channel_id,
        err,
        app_endpoint
      );
      return Err(anyhow::anyhow!("TcpStream::connect error: {}", err));
    }
    Err(err) => {
      // Timeout
      log::error!(
        "PoolId: {}, LambdaId: {}, ChannelId: {} - Contained App TcpStream::connect timed out: {}, endpoint: {}",
        pool_id,
        lambda_id,
        channel_id,
        err,
        app_endpoint
      );
      return Err(anyhow::anyhow!("TcpStream::connect timed out: {}", err));
    }
  }
  .expect("Failed to create TCP stream");

  let deadline = tokio::time::Instant::now() + tokio::time::Duration::from_secs(2);
  loop {
    let timeout_duration = deadline.saturating_duration_since(tokio::time::Instant::now());
    let ready_check = tcp_stream.ready(Interest::WRITABLE);
    match tokio::time::timeout(timeout_duration, ready_check).await {
      Ok(Ok(ready)) => {
        if ready.is_writable() {
          break; // Readiness check completed successfully
        }
      }
      Ok(Err(err)) => {
        // Readiness check returned an error
        log::error!(
          "PoolId: {}, LambdaId: {}, ChannelId: {} - Contained App TCP connection readiness check failed: {}, endpoint: {}",
          pool_id,
          lambda_id,
          channel_id,
          err,
          app_endpoint
        );
        return Err(err.into());
      }
      Err(_) => {
        // Timeout
        log::error!(
          "PoolId: {}, LambdaId: {}, ChannelId: {} - Contained App TCP connection readiness check timed out, endpoint: {}",
          pool_id,
          lambda_id,
          channel_id,
          app_endpoint
        );
        return Err(anyhow::anyhow!("TCP connection readiness check timed out"));
      }
    }
  }

  let io: TokioIo<TcpStream> = TokioIo::new(tcp_stream);
  let http1_handshake_future = hyper::client::conn::http1::handshake(io);

  // Wait for the HTTP1 handshake to complete or timeout
  let timeout_duration = tokio::time::Duration::from_secs(2);
  let (sender, connection) = match tokio::time::timeout(timeout_duration, http1_handshake_future)
    .await
  {
    Ok(Ok((sender, connection))) => (sender, connection), // Handshake completed successfully
    Ok(Err(err)) => {
      log::error!(
        "PoolId: {}, LambdaId: {}, ChannelId: {} - Contained App HTTP connection could not be established: {}, endpoint: {}",
        pool_id,
        lambda_id,
        channel_id,
        err,
        app_endpoint
      );
      return Err(anyhow::anyhow!(
        "Contained App HTTP connection could not be established: {}",
        err
      ));
    }
    Err(_) => {
      log::error!(
        "PoolId: {}, LambdaId: {}, ChannelId: {} - Contained App HTTP connection timed out, endpoint: {}",
        pool_id,
        lambda_id,
        channel_id,
        app_endpoint
      );
      return Err(anyhow::anyhow!("Contained App HTTP connection timed out"));
    }
  };

  // For HTTP1, the ready check never returns
  // let timeout_duration = tokio::time::Duration::from_secs(2);
  // match tokio::time::timeout(timeout_duration, sender.ready()).await {
  //   Ok(Ok(_)) => {} // The ready check completed successfully
  //   Ok(Err(e)) => {
  //     log::error!(
  //       "PoolId: {}, LambdaId: {}, ChannelId: {} - Contained App ready check failed: {:?}",
  //       pool_id,
  //       lambda_id,
  //       channel_id,
  //       e
  //     );
  //     return Err(anyhow::anyhow!("Contained App ready check failed"));
  //   }
  //   Err(_) => {
  //     log::error!(
  //       "PoolId: {}, LambdaId: {}, ChannelId: {} - Contained App ready check timed out",
  //       pool_id,
  //       lambda_id,
  //       channel_id
  //     );
  //     return Err(anyhow::anyhow!("Contained App ready check timed out"));
  //   }
  // }

  // This task just keeps the connection from being dropped
  // TODO: Let's return this and hold it elsewhere
  tokio::task::spawn(async move {
    if let Err(err) = connection.await {
      log::error!(
        "PoolId: {}, LambdaId: {}, ChannelId: {} - Contained App HTTP connection failed: {}",
        pool_id,
        lambda_id,
        channel_id,
        err
      );
    }
  });

  Ok(sender)
}

#[cfg(test)]
mod tests {
  use super::*;

  use crate::endpoint::{Endpoint, Scheme};
  use httpmock::{Method::GET, MockServer};
  use std::sync::Arc;
  use tokio::net::TcpListener;

  async fn start_mock_tcp_server() -> (TcpListener, u16) {
    let listener = TcpListener::bind(("127.0.0.1", 0)).await.unwrap();
    let port = listener.local_addr().unwrap().port();
    (listener, port)
  }

  #[tokio::test]
  async fn test_connect_to_app_dns_failure() {
    let app_endpoint = Endpoint::new(
      Scheme::Http,
      "nonexistent-subdomain-12345.example.com",
      12345,
    );
    let pool_id = Arc::from("pool_id");
    let lambda_id = Arc::from("lambda_id");
    let channel_id = Arc::from("channel_id");

    // Act
    let start = std::time::Instant::now();
    let sender = connect_to_app(
      &app_endpoint,
      Arc::clone(&pool_id),
      Arc::clone(&lambda_id),
      Arc::clone(&channel_id),
    )
    .await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    match sender {
      Ok(_) => assert!(sender.is_err(), "Connection should not be established"),
      Err(e) => {
        let error_message = e.to_string();
        let expected_prefix = "TcpStream::connect error: failed to lookup address information:";
        let actual_prefix = &error_message[..expected_prefix.len()];
        assert_eq!(
          actual_prefix, expected_prefix,
          "Unexpected error message. Expected to start with: '{}', but was: '{}'",
          expected_prefix, error_message
        );
      }
    }
    assert!(
      duration <= std::time::Duration::from_secs(6),
      "Connection should take at most 6 seconds"
    );
  }

  #[tokio::test]
  async fn test_connect_to_app_blackhole_timeout() {
    // 192.0.2.0/24 (TEST-NET-1)
    let router_endpoint = Endpoint::new(Scheme::Http, "192.0.2.0", 12345);
    let pool_id = Arc::from("pool_id");
    let lambda_id = Arc::from("lambda_id");
    let channel_id = Arc::from("channel_id");

    // Act
    let start = std::time::Instant::now();
    let sender = connect_to_app(
      &router_endpoint,
      Arc::clone(&pool_id),
      Arc::clone(&lambda_id),
      Arc::clone(&channel_id),
    )
    .await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    match sender {
      Ok(_) => assert!(sender.is_err(), "Connection should not be established"),
      Err(e) => assert_eq!(
        e.to_string(),
        "TcpStream::connect timed out: deadline has elapsed"
      ),
    }
    assert!(
      duration >= std::time::Duration::from_secs(5),
      "Connection should take at least 5 seconds"
    );
    assert!(
      duration <= std::time::Duration::from_secs(7),
      "Connection should take at most 7 seconds"
    );
  }

  #[tokio::test]
  #[ignore = "http1 sender.ready() does not return when checked"]
  async fn test_connect_to_app_http1_establishment_timeout() {
    // Start the mock server
    let (_listener, port) = start_mock_tcp_server().await;

    let router_endpoint = Endpoint::new(Scheme::Http, "localhost", port);
    let pool_id = Arc::from("pool_id");
    let lambda_id = Arc::from("lambda_id");
    let channel_id = Arc::from("channel_id");

    // Act
    let start = std::time::Instant::now();
    let sender = connect_to_app(
      &router_endpoint,
      Arc::clone(&pool_id),
      Arc::clone(&lambda_id),
      Arc::clone(&channel_id),
    )
    .await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    match sender {
      Ok(_) => assert!(sender.is_ok(), "Connection should not be established"),
      Err(e) => assert_eq!(e.to_string(), "Contained App ready check timed out"),
    }
    assert!(
      duration <= std::time::Duration::from_secs(2),
      "Connection should take at most 2 seconds"
    );
  }

  #[tokio::test]
  async fn test_connect_to_app_valid_http() {
    // Start app server
    let app_server = MockServer::start();
    let app_healthcheck_mock = app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });

    let router_endpoint = Endpoint::new(Scheme::Http, "localhost", app_server.port());
    let pool_id = Arc::from("pool_id");
    let lambda_id = Arc::from("lambda_id");
    let channel_id = Arc::from("channel_id");

    // Act
    let start = std::time::Instant::now();
    let sender = connect_to_app(
      &router_endpoint,
      Arc::clone(&pool_id),
      Arc::clone(&lambda_id),
      Arc::clone(&channel_id),
    )
    .await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    match sender {
      Ok(_) => assert!(sender.is_ok(), "Connection should be established"),
      Err(e) => assert_eq!(e.to_string(), "Contained App ready check timed out"),
    }
    assert!(
      duration <= std::time::Duration::from_secs(2),
      "Connection should take at most 2 seconds"
    );

    // Assert app server health check route didn't get called
    app_healthcheck_mock.assert_hits(0);
  }
}
