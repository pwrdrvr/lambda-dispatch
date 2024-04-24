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
  client::conn::http2,
  Request, Uri,
};

use crate::app_request;
use crate::connect_to_app;
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
    let mut app_sender = connect_to_app::connect_to_app(
      &self.app_endpoint,
      Arc::clone(&self.pool_id),
      Arc::clone(&self.lambda_id),
      Arc::clone(&self.channel_id),
    )
    .await?;

    // This is where HTTP2 loops to make all the requests for a given client and worker
    // If the pinger or another channel sets the goaway we will stop looping
    while !self
      .goaway_received
      .load(std::sync::atomic::Ordering::Acquire)
    {
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
        .context(format!("PoolId: {}, LambdaId: {}, ChannelId: {} - Router connection ready check threw error - connection has disconnected, should reconnect", self.pool_id, self.lambda_id, self.channel_id))?;

      // FIXME: This will exit the entire channel when one request errors
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
      // FIXME: This will exit the entire channel when one request errors
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
      // FIXME: This will exit the entire channel when one request errors
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
      let relay_request_task = tokio::task::spawn(async move {
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
              log::error!("PoolId: {}, LambdaId: {}, ChannelId: {}, Reqs in Flight: {}, BytesSent: {}, ChunkLen: {} - Error sending to app_req_tx: {}",
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
      // FIXME: This will exit the entire channel when one request errors
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
          // FIXME: This will exit the entire channel when one request errors
          tx.send(Ok(Frame::data(header_buffer.into()))).await?;

          header_buffer = Vec::new();
          header_buffer.extend(header_bytes);
        }
      }

      // End the headers
      header_buffer.extend(b"\r\n");
      // FIXME: This will exit the entire channel when one request errors
      tx.send(Ok(Frame::data(header_buffer.into()))).await?;

      let mut encoder: Option<GzEncoder<Writer<BytesMut>>> = None;
      if app_res_will_compress {
        encoder = Some(GzEncoder::new(
          BytesMut::new().writer(),
          flate2::Compression::default(),
        ));
      }

      // TODO: This also needs to be a task so we can await the request relay
      // and the response relay at the same time
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
          // FIXME: This will exit the entire channel when one request errors
          encoder.write_all(chunk_data)?;
          // FIXME: This will exit the entire channel when one request errors
          encoder.flush()?;

          let writer = encoder.get_mut();
          let bytes = writer.get_mut().split().into();

          // FIXME: This will exit the entire channel when one request errors
          tx.send(Ok(Frame::data(bytes))).await?;
        } else {
          // FIXME: This will exit the entire channel when one request errors
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
          result = futures::future::try_join_all([relay_request_task, close_router_tx_task]) => {
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

#[cfg(test)]
mod tests {
  use super::*;

  use std::sync::Arc;

  use crate::connect_to_router;
  use crate::{endpoint::Endpoint, test_http2_server::test_http2_server::run_http2_app};

  use axum::response::Response;
  use axum::{extract::Path, routing::post, Router};
  use axum_extra::body::AsyncReadBody;
  use futures::stream::StreamExt;
  use httpmock::{Method::GET, MockServer};
  use hyper::StatusCode;
  use tokio::io::AsyncWriteExt;
  use tokio_test::assert_ok;

  #[tokio::test]
  async fn test_channel_immediate_409() {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();
    let channel_id = "channel_id".to_string();

    // Start router server
    // Use an arc int to count how many times the request endpoint was called
    let request_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let request_count_clone = Arc::clone(&request_count);
    let app = Router::new().route(
      "/api/chunked/request/:lambda_id/:channel_id",
      post(
        move |Path((lambda_id, channel_id)): Path<(String, String)>| {
          let request_count = Arc::clone(&request_count_clone);
          async move {
            // Increment the request count
            request_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
            println!(
              "LambdaID: {}, ChannelID: {}, Request count: {}",
              lambda_id,
              channel_id,
              request_count.load(std::sync::atomic::Ordering::SeqCst)
            );

            // Return a 409
            (StatusCode::CONFLICT, b"Request already made")
          }
        },
      ),
    );
    let mock_router_server = run_http2_app(app);

    let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
      .parse()
      .unwrap();

    // Start app server
    let mock_app_server = MockServer::start();
    let mock_app_healthcheck = mock_app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });
    let app_endpoint: Endpoint = mock_app_server.base_url().parse().unwrap();

    let pool_id_arc: PoolId = pool_id.clone().into();
    let lambda_id_arc: LambdaId = lambda_id.clone().into();

    // Setup our connection to the router
    let sender = connect_to_router::connect_to_router(
      router_endpoint.clone(),
      Arc::clone(&pool_id_arc),
      Arc::clone(&lambda_id_arc),
    )
    .await
    .unwrap();

    // Declare the counts
    let channel_request_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let last_active = Arc::new(std::sync::atomic::AtomicU64::new(0));
    let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(0));

    // Act
    let mut channel = RouterChannel::new(
      Arc::clone(&channel_request_count),
      false,
      Arc::clone(&goaway_received),
      Arc::clone(&last_active),
      Arc::clone(&requests_in_flight),
      router_endpoint,
      app_endpoint,
      0,
      sender,
      pool_id.into(),
      lambda_id.into(),
      channel_id,
    );
    let channel_start_result = channel.start().await;

    // Assert
    assert_ok!(channel_start_result);
    assert_eq!(channel.count.load(std::sync::atomic::Ordering::SeqCst), 0);
    assert_eq!(
      channel
        .requests_in_flight
        .load(std::sync::atomic::Ordering::SeqCst),
      0
    );
    assert_eq!(
      channel
        .last_active
        .load(std::sync::atomic::Ordering::SeqCst),
      0
    );
    assert_eq!(request_count.load(std::sync::atomic::Ordering::SeqCst), 1);
    assert_eq!(
      goaway_received.load(std::sync::atomic::Ordering::SeqCst),
      true
    );

    // Assert app server's healthcheck endpoint did not get called
    mock_app_healthcheck.assert_hits(0);
  }

  #[tokio::test]
  async fn test_channel_read_request_send_to_app() {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();
    let channel_id = "channel_id".to_string();

    // Start router server
    let (release_request_tx, release_request_rx) = tokio::sync::mpsc::channel::<()>(1);
    let release_request_rx = Arc::new(tokio::sync::Mutex::new(release_request_rx));
    // Use an arc int to count how many times the request endpoint was called
    let request_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let request_count_clone = Arc::clone(&request_count);
    let app = Router::new().route(
      "/api/chunked/request/:lambda_id/:channel_id",
      post(
        move |Path((_lambda_id, _channel_id)): Path<(String, String)>,
              request: axum::extract::Request| {
          let request_count = Arc::clone(&request_count_clone);
          let release_request_rx = Arc::clone(&release_request_rx);

          async move {
            // Spawn a task to write to the stream
            tokio::spawn(async move {
              let parts = request.into_parts();

              parts
                .1
                .into_data_stream()
                .for_each(|chunk| async {
                  let chunk = chunk.unwrap();
                  println!("Chunk: {:?}", chunk);
                })
                .await;

              println!(
                "Router Channel - Request body (for contained app response) finished writing"
              );
            });

            // Increment the request count
            request_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
            println!(
              "Request count: {}",
              request_count.load(std::sync::atomic::Ordering::SeqCst)
            );

            // Bail after 1st request
            if request_count.load(std::sync::atomic::Ordering::SeqCst) > 1 {
              let body = AsyncReadBody::new(tokio::io::empty());
              let response = Response::builder()
                .status(StatusCode::CONFLICT)
                .body(body)
                .unwrap();

              return response;
            }

            // Create a channel for the stream
            let (mut tx, rx) = tokio::io::duplex(65_536);

            // Spawn a task to write to the stream
            tokio::spawn(async move {
              // Write some of the headers
              let data = b"GET /bananas HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n";
              tx.write_all(data).await.unwrap();

              // Wait for the release
              release_request_rx.lock().await.recv().await;

              // Write the rest of the headers
              let data = b"Test-Headers: bar\r\nTest-Headerss: baz\r\nAccept-Encoding: gzip\r\n\r\nHELLO WORLD";
              tx.write_all(data).await.unwrap();

              // Close the stream
              tx.shutdown().await.unwrap();

              println!(
                "Router Channel - Response body (for contained app request) finished reading"
              );
            });

            let body = AsyncReadBody::new(rx);

            let response = Response::builder()
              .header("content-type", "application/octet-stream")
              .body(body)
              .unwrap();

            response
          }
        },
      ),
    );

    let mock_router_server = run_http2_app(app);

    let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
      .parse()
      .unwrap();

    let pool_id_arc: PoolId = pool_id.clone().into();
    let lambda_id_arc: LambdaId = lambda_id.clone().into();

    // Start app server
    let mock_app_server = MockServer::start();
    let mock_app_healthcheck = mock_app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });
    let mock_app_bananas = mock_app_server.mock(|when, then| {
      when.method(GET).path("/bananas");
      then
        .status(200)
        .header("Content-Type", "text/plain")
        .header("Connection", "close")
        .header("Keep-Alive", "timeout=5")
        .body("Bananas");
    });
    let app_endpoint: Endpoint = mock_app_server.base_url().parse().unwrap();

    // Setup our connection to the router
    let sender = connect_to_router::connect_to_router(
      router_endpoint.clone(),
      Arc::clone(&pool_id_arc),
      Arc::clone(&lambda_id_arc),
    )
    .await
    .unwrap();

    // Release the request after a few seconds
    tokio::spawn(async move {
      tokio::time::sleep(tokio::time::Duration::from_secs(1)).await;
      release_request_tx.send(()).await.unwrap();
    });

    let channel_request_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let last_active = Arc::new(std::sync::atomic::AtomicU64::new(0));
    let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(0));

    // Act
    let mut channel = RouterChannel::new(
      Arc::clone(&channel_request_count),
      true, // compression
      Arc::clone(&goaway_received),
      Arc::clone(&last_active),
      Arc::clone(&requests_in_flight),
      router_endpoint,
      app_endpoint,
      0,
      sender,
      pool_id.into(),
      lambda_id.into(),
      channel_id,
    );
    let channel_start_result = channel.start().await;
    // Assert
    assert_ok!(channel_start_result);
    assert_eq!(
      channel.count.load(std::sync::atomic::Ordering::SeqCst),
      1,
      "channel should have finished a request"
    );
    assert_eq!(
      channel
        .requests_in_flight
        .load(std::sync::atomic::Ordering::SeqCst),
      0
    );
    assert_ne!(
      channel
        .last_active
        .load(std::sync::atomic::Ordering::SeqCst),
      0,
      "last active should not be 0"
    );
    // last active should be close to current_time_millis
    assert!(
      time::current_time_millis()
        - channel
          .last_active
          .load(std::sync::atomic::Ordering::SeqCst)
        < 1000,
      "last active should be close to current_time_millis"
    );
    assert_eq!(
      request_count.load(std::sync::atomic::Ordering::SeqCst),
      2,
      "router channel requests received should be 2"
    );
    assert_eq!(
      goaway_received.load(std::sync::atomic::Ordering::SeqCst),
      true,
      "goaway received should be true"
    );

    // Assert app server's healthcheck endpoint did not get called
    mock_app_healthcheck.assert_hits(0);

    // Assert that the test route did get called
    mock_app_bananas.assert_hits(1);
  }

  #[tokio::test]
  async fn test_channel_goaway_on_body() {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();
    let channel_id = "channel_id".to_string();

    // Start router server
    let (release_request_tx, release_request_rx) = tokio::sync::mpsc::channel::<()>(1);
    let release_request_rx = Arc::new(tokio::sync::Mutex::new(release_request_rx));
    // Use an arc int to count how many times the request endpoint was called
    let request_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let request_count_clone = Arc::clone(&request_count);
    let app = Router::new().route(
      "/api/chunked/request/:lambda_id/:channel_id",
      post(
        move |Path((_lambda_id, _channel_id)): Path<(String, String)>| {
          let request_count = Arc::clone(&request_count_clone);
          let release_request_rx = Arc::clone(&release_request_rx);

          async move {
            // Increment the request count
            request_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
            println!(
              "Request count: {}",
              request_count.load(std::sync::atomic::Ordering::SeqCst)
            );

            // Create a channel for the stream
            let (mut tx, rx) = tokio::io::duplex(65_536);

            // Spawn a task to write to the stream
            tokio::spawn(async move {
              // Write some of the headers
              let data =
                b"GET /_lambda_dispatch/goaway HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n";
              tx.write_all(data).await.unwrap();

              // Wait for the release
              // This is panicing on unwrapping the () from the channel
              // Fix it
              release_request_rx.lock().await.recv().await;

              // Write the rest of the headers
              let data = b"Test-Headers: bar\r\nTest-Headerss: baz\r\n\r\nHELLO WORLD";
              tx.write_all(data).await.unwrap();

              // Close the stream
              tx.shutdown().await.unwrap();

              println!(
                "Router Channel - Response body (for contained app request) finished reading"
              );
            });

            let body = AsyncReadBody::new(rx);

            let response = Response::builder()
              .header("content-type", "application/octet-stream")
              .body(body)
              .unwrap();

            response
          }
        },
      ),
    );

    let mock_router_server = run_http2_app(app);

    let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
      .parse()
      .unwrap();

    let pool_id_arc: PoolId = pool_id.clone().into();
    let lambda_id_arc: LambdaId = lambda_id.clone().into();

    // Start app server
    let mock_app_server = MockServer::start();
    let mock_app_healthcheck = mock_app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });
    let app_endpoint: Endpoint = mock_app_server.base_url().parse().unwrap();

    // Setup our connection to the router
    let sender = connect_to_router::connect_to_router(
      router_endpoint.clone(),
      Arc::clone(&pool_id_arc),
      Arc::clone(&lambda_id_arc),
    )
    .await
    .unwrap();

    // Release the request after a few seconds
    tokio::spawn(async move {
      tokio::time::sleep(tokio::time::Duration::from_secs(1)).await;
      release_request_tx.send(()).await.unwrap();
    });

    let channel_request_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let last_active = Arc::new(std::sync::atomic::AtomicU64::new(0));
    let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(0));

    // Act
    let mut channel = RouterChannel::new(
      Arc::clone(&channel_request_count),
      false, // compression
      Arc::clone(&goaway_received),
      Arc::clone(&last_active),
      Arc::clone(&requests_in_flight),
      router_endpoint,
      app_endpoint,
      0,
      sender,
      pool_id.into(),
      lambda_id.into(),
      channel_id,
    );
    let channel_start_result = channel.start().await;
    // Assert
    assert_ok!(channel_start_result);
    assert_eq!(
      channel.count.load(std::sync::atomic::Ordering::SeqCst),
      0,
      "channel handled request count"
    );
    assert_eq!(
      channel
        .requests_in_flight
        .load(std::sync::atomic::Ordering::SeqCst),
      0
    );
    assert_ne!(
      channel
        .last_active
        .load(std::sync::atomic::Ordering::SeqCst),
      0,
      "last active should not be 0"
    );
    // last active should be close to current_time_millis
    assert!(
      time::current_time_millis()
        - channel
          .last_active
          .load(std::sync::atomic::Ordering::SeqCst)
        < 2000,
      "last active should be close to current_time_millis"
    );
    assert_eq!(
      request_count.load(std::sync::atomic::Ordering::SeqCst),
      1,
      "router channel requests received"
    );
    assert_eq!(
      goaway_received.load(std::sync::atomic::Ordering::SeqCst),
      true,
      "goaway received should be true"
    );

    // Assert app server's healthcheck endpoint did not get called
    mock_app_healthcheck.assert_hits(0);
  }
}
