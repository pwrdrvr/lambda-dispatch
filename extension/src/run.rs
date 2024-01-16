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
use crate::ping;
// use crate::support::TokioIo;
use crate::time;

// use crate::support::TokioExecutor;

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
  deadline: u64,
) -> anyhow::Result<()> {
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

  // let tls_stream = connector.connect(domain.to_owned(), tcp_stream).await?;
  // let io = TokioIo::new(tls_stream);
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

  tokio::task::spawn(async move {
    if let Err(err) = conn.await {
      println!("Connection failed: {:?}", err);
    }
  });

  // Send the ping requests in background
  let scheme_clone = scheme.clone();
  let host_clone = host.clone();
  tokio::task::spawn(ping::send_ping_requests(
    Arc::clone(&last_active),
    Arc::clone(&goaway_received),
    authority.to_string(),
    sender.clone(),
    lambda_id.clone(),
    Arc::clone(&count),
    scheme_clone,
    host_clone,
    port,
    deadline,
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
              "channel-{}",
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

          tokio::spawn(async move {
              // TODO: Load app_url from config
              let app_url: Uri = "http://127.0.0.1:3001".parse().unwrap();
              let app_host = app_url.host().expect("uri has no host");
              let app_port = app_url.port_u16().unwrap_or(80);
              let app_addr = format!("{}:{}", app_host, app_port);

              // TODO: Only create this when we get a request
              // Setup the contained app connection
              // This is HTTP/1.1 so we need 1 connection for each worker
              let app_tcp_stream = TcpStream::connect(app_addr).await?;
              let app_io = TokioIo::new(app_tcp_stream);
              let (mut app_sender, app_conn) =
                  hyper::client::conn::http1::handshake(app_io).await?;
              tokio::task::spawn(async move {
                  if let Err(err) = app_conn.await {
                      println!("Connection failed: {:?}", err);
                  }
              });

              // This is where HTTP2 loops to make all the requests for a given client and worker
              loop {
                  if goaway_received.load(std::sync::atomic::Ordering::Relaxed) {
                      println!("ChannelId: {}, ChannelNum: {} - GoAway received, exiting loop", channel_id.clone(), channel_number);
                      break;
                  }

                  // Create the router request
                  let (mut tx, recv) = mpsc::channel::<Result<Frame<Bytes>>>(32 * 1024);
                  let boxed_body = BodyExt::boxed(StreamBody::new(recv));
                  let req = Request::builder()
                      .uri(&url)
                      .method("POST")
                      .header(hyper::header::DATE, fmt_http_date(SystemTime::now()))
                      .header(hyper::header::HOST, authority.as_str())
                      .header(hyper::header::CONTENT_TYPE, "application/json")
                      .header("X-Lambda-Id", lambda_id.to_string())
                      .header("X-Channel-Id", channel_id.to_string())
                      .body(boxed_body)?;

                  //
                  // Make the request
                  //

                  // println!("sending request");
                  while futures::future::poll_fn(|ctx| sender.poll_ready(ctx))
                  .await
                  .is_err()
                  {
                      // This gets hit when the connection faults
                      panic!("Connection ready check threw error - connection has disconnected, should reconnect");
                  }

                  let res = sender.send_request(req).await?;
                  // println!("got response: {:?}", res);
                  let (parts, mut res_stream) = res.into_parts();
                  // println!("split response");

                  // If the router returned a 409 when we opened the channel
                  // then we close the channel
                  if parts.status == 409 {
                      println!("ChannelId: {}, ChannelNum: {} - 409 received, exiting loop", channel_id.clone(), channel_number);
                      goaway_received.store(true, std::sync::atomic::Ordering::Relaxed);
                      break;
                  }

                  // Read until we get all the request headers so we can construct our app request
                  let (app_req_builder, is_goaway, left_over_buf)
                      = app_request::read_until_req_headers(&mut res_stream, lambda_id.clone(), channel_id.clone(), app_url.clone()).await?;

                  if is_goaway {
                      println!("ChannelId: {} - GoAway received from read_until_req_headers, exiting", channel_id.clone());
                      goaway_received.store(true, std::sync::atomic::Ordering::Relaxed);
                      tx.close().await.unwrap_or(());
                      break;
                  }

                  // We got a request to run
                  last_active.store(time::current_time_millis(), Ordering::Relaxed);

                  let (mut app_req_tx, app_req_recv) =
                      mpsc::channel::<Result<Frame<Bytes>>>(32 * 1024);
                  let app_req = app_req_builder.body(StreamBody::new(app_req_recv))?;

                  // let app_req =
                  //     app_req_builder.body(http_body_util::Empty::<Bytes>::new())?;

                  while futures::future::poll_fn(|ctx| app_sender.poll_ready(ctx))
                  .await
                  .is_err()
                  {
                      // This gets hit when the connection faults
                      panic!("Connection ready check threw error - connection has disconnected, should reconnect");
                  }

                  // Relay the request body to the contained app
                  // We start a task for this because we need to relay the bytes
                  // between the incoming request and the outgoing request
                  // and we need to set this up before we actually send the request
                  // to the contained app
                  let channel_id_clone = channel_id.clone();
                  let relay_task = tokio::task::spawn(async move {
                      let mut bytes_sent = 0;

                      // Send any overflow body bytes to the contained app
                      if left_over_buf.len() > 0 {
                          bytes_sent += left_over_buf.len();
                          // println!("ChannelId: {} - Sending left over bytes to contained app: {:?}", channel_id_clone.clone(), left_over_buf.len());
                          app_req_tx
                              .send(Ok(Frame::data(left_over_buf.into())))
                              .await
                              .unwrap();
                      }

                      // Source: res_stream
                      // Sink: app_req_tx
                      while let Some(chunk) = futures::future::poll_fn(|cx| {
                          Incoming::poll_frame(Pin::new(&mut res_stream), cx)
                      })
                      .await
                      {
                          if chunk.is_err() {
                              println!("ChannelId: {} - Error reading from res_stream: {:?}", channel_id_clone.clone(), chunk.err());
                              break;
                          }

                          let chunk_len = chunk.as_ref().unwrap().data_ref().unwrap().len();
                          // If chunk_len is zero the channel has closed
                          if chunk_len == 0 {
                            // println!("ChannelId: {}, BytesSent: {}, ChunkLen: {} - Channel closed", channel_id_clone.clone(), bytes_sent, chunk_len);
                            break;
                          }
                          match app_req_tx.send(Ok(chunk.unwrap())).await {
                              Ok(_) => {}
                              Err(err) => {
                                  println!("ChannelId: {}, BytesSent: {}, ChunkLen: {} - Error sending to app_req_tx: {:?}", channel_id_clone.clone(), bytes_sent, chunk_len, err);
                                  break;
                              }
                          }
                          bytes_sent += chunk_len;
                      }
                      // Close the post body stream

                      // This may error if the router close the connection
                      // println!("ChannelId: {} - Closing app_req_tx", channel_id_clone.clone());
                      let _ = app_req_tx.flush().await;
                      let _ = app_req_tx.close().await;
                      // println!("ChannelId: {} - Closed app_req_tx", channel_id_clone.clone());
                  });

                  // Send the request
                  let app_res = app_sender.send_request(app_req).await?;
                  let (app_parts, mut app_res_stream) = app_res.into_parts();

                  //
                  // TODO: Handle incoming POST request by relaying the body
                  //

                  // Close the post body stream
                  // app_tx.close().await?;
                  // drop(app_tx);

                  //
                  // Relay the response
                  //

                  let mut header_buffer = Vec::with_capacity(32 * 1024);

                  // Write the status line
                  let status_line = format!(
                      "HTTP/1.1 {} {}\r\n",
                      app_parts.status.as_u16(),
                      app_parts.status.canonical_reason().unwrap()
                  );
                  let status_line_bytes = status_line.as_bytes();
                  header_buffer.extend(status_line_bytes);

                  // Add two static headers for X-Lambda-Id and X-Channel-Id
                    let lambda_id_header = format!("X-Lambda-Id: {}\r\n", lambda_id);
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
                  while let Some(chunk) = futures::future::poll_fn(|cx| {
                      Incoming::poll_frame(Pin::new(&mut app_res_stream), cx)
                  })
                  .await
                  {
                      if chunk.is_err() {
                          println!("ChannelId: {} - Error reading from app_res_stream: {:?}", channel_id_clone.clone(), chunk.err());
                          break;
                      }

                      let chunk_len = chunk.as_ref().unwrap().data_ref().unwrap().len();
                      // If chunk_len is zero the channel has closed
                      if chunk_len == 0 {
                        break;
                      }

                      tx.send(Ok(chunk.unwrap())).await?;
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
              }
          }
      }
  }

  Ok(())
}
