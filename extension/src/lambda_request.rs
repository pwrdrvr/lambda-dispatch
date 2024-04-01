use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::{pin::Pin, sync::Arc};

use hyper::Uri;
use hyper_util::rt::{TokioExecutor, TokioIo};
use rand::SeedableRng;
use tokio::io::{AsyncRead, AsyncWrite};
use tokio::net::TcpStream;

use crate::cert::AcceptAnyServerCert;
use crate::ping;
use crate::router_channel::RouterChannel;
use crate::time::current_time_millis;

use tokio_rustls::client::TlsStream;

// Define a Stream trait that both TlsStream and TcpStream implement
pub trait Stream: AsyncRead + AsyncWrite + Send {}
impl Stream for TlsStream<TcpStream> {}
impl Stream for TcpStream {}

#[derive(Clone)]
pub struct LambdaRequest {
  domain: Uri,
  compression: bool,
  lambda_id: String,
  channel_count: u8,
  dispatcher_url: Uri,
  cancel_token: tokio_util::sync::CancellationToken,
  deadline_ms: u64,
  goaway_received: Arc<AtomicBool>,
  last_active: Arc<AtomicU64>,
  rng: rand::rngs::StdRng,
  requests_in_flight: Arc<AtomicUsize>,
  count: Arc<AtomicUsize>,
}

//
// LambdaRequest handles connecting back to the router, picking up requests,
// sending ping requests to the router, and sending the requests to the contained app
// When an invoke completes this is torn down completely
//

impl LambdaRequest {
  pub fn new(
    domain: Uri,
    compression: bool,
    lambda_id: String,
    channel_count: u8,
    dispatcher_url: Uri,
    deadline_ms: u64,
  ) -> Self {
    LambdaRequest {
      count: Arc::new(AtomicUsize::new(0)),
      domain,
      compression,
      lambda_id,
      channel_count,
      dispatcher_url,
      cancel_token: tokio_util::sync::CancellationToken::new(),
      deadline_ms,
      goaway_received: Arc::new(AtomicBool::new(false)),
      last_active: Arc::new(AtomicU64::new(0)),
      rng: rand::rngs::StdRng::from_entropy(),
      requests_in_flight: Arc::new(AtomicUsize::new(0)),
    }
  }

  /// Executes a task with a specified deadline.
  ///
  /// # Parameters
  ///
  /// * `deadline_ms`: A timestamp in milliseconds since the Unix epoch
  ///   representing when the Lambda function needs to finish execution.
  pub async fn start(&mut self) -> anyhow::Result<()> {
    let start_time = current_time_millis();
    let scheme = self.dispatcher_url.scheme().unwrap().to_string();
    let use_https = scheme == "https";
    let host = self
      .dispatcher_url
      .host()
      .expect("uri has no host")
      .to_string();
    let port = self.dispatcher_url.port_u16().unwrap_or(80);
    let addr = format!("{}:{}", host, port);
    let dispatcher_authority = self.dispatcher_url.authority().unwrap().clone();

    // Setup the connection to the router
    let tcp_stream = TcpStream::connect(addr).await?;
    tcp_stream.set_nodelay(true)?;

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
    let connector = tokio_rustls::TlsConnector::from(Arc::new(config));
    let domain_host = self
      .dispatcher_url
      .host()
      .ok_or_else(|| "Host not found")
      .unwrap();
    let domain = rustls_pki_types::ServerName::try_from(domain_host)?;

    let stream: Box<dyn Stream + Unpin>;
    if use_https {
      let tls_stream = connector.connect(domain.to_owned(), tcp_stream).await?;
      stream = Box::new(tls_stream);
    } else {
      stream = Box::new(tcp_stream);
    }
    let io = TokioIo::new(Pin::new(stream));

    // Setup the HTTP2 connection
    let (sender, conn) = hyper::client::conn::http2::handshake(TokioExecutor::new(), io)
      .await
      .unwrap();

    let lambda_id_clone = self.lambda_id.clone();
    tokio::task::spawn(async move {
      if let Err(err) = conn.await {
        log::error!(
          "LambdaId: {} - Router HTTP2 connection failed: {:?}",
          lambda_id_clone.clone(),
          err
        );
      }
    });

    let app_url: Uri = self.domain.clone();

    // Send the ping requests in background
    let scheme_clone = scheme.clone();
    let host_clone = host.clone();
    let ping_task = tokio::task::spawn(ping::send_ping_requests(
      Arc::clone(&self.last_active),
      Arc::clone(&self.goaway_received),
      dispatcher_authority.to_string(),
      sender.clone(),
      self.lambda_id.clone(),
      Arc::clone(&self.count),
      scheme_clone,
      host_clone,
      port,
      self.deadline_ms,
      self.cancel_token.clone(),
      Arc::clone(&self.requests_in_flight),
    ));

    // Startup the request channels
    let futures = (0..self.channel_count)
      .map(|channel_number| {
        let app_url = app_url.clone();
        let compression_enabled = self.compression.clone();
        let last_active = Arc::clone(&self.last_active);
        let goaway_received = Arc::clone(&self.goaway_received);
        let dispatcher_authority = dispatcher_authority.clone();
        let sender = sender.clone();
        let count = Arc::clone(&self.count);
        let rng = self.rng.clone();
        let scheme = scheme.clone();
        let host = host.clone();
        let port = port;
        let lambda_id = self.lambda_id.clone();
        let requests_in_flight = Arc::clone(&self.requests_in_flight);

        // Create a JoinHandle and implicitly return it to be collected in the vector
        tokio::spawn(async move {
          let mut router_channel = RouterChannel::new(
            Arc::clone(&count),
            compression_enabled,
            lambda_id.clone(),
            Arc::clone(&goaway_received),
            Arc::clone(&last_active),
            rng.clone(),
            Arc::clone(&requests_in_flight),
            scheme.clone(),
            host.clone(),
            port.clone(),
            app_url.clone(),
            channel_number.clone(),
            sender.clone(),
            dispatcher_authority.clone(),
          );

          return router_channel.start().await;
        })
      })
      .collect::<Vec<_>>();

    tokio::select! {
        result = futures::future::try_join_all(futures) => {
            match result {
                Ok(_) => {
                  // All tasks completed successfully
                }
                Err(_) => {
                  panic!("LambdaId: {} - run - Error in futures::future::try_join_all", self.lambda_id.clone());
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
