use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::time::SystemTime;
use std::{pin::Pin, sync::Arc};

use futures::channel::mpsc;
use futures::SinkExt;
use http_body_util::{BodyExt, StreamBody};
use httpdate::fmt_http_date;
use hyper::body::Body;
use hyper::{
  body::{Bytes, Frame, Incoming},
  Request, Uri,
};
use hyper_util::rt::{TokioExecutor, TokioIo};
use rand::prelude::*;
use rand::SeedableRng;
use tokio::io::{AsyncRead, AsyncWrite};
use tokio::net::TcpStream;

use crate::app_request;
use crate::cert::AcceptAnyServerCert;
use crate::counter_drop::DecrementOnDrop;
use crate::ping;
use crate::time;

use tokio_rustls::client::TlsStream;

// Define a Stream trait that both TlsStream and TcpStream implement
pub trait Stream: AsyncRead + AsyncWrite + Send {}
impl Stream for TlsStream<TcpStream> {}
impl Stream for TcpStream {}

type Result<T> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;

pub async fn run(
  lambda_id: String,
  channel_count: i32,
  router_url: Uri,
  deadline_ms: u64,
) -> anyhow::Result<()> {
  let requests_in_flight = Arc::new(AtomicUsize::new(0));
  let cancel_token = tokio_util::sync::CancellationToken::new();
  let count = Arc::new(AtomicUsize::new(0));
  let mut rng = rand::rngs::StdRng::from_entropy();
  let goaway_received = Arc::new(AtomicBool::new(false));
  let last_active = Arc::new(AtomicU64::new(time::current_time_millis()));

  let scheme = router_url.scheme().unwrap().to_string();
  let use_https = scheme == "https";
  let host = router_url.host().expect("uri has no host").to_string();
  let port = router_url.port_u16().unwrap_or(80);
  let addr = format!("{}:{}", host, port);
  let authority = router_url.authority().unwrap().clone();

  // Setup the connection
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
  let domain_host = router_url.host().ok_or_else(|| "Host not found").unwrap();
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

  let lambda_id_clone = lambda_id.clone();
  tokio::task::spawn(async move {
    if let Err(err) = conn.await {
      log::error!(
        "LambdaId: {} - Router HTTP2 connection failed: {:?}",
        lambda_id_clone.clone(),
        err
      );
    }
  });

  // Send the ping requests in background
  let scheme_clone = scheme.clone();
  let host_clone = host.clone();
  let ping_task = tokio::task::spawn(ping::send_ping_requests(
    Arc::clone(&last_active),
    Arc::clone(&goaway_received),
    authority.to_string(),
    sender.clone(),
    lambda_id.clone(),
    Arc::clone(&count),
    scheme_clone,
    host_clone,
    port,
    deadline_ms,
    cancel_token.clone(),
    Arc::clone(&requests_in_flight),
  ));

  // Startup the request channels
  let futures = (0..channel_count)
      .map(|channel_number| {
          let last_active = Arc::clone(&last_active);
          let goaway_received = Arc::clone(&goaway_received);
          let authority = authority.clone();
          let mut sender = sender.clone();
          let count = Arc::clone(&count);

          let lambda_id = lambda_id.clone();
          let channel_id = format!(
              "{}",
              uuid::Builder::from_random_bytes(rng.gen())
                  .into_uuid()
                  .to_string()
          );
          let url: Uri = format!(
              "{}://{}:{}/api/chunked/request/{}/{}",
              scheme, host, port, lambda_id, channel_id
          )
          .parse()
          .unwrap();

          let requests_in_flight = Arc::clone(&requests_in_flight);

          tokio::spawn(async move {
              // TODO: Load app_url from config
              let app_url: Uri = "http://127.0.0.1:3001".parse().unwrap();
              let app_host = app_url.host().expect("uri has no host");
              let app_port = app_url.port_u16().unwrap_or(80);
              let app_addr = format!("{}:{}", app_host, app_port);

              // Setup the contained app connection
              // This is HTTP/1.1 so we need 1 connection for each worker
              let app_tcp_stream = TcpStream::connect(app_addr).await?;
              let app_io = TokioIo::new(app_tcp_stream);
              let (mut app_sender, app_conn) =
                  hyper::client::conn::http1::handshake(app_io).await?;
              let lambda_id_clone = lambda_id.clone();
              let channel_id_clone = channel_id.clone();
              tokio::task::spawn(async move {
                  if let Err(err) = app_conn.await {
                      log::error!("LambdaId: {}, ChannelId: {} - Contained App connection failed: {:?}", lambda_id_clone.clone(), channel_id_clone.clone(), err);
                  }
              });

              // This is where HTTP2 loops to make all the requests for a given client and worker
              loop {
                  let requests_in_flight = Arc::clone(&requests_in_flight);
                  let mut _decrement_on_drop = None;

                  // Create the router request
                  let (mut tx, recv) = mpsc::channel::<Result<Frame<Bytes>>>(32 * 1024);
                  let boxed_body = BodyExt::boxed(StreamBody::new(recv));
                  let req = Request::builder()
                      .uri(&url)
                      .method("POST")
                      .header(hyper::header::DATE, fmt_http_date(SystemTime::now()))
                      .header(hyper::header::HOST, authority.as_str())
                      // The content-type that we're sending to the router is opaque
                      // as it contains another HTTP request/response, so may start as text
                      // with request/headers and then be binary after that - it should not be parsed
                      // by anything other than us
                      .header(hyper::header::CONTENT_TYPE, "application/octet-stream")
                      .header("X-Lambda-Id", lambda_id.to_string())
                      .header("X-Channel-Id", channel_id.to_string())
                      .body(boxed_body)?;

                  //
                  // Make the request to the router
                  //

                  log::debug!("LambdaId: {}, ChannelId: {} - sending request", lambda_id.clone(), channel_id.clone());
                  while futures::future::poll_fn(|ctx| sender.poll_ready(ctx))
                  .await
                  .is_err()
                  {
                      // This gets hit when the router connection faults
                      panic!("LambdaId: {}, ChannelId: {} - Router connection ready check threw error - connection has disconnected, should reconnect", lambda_id.clone(), channel_id.clone());
                  }

                  let res = sender.send_request(req).await?;
                  log::debug!("LambdaId: {}, ChannelId: {} - got response: {:?}", lambda_id.clone(), channel_id.clone(), res);
                  let (parts, mut res_stream) = res.into_parts();
                  log::debug!("LambdaId: {}, ChannelId: {} - split response", lambda_id.clone(), channel_id.clone(),);

                  // If the router returned a 409 when we opened the channel
                  // then we close the channel
                  if parts.status == 409 {
                    if !goaway_received.load(std::sync::atomic::Ordering::Relaxed) {
                      log::info!("LambdaId: {}, ChannelId: {}, ChannelNum: {}, Reqs in Flight: {} - 409 received, exiting loop",
                        lambda_id.clone(), channel_id.clone(), channel_number, requests_in_flight.load(std::sync::atomic::Ordering::Relaxed));
                      goaway_received.store(true, std::sync::atomic::Ordering::Relaxed);
                    }
                    tx.close().await.unwrap_or(());
                    break;
                  }

                  // Read until we get all the request headers so we can construct our app request
                  let (app_req_builder, is_goaway, left_over_buf)
                      = app_request::read_until_req_headers(&mut res_stream, lambda_id.clone(), channel_id.clone()).await?;

                  if is_goaway {
                      if !goaway_received.load(std::sync::atomic::Ordering::Relaxed) {
                        log::info!("LambdaId: {}, ChannelId: {}, ChannelNum: {}, Reqs in Flight: {} - GoAway received, exiting loop",
                          lambda_id.clone(), channel_id.clone(), channel_number, requests_in_flight.load(std::sync::atomic::Ordering::Relaxed));
                        goaway_received.store(true, std::sync::atomic::Ordering::Relaxed);
                      }
                      tx.close().await.unwrap_or(());
                      break;
                  }

                  // We got a request to run
                  requests_in_flight.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                  _decrement_on_drop = Some(DecrementOnDrop(&requests_in_flight));
                  last_active.store(time::current_time_millis(), Ordering::Relaxed);

                  //
                  // Make the request to the contained app
                  //
                  let (mut app_req_tx, app_req_recv) =
                      mpsc::channel::<Result<Frame<Bytes>>>(32 * 1024);
                  let app_req = app_req_builder.body(StreamBody::new(app_req_recv))?;

                  while futures::future::poll_fn(|ctx| app_sender.poll_ready(ctx))
                  .await
                  .is_err()
                  {
                      // This gets hit when the app connection faults
                      panic!("LambdaId: {}, ChannelId: {}, Reqs in Flight: {} - App connection ready check threw error - connection has disconnected, should reconnect",
                        lambda_id.clone(), channel_id.clone(), requests_in_flight.load(std::sync::atomic::Ordering::Relaxed));
                  }

                  // Relay the request body to the contained app
                  // We start a task for this because we need to relay the bytes
                  // between the incoming request and the outgoing request
                  // and we need to set this up before we actually send the request
                  // to the contained app
                  let channel_id_clone = channel_id.clone();
                  let lambda_id_clone = lambda_id.clone();
                  let requests_in_flight_clone = Arc::clone(&requests_in_flight);
                  let relay_task = tokio::task::spawn(async move {
                      let mut bytes_sent = 0;

                      // Send any overflow body bytes to the contained app
                      if left_over_buf.len() > 0 {
                          bytes_sent += left_over_buf.len();
                          log::debug!("LambdaId: {}, ChannelId: {} - Sending left over bytes to contained app: {:?}", lambda_id_clone.clone(), channel_id_clone.clone(), left_over_buf.len());
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
                      while let Some(chunk) = futures::future::poll_fn(|cx| {
                          Incoming::poll_frame(Pin::new(&mut res_stream), cx)
                      })
                      .await
                      {
                          if chunk.is_err() {
                              log::error!("LambadId: {}, ChannelId: {}, Reqs in Flight: {}, BytesSent: {} - Error reading from res_stream: {:?}",
                                lambda_id_clone.clone(),
                                channel_id_clone.clone(),
                                requests_in_flight_clone.load(std::sync::atomic::Ordering::Relaxed),
                                bytes_sent,
                                chunk.err());
                              router_error_reading = true;
                              break;
                          }

                          let chunk_len = chunk.as_ref().unwrap().data_ref().unwrap().len();
                          // If chunk_len is zero the channel has closed
                          if chunk_len == 0 {
                            log::debug!("LambdaId: {}, ChannelId: {}, BytesSent: {}, ChunkLen: {} - Channel closed", lambda_id_clone.clone(), channel_id_clone.clone(), bytes_sent, chunk_len);
                            break;
                          }
                          match app_req_tx.send(Ok(chunk.unwrap())).await {
                              Ok(_) => {}
                              Err(err) => {
                                  log::error!("LambdaId: {}, ChannelId: {}, Reqs in Flight: {}, BytesSent: {}, ChunkLen: {} - Error sending to app_req_tx: {:?}",
                                  lambda_id_clone.clone(),
                                  channel_id_clone.clone(),
                                  requests_in_flight_clone.load(std::sync::atomic::Ordering::Relaxed),
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
                  let (app_parts, mut app_res_stream) = app_res.into_parts();

                  //
                  // Relay the response
                  //

                  let mut header_buffer = Vec::with_capacity(32 * 1024);

                  // Write the status line
                  let status_code = app_parts.status.as_u16();
                  let reason = app_parts.status.canonical_reason();
                  let status_line = match reason {
                      Some(r) => format!("HTTP/1.1 {} {}\r\n", status_code, r),
                      None => format!("HTTP/1.1 {}\r\n", status_code),
                  };
                  let status_line_bytes = status_line.as_bytes();
                  header_buffer.extend(status_line_bytes);

                  // Add two static headers for X-Lambda-Id and X-Channel-Id
                  let lambda_id_header = format!("X-Lambda-Id: {}\r\n", lambda_id.clone());
                  let lambda_id_header_bytes = lambda_id_header.as_bytes();
                  header_buffer.extend(lambda_id_header_bytes);
                  let channel_id_header = format!("X-Channel-Id: {}\r\n", channel_id);
                  let channel_id_header_bytes = channel_id_header.as_bytes();
                  header_buffer.extend(channel_id_header_bytes);

                  // Send the headers to the caller
                  for header in app_parts.headers.iter() {
                      let header_name = header.0.as_str();
                      let header_value = header.1.to_str().unwrap();

                      // Skip the connection and keep-alive headers
                      if header_name == "connection" {
                        continue;
                      }
                      if header_name == "keep-alive" {
                        continue;
                      }

                      let header_line =
                          format!("{}: {}\r\n", header_name, header_value);
                      let header_bytes = header_line.as_bytes();

                      // Check if the buffer has enough room for the header bytes
                      if header_buffer.len() + header_bytes.len() <= 32 * 1024
                      {
                          header_buffer.extend(header_bytes);
                      } else {
                          // If the header_buffer is full, send it and create a new header_buffer
                          tx.send(Ok(Frame::data(header_buffer.into())))
                              .await?;

                          header_buffer = Vec::new();
                          header_buffer.extend(header_bytes);
                      }
                  }

                  // End the headers
                  header_buffer.extend(b"\r\n");
                  tx.send(Ok(Frame::data(header_buffer.into()))).await?;

                  // Rip the bytes back to the caller
                  let channel_id_clone = channel_id.clone();
                  let mut app_error_reading = false;
                  let mut bytes_read = 0;
                  while let Some(chunk) = futures::future::poll_fn(|cx| {
                      Incoming::poll_frame(Pin::new(&mut app_res_stream), cx)
                  })
                  .await
                  {
                      if chunk.is_err() {
                          log::info!("LambdaId: {}, ChannelId: {}, Reqs in Flight: {}, BytesRead: {} - Error reading from app_res_stream: {:?}",
                            lambda_id.clone(),
                            channel_id_clone.clone(),
                            requests_in_flight.load(std::sync::atomic::Ordering::Relaxed),
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

                      tx.send(Ok(chunk.unwrap())).await?;
                  }
                  if app_error_reading {
                    log::debug!("LambdaId: {}, ChannelId: {} - Error reading from app_res_stream, dropping tx", lambda_id.clone(), channel_id_clone.clone());
                  }

                  let close_router_tx_task = tokio::task::spawn(async move {
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
                                panic!("LambdaId: {}, ChannelId: {} - Error in futures::future::try_join_all", lambda_id.clone(), channel_id_clone.clone());
                            }
                        }
                    }
                }

                count.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
              }

              Ok::<(), anyhow::Error>(())
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
                panic!("LambdaId: {} - run - Error in futures::future::try_join_all", lambda_id.clone());
              }
          }
      }
  }

  // Wait for the ping loop to exit
  cancel_token.cancel();
  ping_task.await?;

  Ok(())
}
