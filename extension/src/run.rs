use std::io::Write;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::time::SystemTime;
use std::{pin::Pin, sync::Arc};

use futures::channel::mpsc;
use futures::{Future, SinkExt};
use http_body_util::{BodyExt, StreamBody};
use httpdate::fmt_http_date;
use hyper::body::Body;
use hyper::{
  body::{Bytes, Frame, Incoming},
  Request, Uri,
};
use hyper_util::rt::{TokioExecutor, TokioIo};
use lambda_runtime::LambdaEvent;
use rand::prelude::*;
use rand::SeedableRng;
use tokio::io::{AsyncRead, AsyncWrite};
use tokio::net::TcpStream;
use tower::Service;

use crate::cert::AcceptAnyServerCert;
use crate::counter_drop::DecrementOnDrop;
use crate::options::Options;
use crate::ping;
use crate::time::{self, current_time_millis};
use crate::{app_request, messages};

use tokio_rustls::client::TlsStream;

use flate2::write::GzEncoder;

// Define a Stream trait that both TlsStream and TcpStream implement
pub trait Stream: AsyncRead + AsyncWrite + Send {}
impl Stream for TlsStream<TcpStream> {}
impl Stream for TcpStream {}

type Result<T> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;

#[derive(Clone)]
pub struct LambdaService {
  healthcheck_url: Uri,
  async_init: bool,
  ready_at_init: Arc<AtomicBool>,
  domain: Uri,
  compression: bool,
}

impl LambdaService {
  pub fn new(options: &Options) -> Self {
    let schema = "http";

    let healthcheck_url = format!("{}://{}:{}{}", schema, "127.0.0.1", options.port, "/health")
      .parse()
      .unwrap();

    let domain = format!("{}://{}:{}", schema, "127.0.0.1", options.port)
      .parse()
      .unwrap();

    LambdaService {
      async_init: options.async_init,
      ready_at_init: Arc::new(AtomicBool::new(false)),
      compression: options.compression,
      domain,
      healthcheck_url,
    }
  }

  //
  // This is called by the Tower.Service trait impl below
  //
  async fn fetch_response(
    &self,
    event: LambdaEvent<messages::WaiterRequest>,
  ) -> std::result::Result<messages::WaiterResponse, lambda_runtime::Error> {
    // extract some useful info from the request
    let lambda_id = event.payload.id;
    let channel_count: u8 = event.payload.number_of_channels;
    let dispatcher_url = event.payload.dispatcher_url;

    // prepare the response
    let resp = messages::WaiterResponse {
      id: lambda_id.to_string(),
    };

    if event.payload.init_only {
      log::info!(
        "LambdaId: {} - Returning from init-only request",
        lambda_id.clone()
      );
      return Ok(resp);
    }

    // If the sent_time is more than a second old, just return
    // This is mostly needed locally where requests get stuck in the queue
    let sent_time = chrono::DateTime::parse_from_rfc3339(&event.payload.sent_time).unwrap();
    if sent_time.timestamp_millis() < (current_time_millis() - 5000).try_into().unwrap() {
      log::info!(
        "LambdaId: {} - Returning from stale request",
        lambda_id.clone()
      );
      return Ok(resp);
    }

    log::info!(
      "LambdaId: {}, Timeout: {}s - Invoked",
      lambda_id,
      (event.context.deadline - current_time_millis()) / 1000
    );
    let mut deadline_ms = event.context.deadline;
    if (deadline_ms - current_time_millis()) > 15 * 60 * 1000 {
      log::warn!("Deadline is greater than 15 minutes, trimming to 1 minute");
      deadline_ms = current_time_millis() + 60 * 1000;
    }
    // check if env var is set to force deadline for testing
    if let Ok(force_deadline_secs) = std::env::var("LAMBDA_DISPATCH_FORCE_DEADLINE") {
      log::warn!("Forcing deadline to {} seconds", force_deadline_secs);
      let force_deadline_secs: u64 = force_deadline_secs.parse().unwrap();
      deadline_ms = current_time_millis() + force_deadline_secs * 1000;
    }

    // run until we get a GoAway or deadline is about to be reached
    let mut lambda_request = LambdaRequest::new(
      self.domain.clone(),
      self.compression,
      lambda_id.clone(),
      channel_count,
      dispatcher_url.parse().unwrap(),
      deadline_ms,
    );
    lambda_request.start().await?;

    Ok(resp)
  }
}

// Tower.Service is the interface required by lambda_runtime::run
impl Service<LambdaEvent<messages::WaiterRequest>> for LambdaService {
  type Response = messages::WaiterResponse;
  type Error = lambda_runtime::Error;
  type Future =
    Pin<Box<dyn Future<Output = std::result::Result<Self::Response, Self::Error>> + Send>>;

  fn poll_ready(
    &mut self,
    _cx: &mut core::task::Context<'_>,
  ) -> core::task::Poll<std::result::Result<(), Self::Error>> {
    core::task::Poll::Ready(Ok(()))
  }

  fn call(&mut self, event: LambdaEvent<messages::WaiterRequest>) -> Self::Future {
    let adapter = self.clone();
    Box::pin(async move { adapter.fetch_response(event).await })
  }
}

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
    let start_time = time::current_time_millis();
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
          //
          // TODO: Create a channel object and call the start function on it
          //

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
    let elapsed = time::current_time_millis() - start_time;
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

#[derive(Clone)]
pub struct RouterChannel {
  count: Arc<AtomicUsize>,
  compression: bool,
  lambda_id: String,
  goaway_received: Arc<AtomicBool>,
  last_active: Arc<AtomicU64>,
  requests_in_flight: Arc<AtomicUsize>,
  channel_url: Uri,
  channel_id: String,
  app_url: Uri,
  channel_number: u8,
  sender: hyper::client::conn::http2::SendRequest<
    http_body_util::combinators::BoxBody<Bytes, Box<dyn std::error::Error + Send + Sync>>,
  >,
  dispatcher_authority: hyper::http::uri::Authority,
}

impl RouterChannel {
  pub fn new(
    count: Arc<AtomicUsize>,
    compression: bool,
    lambda_id: String,
    goaway_received: Arc<AtomicBool>,
    last_active: Arc<AtomicU64>,
    mut rng: rand::rngs::StdRng,
    requests_in_flight: Arc<AtomicUsize>,
    router_schema: String,
    router_host: String,
    router_port: u16,
    app_url: Uri,
    channel_number: u8,
    sender: hyper::client::conn::http2::SendRequest<
      http_body_util::combinators::BoxBody<Bytes, Box<dyn std::error::Error + Send + Sync>>,
    >,
    dispatcher_authority: hyper::http::uri::Authority,
  ) -> Self {
    let channel_id = format!(
      "{}",
      uuid::Builder::from_random_bytes(rng.gen())
        .into_uuid()
        .to_string()
    );

    let channel_url = format!(
      "{}://{}:{}/api/chunked/request/{}/{}",
      router_schema,
      router_host,
      router_port,
      lambda_id.clone(),
      channel_id.clone()
    )
    .parse()
    .unwrap();

    RouterChannel {
      count,
      compression,
      lambda_id: lambda_id.clone(),
      goaway_received,
      last_active,
      requests_in_flight,
      channel_id: channel_id.clone(),
      channel_url,
      app_url,
      channel_number,
      sender,
      dispatcher_authority,
    }
  }

  pub async fn start(&mut self) -> anyhow::Result<()> {
    let app_host = self.app_url.host().expect("uri has no host");
    let app_port = self.app_url.port_u16().unwrap_or(80);
    let app_addr = format!("{}:{}", app_host, app_port);

    // Setup the contained app connection
    // This is HTTP/1.1 so we need 1 connection for each worker
    let app_tcp_stream = TcpStream::connect(app_addr).await?;
    let app_io = TokioIo::new(app_tcp_stream);
    let (mut app_sender, app_conn) = hyper::client::conn::http1::handshake(app_io).await?;
    let lambda_id_clone = self.lambda_id.clone();
    let channel_id_clone = self.channel_id.clone();

    tokio::task::spawn(async move {
      if let Err(err) = app_conn.await {
        log::error!(
          "LambdaId: {}, ChannelId: {} - Contained App connection failed: {:?}",
          lambda_id_clone.clone(),
          channel_id_clone.clone(),
          err
        );
      }
    });

    // This is where HTTP2 loops to make all the requests for a given client and worker
    loop {
      // let requests_in_flight = Arc::clone(&requests_in_flight);
      let mut _decrement_on_drop = None;

      // Create the router request
      let (mut tx, recv) = mpsc::channel::<Result<Frame<Bytes>>>(32 * 1024);
      let boxed_body = BodyExt::boxed(StreamBody::new(recv));
      let req = Request::builder()
        .uri(&self.channel_url)
        .method("POST")
        .header(hyper::header::DATE, fmt_http_date(SystemTime::now()))
        .header(hyper::header::HOST, self.dispatcher_authority.as_str())
        // The content-type that we're sending to the router is opaque
        // as it contains another HTTP request/response, so may start as text
        // with request/headers and then be binary after that - it should not be parsed
        // by anything other than us
        .header(hyper::header::CONTENT_TYPE, "application/octet-stream")
        .header("X-Lambda-Id", self.lambda_id.to_string())
        .header("X-Channel-Id", self.channel_id.to_string())
        .body(boxed_body)?;

      //
      // Make the request to the router
      //

      log::debug!(
        "LambdaId: {}, ChannelId: {} - sending request",
        self.lambda_id.clone(),
        self.channel_id.clone()
      );
      while futures::future::poll_fn(|ctx| self.sender.poll_ready(ctx))
        .await
        .is_err()
      {
        // This gets hit when the router connection faults
        panic!("LambdaId: {}, ChannelId: {} - Router connection ready check threw error - connection has disconnected, should reconnect", self.lambda_id.clone(), self.channel_id.clone());
      }

      let res = self.sender.send_request(req).await?;
      log::debug!(
        "LambdaId: {}, ChannelId: {} - got response: {:?}",
        self.lambda_id.clone(),
        self.channel_id.clone(),
        res
      );
      let (parts, mut res_stream) = res.into_parts();
      log::debug!(
        "LambdaId: {}, ChannelId: {} - split response",
        self.lambda_id.clone(),
        self.channel_id.clone(),
      );

      // If the router returned a 409 when we opened the channel
      // then we close the channel
      if parts.status == 409 {
        if !self
          .goaway_received
          .load(std::sync::atomic::Ordering::Acquire)
        {
          log::info!("LambdaId: {}, ChannelId: {}, ChannelNum: {}, Reqs in Flight: {} - 409 received, exiting loop",
                  self.lambda_id.clone(), self.channel_id.clone(), self.channel_number, self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire));
          self
            .goaway_received
            .store(true, std::sync::atomic::Ordering::Release);
        }
        tx.close().await.unwrap_or(());
        break;
      }

      // On first request this will release the ping task to start
      // We have to hold the pinger up else it will exit before connections to the router are established
      // We also count re-establishing a channel as an action since it can allow a request to flow in
      if self.last_active.load(Ordering::Acquire) == 0 {
        log::info!("LambdaId: {}, ChannelId: {}, ChannelNum: {}, Reqs in Flight: {} - First request, releasing pinger",
                self.lambda_id.clone(), self.channel_id.clone(), self.channel_number, self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire));
      }
      self
        .last_active
        .store(time::current_time_millis(), Ordering::Release);

      // Read until we get all the request headers so we can construct our app request
      let (app_req_builder, is_goaway, left_over_buf) = app_request::read_until_req_headers(
        &mut res_stream,
        self.lambda_id.clone(),
        self.channel_id.clone(),
      )
      .await?;

      // Check if the request has an Accept-Encoding header with gzip
      // If it does, we *can* gzip the response
      let accepts_gzip = app_req_builder
        .headers_ref()
        .unwrap()
        .get("accept-encoding")
        .map(|v| v.to_str().unwrap_or_default().contains("gzip"))
        .unwrap_or_default();

      if is_goaway {
        if !self
          .goaway_received
          .load(std::sync::atomic::Ordering::Acquire)
        {
          log::info!("LambdaId: {}, ChannelId: {}, ChannelNum: {}, Reqs in Flight: {} - GoAway received, exiting loop",
                    self.lambda_id.clone(), self.channel_id.clone(), self.channel_number, self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire));
          self
            .goaway_received
            .store(true, std::sync::atomic::Ordering::Release);
        }
        tx.close().await.unwrap_or(());
        break;
      }

      // We got a request to run
      self
        .requests_in_flight
        .fetch_add(1, std::sync::atomic::Ordering::AcqRel);
      _decrement_on_drop = Some(DecrementOnDrop(&self.requests_in_flight));
      self
        .last_active
        .store(time::current_time_millis(), Ordering::Release);

      //
      // Make the request to the contained app
      //
      let (mut app_req_tx, app_req_recv) = mpsc::channel::<Result<Frame<Bytes>>>(32 * 1024);
      let app_req = app_req_builder.body(StreamBody::new(app_req_recv))?;

      while futures::future::poll_fn(|ctx| app_sender.poll_ready(ctx))
        .await
        .is_err()
      {
        // This gets hit when the app connection faults
        panic!("LambdaId: {}, ChannelId: {}, Reqs in Flight: {} - App connection ready check threw error - connection has disconnected, should reconnect",
                  self.lambda_id.clone(), self.channel_id.clone(), self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire));
      }

      // Relay the request body to the contained app
      // We start a task for this because we need to relay the bytes
      // between the incoming request and the outgoing request
      // and we need to set this up before we actually send the request
      // to the contained app
      let channel_id_clone = self.channel_id.clone();
      let lambda_id_clone = self.lambda_id.clone();
      let requests_in_flight_clone = Arc::clone(&self.requests_in_flight);
      // let requests_in_flight_clone = Arc::clone(&self.requests_in_flight);
      let relay_task = tokio::task::spawn(async move {
        let mut bytes_sent = 0;

        // Send any overflow body bytes to the contained app
        if left_over_buf.len() > 0 {
          bytes_sent += left_over_buf.len();
          log::debug!(
            "LambdaId: {}, ChannelId: {} - Sending left over bytes to contained app: {:?}",
            lambda_id_clone.clone(),
            channel_id_clone.clone(),
            left_over_buf.len()
          );
          app_req_tx
            .send(Ok(Frame::data(left_over_buf.into())))
            .await
            .unwrap();
        }

        //
        // Handle incoming POST request by relaying the body
        //
        // Source: res_stream
        // Sink: app_req_tx
        let mut router_error_reading = false;
        while let Some(chunk) =
          futures::future::poll_fn(|cx| Incoming::poll_frame(Pin::new(&mut res_stream), cx)).await
        {
          if chunk.is_err() {
            log::error!("LambadId: {}, ChannelId: {}, Reqs in Flight: {}, BytesSent: {} - Error reading from res_stream: {:?}",
                          lambda_id_clone.clone(),
                          channel_id_clone.clone(),
                          requests_in_flight_clone.load(std::sync::atomic::Ordering::Acquire),
                          bytes_sent,
                          chunk.err());
            router_error_reading = true;
            break;
          }

          let chunk_len = chunk.as_ref().unwrap().data_ref().unwrap().len();
          // If chunk_len is zero the channel has closed
          if chunk_len == 0 {
            log::debug!(
              "LambdaId: {}, ChannelId: {}, BytesSent: {}, ChunkLen: {} - Channel closed",
              lambda_id_clone.clone(),
              channel_id_clone.clone(),
              bytes_sent,
              chunk_len
            );
            break;
          }
          match app_req_tx.send(Ok(chunk.unwrap())).await {
            Ok(_) => {}
            Err(err) => {
              log::error!("LambdaId: {}, ChannelId: {}, Reqs in Flight: {}, BytesSent: {}, ChunkLen: {} - Error sending to app_req_tx: {:?}",
                            lambda_id_clone.clone(),
                            channel_id_clone.clone(),
                            requests_in_flight_clone.load(std::sync::atomic::Ordering::Acquire),
                            bytes_sent,
                            chunk_len,
                            err);
              break;
            }
          }
          bytes_sent += chunk_len;
        }

        // Close the post body stream
        if router_error_reading {
          log::info!("LambdaId: {}, ChannelId: {}, BytesSent: {} - Error reading from res_stream, dropping app_req_tx", lambda_id_clone.clone(), channel_id_clone.clone(), bytes_sent);
          return;
        }

        // This may error if the router closed the connection
        let _ = app_req_tx.flush().await;
        let _ = app_req_tx.close().await;
      });

      //
      // Send the request
      //
      let app_res = app_sender.send_request(app_req).await?;
      let (app_res_parts, mut app_res_stream) = app_res.into_parts();

      //
      // Relay the response
      //

      let mut header_buffer = Vec::with_capacity(32 * 1024);

      // Write the status line
      let status_code = app_res_parts.status.as_u16();
      let reason = app_res_parts.status.canonical_reason();
      let status_line = match reason {
        Some(r) => format!("HTTP/1.1 {} {}\r\n", status_code, r),
        None => format!("HTTP/1.1 {}\r\n", status_code),
      };
      let status_line_bytes = status_line.as_bytes();
      header_buffer.extend(status_line_bytes);

      // Add two static headers for X-Lambda-Id and X-Channel-Id
      let lambda_id_header = format!("X-Lambda-Id: {}\r\n", self.lambda_id.clone());
      let lambda_id_header_bytes = lambda_id_header.as_bytes();
      header_buffer.extend(lambda_id_header_bytes);
      let channel_id_header = format!("X-Channel-Id: {}\r\n", self.channel_id);
      let channel_id_header_bytes = channel_id_header.as_bytes();
      header_buffer.extend(channel_id_header_bytes);

      // Check if we have a Content-Encoding response header
      // If we do, we should not gzip the response
      let app_res_compressed = !app_res_parts.headers.get("content-encoding").is_none();

      // Check if we have a Content-Length response header
      // If we do, and if it's small (e.g. < 1 KB), we should not gzip the response
      let app_res_content_length = app_res_parts
        .headers
        .get(hyper::header::CONTENT_LENGTH)
        .and_then(|val| val.to_str().ok())
        .and_then(|s| s.parse::<i32>().ok())
        .unwrap_or(-1);

      let app_res_content_type =
        if let Some(content_type) = app_res_parts.headers.get("content-type") {
          content_type.to_str().unwrap()
        } else {
          ""
        };

      let compressable_content_type = app_res_content_type.starts_with("text/")
        || app_res_content_type.starts_with("application/json")
        || app_res_content_type.starts_with("application/javascript")
        || app_res_content_type.starts_with("image/svg+xml")
        || app_res_content_type.starts_with("application/xhtml+xml")
        || app_res_content_type.starts_with("application/x-javascript")
        || app_res_content_type.starts_with("application/xml");

      let app_res_will_compress = self.compression
        && accepts_gzip
        && !app_res_compressed
        // If it's a chunked response we'll compress it
        // But if it's non-chunked we'll only compress it if it's not small
        && (app_res_content_length == -1 || app_res_content_length > 1024)
        && compressable_content_type;

      // If we're going to gzip the response, add the content-encoding header
      if app_res_will_compress {
        let content_encoding = format!("content-encoding: {}\r\n", "gzip");
        let content_encoding_bytes = content_encoding.as_bytes();
        header_buffer.extend(content_encoding_bytes);
      }

      // Send the headers to the caller
      for header in app_res_parts.headers.iter() {
        let header_name = header.0.as_str();
        let header_value = header.1.to_str().unwrap();

        // Skip the connection and keep-alive headers
        if header_name == "connection" {
          continue;
        }
        if header_name == "keep-alive" {
          continue;
        }

        // Need to skip content-length if we're going to gzip the response
        if header_name == "content-length" && app_res_will_compress {
          continue;
        }

        let header_line = format!("{}: {}\r\n", header_name, header_value);
        let header_bytes = header_line.as_bytes();

        // Check if the buffer has enough room for the header bytes
        if header_buffer.len() + header_bytes.len() <= 32 * 1024 {
          header_buffer.extend(header_bytes);
        } else {
          // If the header_buffer is full, send it and create a new header_buffer
          tx.send(Ok(Frame::data(header_buffer.into()))).await?;

          header_buffer = Vec::new();
          header_buffer.extend(header_bytes);
        }
      }

      // End the headers
      header_buffer.extend(b"\r\n");
      tx.send(Ok(Frame::data(header_buffer.into()))).await?;

      let mut encoder: Option<GzEncoder<Vec<u8>>> = None;
      if app_res_will_compress {
        encoder = Some(GzEncoder::new(Vec::new(), flate2::Compression::default()));
      }

      // Rip the bytes back to the caller
      let channel_id_clone = self.channel_id.clone();
      let mut app_error_reading = false;
      let mut bytes_read = 0;
      while let Some(chunk) =
        futures::future::poll_fn(|cx| Incoming::poll_frame(Pin::new(&mut app_res_stream), cx)).await
      {
        if chunk.is_err() {
          log::info!("LambdaId: {}, ChannelId: {}, Reqs in Flight: {}, BytesRead: {} - Error reading from app_res_stream: {:?}",
            self.lambda_id.clone(),
            channel_id_clone.clone(),
            self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire),
            bytes_read,
            chunk.err());
          app_error_reading = true;
          break;
        }

        let chunk_len = chunk.as_ref().unwrap().data_ref().unwrap().len();
        // If chunk_len is zero the channel has closed
        if chunk_len == 0 {
          break;
        }

        bytes_read += chunk_len;

        // TODO: This is where we can gzip
        if let Some(ref mut encoder) = encoder {
          let chunk_data = chunk.as_ref().unwrap().data_ref().unwrap();
          encoder.write_all(chunk_data)?;
          encoder.flush()?;
          let compressed_chunk = encoder.get_mut();
          tx.send(Ok(Frame::data(compressed_chunk.clone().into())))
            .await?;
          compressed_chunk.clear();
        } else {
          tx.send(Ok(chunk.unwrap())).await?;
        }
      }
      if app_error_reading {
        log::debug!(
          "LambdaId: {}, ChannelId: {} - Error reading from app_res_stream, dropping tx",
          self.lambda_id.clone(),
          channel_id_clone.clone()
        );
      }

      let close_router_tx_task = tokio::task::spawn(async move {
        if let Some(encoder) = encoder.take() {
          let compressed_chunk = encoder.finish().unwrap();
          tx.send(Ok(Frame::data(compressed_chunk.into())))
            .await
            .unwrap();
        }
        let _ = tx.flush().await;
        let _ = tx.close().await;
      });

      let mut futures = Vec::new();
      futures.push(relay_task);
      futures.push(close_router_tx_task);

      // Wait for both to finish
      tokio::select! {
          result = futures::future::try_join_all(futures) => {
              match result {
                  Ok(_) => {
                      // All tasks completed successfully
                  }
                  Err(_) => {
                      panic!("LambdaId: {}, ChannelId: {} - Error in futures::future::try_join_all", self.lambda_id.clone(), channel_id_clone.clone());
                  }
              }
          }
      }

      self.count.fetch_add(1, std::sync::atomic::Ordering::AcqRel);
    }

    Ok::<(), anyhow::Error>(())
  }
}
