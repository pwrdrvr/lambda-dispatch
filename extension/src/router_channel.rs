use crate::prelude::*;
use std::io::Write;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::time::SystemTime;
use std::{pin::Pin, sync::Arc};

use bytes::{buf::Writer, BufMut, BytesMut};
use futures::channel::mpsc;
use futures::SinkExt;
use http_body_util::{combinators::BoxBody, BodyExt, StreamBody};
use httpdate::fmt_http_date;
use hyper::{
  body::{Body, Bytes, Frame, Incoming},
  client::conn::{http1, http2},
  Request, Uri,
};
use hyper_util::rt::TokioIo;
use mpsc::Receiver;
use tokio::net::TcpStream;

use crate::app_request;
use crate::counter_drop::DecrementOnDrop;
use crate::endpoint::Endpoint;
use crate::time;

use flate2::write::GzEncoder;

#[derive(Clone)]
pub struct RouterChannel {
  count: Arc<AtomicUsize>,
  compression: bool,
  goaway_received: Arc<AtomicBool>,
  last_active: Arc<AtomicU64>,
  requests_in_flight: Arc<AtomicUsize>,
  channel_url: Uri,
  app_endpoint: Endpoint,
  channel_number: u8,
  sender: http2::SendRequest<BoxBody<Bytes, Error>>,
  pool_id: PoolId,
  lambda_id: LambdaId,
  channel_id: ChannelId,
}

impl RouterChannel {
  pub fn new(
    count: Arc<AtomicUsize>,
    compression: bool,
    goaway_received: Arc<AtomicBool>,
    last_active: Arc<AtomicU64>,
    requests_in_flight: Arc<AtomicUsize>,
    router_endpoint: Endpoint,
    app_endpoint: Endpoint,
    channel_number: u8,
    sender: http2::SendRequest<BoxBody<Bytes, Error>>,
    pool_id: PoolId,
    lambda_id: LambdaId,
    channel_id: String,
  ) -> Self {
    let channel_url = format!(
      "{}://{}:{}/api/chunked/request/{}/{}",
      router_endpoint.scheme().as_str(),
      router_endpoint.host(),
      router_endpoint.port(),
      lambda_id,
      channel_id
    )
    .parse()
    .unwrap();

    RouterChannel {
      count,
      compression,
      goaway_received,
      last_active,
      requests_in_flight,
      channel_url,
      app_endpoint,
      channel_number,
      sender,
      pool_id,
      lambda_id,
      channel_id: channel_id.into(),
    }
  }

  pub async fn start(&mut self) -> Result<()> {
    let mut app_sender = connect_to_app(
      &self.app_endpoint,
      Arc::clone(&self.pool_id),
      Arc::clone(&self.lambda_id),
      Arc::clone(&self.channel_id),
    )
    .await?;

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
        .header(hyper::header::HOST, self.channel_url.host().unwrap())
        // The content-type that we're sending to the router is opaque
        // as it contains another HTTP request/response, so may start as text
        // with request/headers and then be binary after that - it should not be parsed
        // by anything other than us
        .header(hyper::header::CONTENT_TYPE, "application/octet-stream")
        .header("X-Pool-Id", self.pool_id.as_ref())
        .header("X-Lambda-Id", self.lambda_id.as_ref())
        .header("X-Channel-Id", self.channel_id.as_ref())
        .body(boxed_body)?;

      //
      // Make the request to the router
      //

      log::debug!(
        "PoolId: {}, LambdaId: {}, ChannelId: {} - sending request",
        self.pool_id,
        self.lambda_id,
        self.channel_id
      );

      self.sender
                .ready()
                .await
                .context("PoolId: {}, LambdaId: {}, ChannelId: {} - Router connection ready check threw error - connection has disconnected, should reconnect")?;

      let res = self.sender.send_request(req).await?;
      log::debug!(
        "PoolId: {}, LambdaId: {}, ChannelId: {} - got response: {:?}",
        self.pool_id,
        self.lambda_id,
        self.channel_id,
        res
      );
      let (parts, mut res_stream) = res.into_parts();
      log::debug!(
        "PoolId: {}, LambdaId: {}, ChannelId: {} - split response",
        self.pool_id,
        self.lambda_id,
        self.channel_id,
      );

      // If the router returned a 409 when we opened the channel
      // then we close the channel
      if parts.status == 409 {
        if !self
          .goaway_received
          .load(std::sync::atomic::Ordering::Acquire)
        {
          log::info!("PoolId: {}, LambdaId: {}, ChannelId: {}, ChannelNum: {}, Reqs in Flight: {} - 409 received, exiting loop",
                  self.pool_id, self.lambda_id, self.channel_id, self.channel_number, self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire));
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
        log::info!("PoolId: {}, LambdaId: {}, ChannelId: {}, ChannelNum: {}, Reqs in Flight: {} - First request, releasing pinger",
                self.pool_id, self.lambda_id, self.channel_id, self.channel_number, self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire));
      }
      self
        .last_active
        .store(time::current_time_millis(), Ordering::Release);

      // Read until we get all the request headers so we can construct our app request
      let (app_req_builder, is_goaway, left_over_buf) = app_request::read_until_req_headers(
        &mut res_stream,
        &self.pool_id,
        &self.lambda_id,
        &self.channel_id,
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
          log::info!("PoolId: {}, LambdaId: {}, ChannelId: {}, ChannelNum: {}, Reqs in Flight: {} - GoAway received, exiting loop",
                    self.pool_id, self.lambda_id, self.channel_id, self.channel_number, self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire));
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

      if app_sender.ready().await.is_err() {
        // This gets hit when the app connection faults
        panic!("PoolId: {}, LambdaId: {}, ChannelId: {}, Reqs in Flight: {} - App connection ready check threw error - connection has disconnected, should reconnect",
                  self.pool_id, self.lambda_id, self.channel_id, self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire));
      }

      // Relay the request body to the contained app
      // We start a task for this because we need to relay the bytes
      // between the incoming request and the outgoing request
      // and we need to set this up before we actually send the request
      // to the contained app
      let channel_id_clone = self.channel_id.clone();
      let pool_id_clone = self.pool_id.clone();
      let lambda_id_clone = self.lambda_id.clone();
      let requests_in_flight_clone = Arc::clone(&self.requests_in_flight);
      // let requests_in_flight_clone = Arc::clone(&self.requests_in_flight);
      let relay_task = tokio::task::spawn(async move {
        let mut bytes_sent = 0;

        // Send any overflow body bytes to the contained app
        if !left_over_buf.is_empty() {
          bytes_sent += left_over_buf.len();
          log::debug!(
            "PoolId: {}, LambdaId: {}, ChannelId: {} - Sending left over bytes to contained app: {:?}",
            pool_id_clone,
            lambda_id_clone,
            channel_id_clone,
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
            log::error!("LambdaId: {}, ChannelId: {}, Reqs in Flight: {}, BytesSent: {} - Error reading from res_stream: {:?}",
                          lambda_id_clone,
                          channel_id_clone,
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
              "PoolId: {}, LambdaId: {}, ChannelId: {}, BytesSent: {}, ChunkLen: {} - Channel closed",
              pool_id_clone,
              lambda_id_clone,
              channel_id_clone,
              bytes_sent,
              chunk_len
            );
            break;
          }
          match app_req_tx.send(Ok(chunk.unwrap())).await {
            Ok(_) => {}
            Err(err) => {
              log::error!("PoolId: {}, LambdaId: {}, ChannelId: {}, Reqs in Flight: {}, BytesSent: {}, ChunkLen: {} - Error sending to app_req_tx: {:?}",
                            pool_id_clone,
                            lambda_id_clone,
                            channel_id_clone,
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
          log::info!("PoolId: {}, LambdaId: {}, ChannelId: {}, BytesSent: {} - Error reading from res_stream, dropping app_req_tx", pool_id_clone, lambda_id_clone, channel_id_clone, bytes_sent);
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
      let lambda_id_header = format!("X-Lambda-Id: {}\r\n", self.lambda_id);
      let lambda_id_header_bytes = lambda_id_header.as_bytes();
      header_buffer.extend(lambda_id_header_bytes);
      let channel_id_header = format!("X-Channel-Id: {}\r\n", self.channel_id);
      let channel_id_header_bytes = channel_id_header.as_bytes();
      header_buffer.extend(channel_id_header_bytes);

      // Check if we have a Content-Encoding response header
      // If we do, we should not gzip the response
      let app_res_compressed = app_res_parts.headers.get("content-encoding").is_some();

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

      let mut encoder: Option<GzEncoder<Writer<BytesMut>>> = None;
      if app_res_will_compress {
        encoder = Some(GzEncoder::new(
          BytesMut::new().writer(),
          flate2::Compression::default(),
        ));
      }

      // Rip the bytes back to the caller
      let channel_id_clone = self.channel_id.clone();
      let mut app_error_reading = false;
      let mut bytes_read = 0;
      while let Some(chunk) =
        futures::future::poll_fn(|cx| Incoming::poll_frame(Pin::new(&mut app_res_stream), cx)).await
      {
        if chunk.is_err() {
          log::info!("PoolId: {}, LambdaId: {}, ChannelId: {}, Reqs in Flight: {}, BytesRead: {} - Error reading from app_res_stream: {:?}",
            self.pool_id,
            self.lambda_id,
            self.channel_id,
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

        if let Some(ref mut encoder) = encoder {
          let chunk_data = chunk.as_ref().unwrap().data_ref().unwrap();
          encoder.write_all(chunk_data)?;
          encoder.flush()?;

          let writer = encoder.get_mut();
          let bytes = writer.get_mut().split().into();
          tx.send(Ok(Frame::data(bytes))).await?;
        } else {
          tx.send(Ok(chunk.unwrap())).await?;
        }
      }
      if app_error_reading {
        log::debug!(
          "PoolId: {}, LambdaId: {}, ChannelId: {} - Error reading from app_res_stream, dropping tx",
          self.pool_id,
          self.lambda_id,
          self.channel_id
        );
      }

      let close_router_tx_task = tokio::task::spawn(async move {
        if let Some(encoder) = encoder.take() {
          let writer = encoder.finish().unwrap();
          let bytes = writer.into_inner().into();

          tx.send(Ok(Frame::data(bytes))).await.unwrap();
        }
        let _ = tx.flush().await;
        let _ = tx.close().await;
      });

      // Wait for both to finish
      tokio::select! {
          result = futures::future::try_join_all([relay_task, close_router_tx_task]) => {
              match result {
                  Ok(_) => {
                      // All tasks completed successfully
                  }
                  Err(_) => {
                      panic!("PoolId: {}, LambdaId: {}, ChannelId: {} - Error in futures::future::try_join_all", self.pool_id, self.lambda_id, channel_id_clone);
                  }
              }
          }
      }

      self.count.fetch_add(1, std::sync::atomic::Ordering::AcqRel);
    }

    Ok(())
  }
}

async fn connect_to_app(
  app_endpoint: &Endpoint,
  pool_id: PoolId,
  lambda_id: LambdaId,
  channel_id: ChannelId,
) -> Result<http1::SendRequest<StreamBody<Receiver<Result<Frame<Bytes>>>>>> {
  // Setup the contained app connection
  // This is HTTP/1.1 so we need 1 connection for each worker
  let tcp_stream = TcpStream::connect(app_endpoint.socket_addr_coercable()).await?;
  let io = TokioIo::new(tcp_stream);
  let (sender, conn) = hyper::client::conn::http1::handshake(io).await?;

  tokio::task::spawn(async move {
    if let Err(err) = conn.await {
      log::error!(
        "PoolId: {}, LambdaId: {}, ChannelId: {} - Contained App connection failed: {:?}",
        pool_id,
        lambda_id,
        channel_id,
        err
      );
    }
  });
  Ok(sender)
}
