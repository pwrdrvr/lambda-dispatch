use std::{pin::Pin, sync::Arc};

use http_body_util::combinators::BoxBody;
use hyper::{
  body::Bytes,
  client::conn::http2::{self, SendRequest},
};
use hyper_util::rt::{TokioExecutor, TokioIo};
use rustls_pki_types::ServerName;
use tokio::{
  io::{AsyncRead, AsyncWrite},
  net::TcpStream,
};
use tokio_rustls::{client::TlsStream, TlsConnector};

use crate::cert::AcceptAnyServerCert;
use crate::endpoint::{Endpoint, Scheme};
use crate::prelude::*;

// Define a Stream trait that both TlsStream and TcpStream implement
pub trait Stream: AsyncRead + AsyncWrite + Send {}
impl Stream for TlsStream<TcpStream> {}
impl Stream for TcpStream {}

pub async fn connect_to_router(
  router_endpoint: Endpoint,
  pool_id: PoolId,
  lambda_id: LambdaId,
) -> Result<SendRequest<BoxBody<Bytes, Error>>, Error> {
  let timeout_duration = tokio::time::Duration::from_secs(5);
  let tcp_stream_result = tokio::time::timeout(
    timeout_duration,
    TcpStream::connect(router_endpoint.socket_addr_coercable()),
  )
  .await;
  let tcp_stream = match tcp_stream_result {
    Ok(Ok(stream)) => {
      stream.set_nodelay(true)?;
      Some(stream)
    }
    Ok(Err(err)) => {
      // Connection error
      return Err(anyhow::anyhow!("TcpStream::connect error: {}", err));
    }
    Err(err) => {
      // Timeout
      return Err(anyhow::anyhow!("TcpStream::connect timed out: {}", err));
    }
  }
  .expect("Failed to create TCP stream");

  let stream: Box<dyn Stream + Unpin> = match router_endpoint.scheme() {
    Scheme::Https => {
      let mut root_cert_store = rustls::RootCertStore::empty();
      for cert in rustls_native_certs::load_native_certs()? {
        root_cert_store.add(cert).ok(); // ignore error
      }
      let mut config = rustls::ClientConfig::builder()
        .with_root_certificates(root_cert_store)
        .with_no_client_auth();
      // We're going to accept non-validatable certificates
      config
        .dangerous()
        .set_certificate_verifier(Arc::new(AcceptAnyServerCert));
      // Advertise http2
      config.alpn_protocols = vec![b"h2".to_vec()];
      let connector = TlsConnector::from(Arc::new(config));
      let domain = ServerName::try_from(router_endpoint)?;
      let timeout_duration = tokio::time::Duration::from_secs(2);
      let tls_stream_result = connector.connect(domain, tcp_stream);
      let tls_stream = match tokio::time::timeout(timeout_duration, tls_stream_result).await {
        Ok(Ok(stream)) => stream,
        Ok(Err(err)) => return Err(anyhow::anyhow!("TLS handshake failed: {}", err)),
        Err(_) => return Err(anyhow::anyhow!("Router TLS handshake timed out")),
      };
      Box::new(tls_stream)
    }
    Scheme::Http => Box::new(tcp_stream),
  };

  let io = TokioIo::new(Pin::new(stream));

  // Setup the HTTP2 connection
  let http2_handshake_future = http2::handshake(TokioExecutor::new(), io);

  // let (sender, conn)
  // Wait for the HTTP2 handshake to complete or timeout
  let timeout_duration = tokio::time::Duration::from_secs(2);
  let (sender, connection) =
    match tokio::time::timeout(timeout_duration, http2_handshake_future).await {
      Ok(Ok((sender, connection))) => (sender, connection), // Handshake completed successfully
      Ok(Err(err)) => {
        log::error!(
          "PoolId: {}, LambdaId: {} - Router HTTP2 connection failed: {:?}",
          pool_id,
          lambda_id,
          err
        );
        return Err(anyhow::anyhow!("Router HTTP2 connection failed: {:?}", err));
      }
      Err(_) => {
        log::error!(
          "PoolId: {}, LambdaId: {} - Router HTTP2 connection timed out",
          pool_id,
          lambda_id,
        );
        return Err(anyhow::anyhow!("Router HTTP2 connection timed out"));
      }
    };

  // TODO: sender.ready() on HTTP2 returns immediately even on a TCP server
  // that does absolutely nothing other than send back SYN-ACK.
  // let timeout_duration = tokio::time::Duration::from_secs(2);
  // match tokio::time::timeout(timeout_duration, sender.ready()).await {
  //   Ok(Ok(_)) => {
  //     print!("Connection ready");
  //   } // The ready check completed successfully
  //   Ok(Err(e)) => {
  //     log::error!(
  //       "PoolId: {}, LambdaId: {} - Ready check failed: {:?}",
  //       pool_id,
  //       lambda_id,
  //       e
  //     );
  //     return Err(anyhow::anyhow!("Router HTTP2 ready check failed"));
  //   }
  //   Err(_) => {
  //     log::error!(
  //       "PoolId: {}, LambdaId: {} - Ready check timed out",
  //       pool_id,
  //       lambda_id,
  //     );
  //     return Err(anyhow::anyhow!("Router HTTP2 ready check timed out"));
  //   }
  // }

  // This task just keeps the connection from being dropped
  // TODO: Let's return this and hold it elsewhere
  tokio::task::spawn(async move {
    if let Err(err) = connection.await {
      log::error!(
        "PoolId: {}, LambdaId: {} - Router HTTP2 connection failed: {:?}",
        pool_id,
        lambda_id,
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
  use std::sync::Arc;
  use tokio::net::TcpListener;

  async fn start_mock_server() -> (TcpListener, u16) {
    let listener = TcpListener::bind(("127.0.0.1", 0)).await.unwrap();
    let port = listener.local_addr().unwrap().port();
    (listener, port)
  }

  #[tokio::test]
  async fn test_connect_to_router_dns_failure() {
    let router_endpoint = Endpoint::new(
      Scheme::Http,
      "nonexistent-subdomain-12345.example.com",
      12345,
    );
    let pool_id = Arc::from("pool_id");
    let lambda_id = Arc::from("lambda_id");

    // Act
    let start = std::time::Instant::now();
    let sender = connect_to_router(
      router_endpoint,
      Arc::clone(&pool_id),
      Arc::clone(&lambda_id),
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
  async fn test_connect_to_router_blackhole_timeout() {
    // 192.0.2.0/24 (TEST-NET-1)
    let router_endpoint = Endpoint::new(Scheme::Http, "192.0.2.0", 12345);
    let pool_id = Arc::from("pool_id");
    let lambda_id = Arc::from("lambda_id");

    // Act
    let start = std::time::Instant::now();
    let sender = connect_to_router(
      router_endpoint,
      Arc::clone(&pool_id),
      Arc::clone(&lambda_id),
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
      duration <= std::time::Duration::from_secs(6),
      "Connection should take at most 6 seconds"
    );
  }

  #[tokio::test]
  async fn test_connect_to_router_insecure_http2_timeout() {
    // Start the mock server
    let (_listener, port) = start_mock_server().await;

    let router_endpoint = Endpoint::new(Scheme::Http, "localhost", port);
    let pool_id = Arc::from("pool_id");
    let lambda_id = Arc::from("lambda_id");

    // Act
    let start = std::time::Instant::now();
    let sender = connect_to_router(
      router_endpoint,
      Arc::clone(&pool_id),
      Arc::clone(&lambda_id),
    )
    .await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    match sender {
      // Yeah, this is weird, HTTP2 reports ready even for dummy server
      Ok(_) => assert!(sender.is_ok(), "Connection should be established"),
      Err(e) => assert_eq!(e.to_string(), "Router HTTP2 connection timed out"),
    }
    assert!(
      duration <= std::time::Duration::from_secs(2),
      "Connection should take at most 2 seconds"
    );
  }

  #[tokio::test]
  async fn test_connect_to_router_secure_http2_timeout() {
    // Start the mock server
    let (_listener, port) = start_mock_server().await;

    let router_endpoint = Endpoint::new(Scheme::Https, "localhost", port);
    let pool_id = Arc::from("pool_id");
    let lambda_id = Arc::from("lambda_id");

    // Act
    let start = std::time::Instant::now();
    let sender = connect_to_router(
      router_endpoint,
      Arc::clone(&pool_id),
      Arc::clone(&lambda_id),
    )
    .await;
    let duration = std::time::Instant::now().duration_since(start);

    // Assert
    match sender {
      Ok(_) => assert!(sender.is_err(), "Connection should not be established"),
      Err(e) => assert_eq!(e.to_string(), "Router TLS handshake timed out"),
    }
    assert!(
      duration >= std::time::Duration::from_secs(2),
      "Connection should take at least 2 seconds"
    );
    assert!(
      duration <= std::time::Duration::from_secs(4),
      "Connection should take at most 4 seconds"
    );
  }

  // #[tokio::test]
  // // #[ntest::timeout(15_000)] // timeout at 15 seconds
  // async fn test_connect_to_router_timeout() {
  //   // Start the mock server
  //   let (_listener, port) = start_mock_server().await;

  //   let router_endpoint = Endpoint::new(Scheme::Http, "localhost", port);
  //   let pool_id = Arc::from("pool_id");
  //   let lambda_id = Arc::from("lambda_id");
  //   let sender = connect_to_router(
  //     router_endpoint,
  //     Arc::clone(&pool_id),
  //     Arc::clone(&lambda_id),
  //   )
  //   .await;
  //   assert!(
  //     sender.is_err(),
  //     "PoolId: {}, LambdaId: {} - Connection could not be established",
  //     pool_id,
  //     lambda_id
  //   );
  //   let mut sender = sender.unwrap();

  //   //
  //   // NOTE: None of the stuff below gets hit because we never establish the connection
  //   //
  //   let (mut ping_tx, ping_recv) = mpsc::channel::<anyhow::Result<Frame<Bytes>>>(1);
  //   let boxed_ping_body = BodyExt::boxed(StreamBody::new(ping_recv));
  //   let ping_req = Request::builder().uri("http://localhost/").method("GET");
  //   // let ping_req = match &host_header {
  //   //   Cow::Borrowed(v) => ping_req.header(hyper::header::HOST, *v),
  //   //   Cow::Owned(v) => ping_req.header(hyper::header::HOST, v),
  //   // };
  //   let ping_req = ping_req.body(boxed_ping_body).unwrap();

  //   assert!(sender.ready().await.is_ok(), "PoolId: {}, LambdaId: {} - Connection ready check threw error - connection has disconnected, should reconnect", pool_id, lambda_id);

  //   let result = sender.send_request(ping_req).await;
  //   ping_tx.close().await.unwrap();

  //   // Check if we got a timeout error as expected
  //   match result {
  //     Ok(_) => panic!("Expected a timeout error, but the request succeeded"),
  //     Err(e) => assert!(e.is_timeout()),
  //   }
  // }
}
