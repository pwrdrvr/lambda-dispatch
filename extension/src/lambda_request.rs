use rand::Rng;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::{pin::Pin, sync::Arc};

use http_body_util::combinators::BoxBody;
use hyper::{
  body::Bytes,
  client::conn::http2::{self, SendRequest},
};
use hyper_util::rt::{TokioExecutor, TokioIo};
use rand::SeedableRng;
use rustls_pki_types::ServerName;
use tokio::{
  io::{AsyncRead, AsyncWrite},
  net::TcpStream,
};
use tokio_rustls::{client::TlsStream, TlsConnector};

use crate::cert::AcceptAnyServerCert;
use crate::endpoint::{Endpoint, Scheme};
use crate::ping;
use crate::prelude::*;
use crate::router_channel::RouterChannel;
use crate::time::current_time_millis;

// Define a Stream trait that both TlsStream and TcpStream implement
pub trait Stream: AsyncRead + AsyncWrite + Send {}
impl Stream for TlsStream<TcpStream> {}
impl Stream for TcpStream {}

/// A `LambdaRequest` handles connecting back to the router, picking up requests, sending ping
/// requests to the router, and sending the requests to the contained app When an invoke completes
/// this is torn down completely
#[derive(Debug, Clone)]
pub struct LambdaRequest {
  app_endpoint: Endpoint,
  compression: bool,
  pool_id: PoolId,
  lambda_id: LambdaId,
  channel_count: u8,
  router_endpoint: Endpoint,
  cancel_token: tokio_util::sync::CancellationToken,
  deadline_ms: u64,
  goaway_received: Arc<AtomicBool>,
  last_active: Arc<AtomicU64>,
  rng: rand::rngs::StdRng,
  requests_in_flight: Arc<AtomicUsize>,
  count: Arc<AtomicUsize>,
}
impl LambdaRequest {
  /// Create a new `LambdaRequest` task with a specified deadline.
  ///
  /// # Parameters
  ///
  /// * `deadline_ms`: A timestamp in milliseconds since the Unix epoch representing when the
  /// Lambda function needs to finish execution.
  pub fn new(
    app_endpoint: Endpoint,
    compression: bool,
    pool_id: PoolId,
    lambda_id: LambdaId,
    channel_count: u8,
    router_endpoint: Endpoint,
    deadline_ms: u64,
  ) -> Self {
    LambdaRequest {
      count: Arc::new(AtomicUsize::new(0)),
      app_endpoint,
      compression,
      pool_id,
      lambda_id,
      channel_count,
      router_endpoint,
      cancel_token: tokio_util::sync::CancellationToken::new(),
      deadline_ms,
      goaway_received: Arc::new(AtomicBool::new(false)),
      last_active: Arc::new(AtomicU64::new(0)),
      rng: rand::rngs::StdRng::from_entropy(),
      requests_in_flight: Arc::new(AtomicUsize::new(0)),
    }
  }

  /// Executes a task with a specified deadline.
  pub async fn start(&mut self) -> Result<(), Error> {
    let start_time = current_time_millis();

    let sender = connect_to_router(
      self.router_endpoint.clone(),
      Arc::clone(&self.pool_id),
      Arc::clone(&self.lambda_id),
    )
    .await?;

    // Send the ping requests in background
    let ping_task = tokio::task::spawn(ping::send_ping_requests(
      Arc::clone(&self.last_active),
      Arc::clone(&self.goaway_received),
      sender.clone(),
      Arc::clone(&self.pool_id),
      Arc::clone(&self.lambda_id),
      Arc::clone(&self.count),
      self.router_endpoint.clone(),
      self.deadline_ms,
      self.cancel_token.clone(),
      Arc::clone(&self.requests_in_flight),
    ));

    // Startup the request channels
    let futures = (0..self.channel_count)
      .map(|channel_number| {
        let last_active = Arc::clone(&self.last_active);
        // Create a JoinHandle and implicitly return it to be collected in the vector
        let mut router_channel = RouterChannel::new(
          Arc::clone(&self.count),
          self.compression,
          Arc::clone(&self.goaway_received),
          Arc::clone(&last_active),
          Arc::clone(&self.requests_in_flight),
          self.router_endpoint.clone(),
          self.app_endpoint.clone(),
          channel_number,
          sender.clone(),
          Arc::clone(&self.pool_id),
          Arc::clone(&self.lambda_id),
          // TODO: Do not create an RNG for each request as it takes a little time
          // and will slow down single request processing
          uuid::Builder::from_random_bytes(self.rng.gen())
            .into_uuid()
            .to_string(),
        );
        tokio::spawn(async move { router_channel.start().await })
      })
      .collect::<Vec<_>>();

    tokio::select! {
        result = futures::future::try_join_all(futures) => {
            match result {
                Ok(_) => {
                  // All tasks completed successfully
                }
                Err(_) => {
                  panic!("LambdaId: {} - run - Error in futures::future::try_join_all", self.lambda_id);
                }
            }
        }
    }

    // Wait for the ping loop to exit
    self.cancel_token.cancel();
    ping_task.await?;

    // Print final stats
    let elapsed = current_time_millis() - start_time;
    let rps = format!(
      "{:.1}",
      self.count.load(Ordering::Acquire) as f64 / (elapsed as f64 / 1000.0)
    );
    log::info!(
      "LambdaId: {}, Requests: {}, Elapsed: {} ms, RPS: {} - Returning from run",
      self.lambda_id,
      self.count.load(Ordering::Acquire),
      elapsed,
      rps
    );

    Ok(())
  }
}

async fn connect_to_router(
  router_endpoint: Endpoint,
  pool_id: PoolId,
  lambda_id: LambdaId,
) -> Result<SendRequest<BoxBody<Bytes, Error>>, Error> {
  let tcp_stream = TcpStream::connect(router_endpoint.socket_addr_coercable()).await?;
  tcp_stream.set_nodelay(true)?;

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
      let tls_stream = connector.connect(domain, tcp_stream).await?;
      Box::new(tls_stream)
    }
    Scheme::Http => Box::new(tcp_stream),
  };

  let io = TokioIo::new(Pin::new(stream));

  // Setup the HTTP2 connection
  let (sender, conn) = http2::handshake(TokioExecutor::new(), io)
    .await
    .context("failed to setup HTTP2 connection to the router")?;

  tokio::task::spawn(async move {
    if let Err(err) = conn.await {
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
