use crate::app_client::AppClient;
use crate::lambda_request_error::LambdaRequestError;
use crate::ping::send_close_request;
use crate::relay::{relay_request_to_app, relay_response_to_router};
use crate::router_client::RouterClient;
use crate::utils::compressable;
use crate::{messages, prelude::*};

use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::SystemTime;

use bytes::{buf::Writer, BufMut, BytesMut};
use futures::{channel::mpsc, SinkExt};
use http_body_util::{BodyExt, StreamBody};
use httpdate::fmt_http_date;
use hyper::{
  body::{Bytes, Frame},
  Request, StatusCode, Uri,
};

use crate::app_request;
use crate::counter_drop::DecrementOnDrop;
use crate::endpoint::Endpoint;
use crate::time;

use flate2::write::GzEncoder;

#[derive(PartialEq, Debug)]
pub enum ChannelResult {
  GoAwayReceived,
  RouterStatus5xx,
  RouterStatus4xx,
  RouterStatusOther,
}

impl From<ChannelResult> for messages::ExitReason {
  fn from(result: ChannelResult) -> Self {
    match result {
      ChannelResult::GoAwayReceived => messages::ExitReason::RouterGoAway,
      ChannelResult::RouterStatus5xx => messages::ExitReason::RouterStatus5xx,
      ChannelResult::RouterStatus4xx => messages::ExitReason::RouterStatus4xx,
      ChannelResult::RouterStatusOther => messages::ExitReason::RouterStatusOther,
    }
  }
}

#[derive(Clone)]
pub struct RouterChannel {
  pub count: Arc<AtomicUsize>,
  compression: bool,
  goaway_received: Arc<AtomicBool>,
  pub last_active: Arc<AtomicU64>,
  pub requests_in_flight: Arc<AtomicUsize>,
  channel_url: Uri,
  app_endpoint: Endpoint,
  router_endpoint: Endpoint,
  channel_number: u8,
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
      router_endpoint,
      channel_number,
      pool_id,
      lambda_id,
      channel_id: channel_id.into(),
    }
  }

  pub async fn start(
    &mut self,
    app_client: AppClient,
    router_client: RouterClient,
  ) -> Result<Option<ChannelResult>, LambdaRequestError> {
    // This is where HTTP2 loops to make all the requests for a given client and worker
    // If the pinger or another channel sets the goaway we will stop looping
    while !self
      .goaway_received
      .load(std::sync::atomic::Ordering::Acquire)
    {
      match self
        .pickup_request(app_client.clone(), router_client.clone())
        .await
      {
        Ok(Some(channel_result)) => {
          // Channel is closing for an indicated reason
          return Ok(Some(channel_result));
        }
        Ok(None) => {
          // Channel is still open
        }
        Err(err) => {
          if err == LambdaRequestError::ChannelErrorOther {
            // This is a recoverable error, we can continue
            continue;
          }

          log::error!(
            "PoolId: {}, LambdaId: {}, ChannelId: {}, ChannelNum: {} - Error in handle_one_loop: {}",
            self.pool_id,
            self.lambda_id,
            self.channel_id,
            self.channel_number,
            err
          );
          return Err(err);
        }
      }
    }

    Ok(None)
  }

  async fn pickup_request(
    &mut self,
    app_client: AppClient,
    router_client: RouterClient,
  ) -> Result<Option<ChannelResult>, LambdaRequestError> {
    // NOTE: Even though this has a `_` prefix, it IS used to decrement
    // the requests_in_flight counter when it goes out of scope
    let mut _decrement_on_drop = None;
    let mut channel_result = None;

    // Create the router request
    let (mut router_tx, router_recv) = mpsc::channel::<Result<Frame<Bytes>>>(32 * 1024);
    let boxed_body = BodyExt::boxed(StreamBody::new(router_recv));
    let router_request = Request::builder()
      .version(hyper::Version::HTTP_2)
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
      .body(boxed_body)
      .map_err(|_| LambdaRequestError::ChannelErrorOther)?;

    //
    // Make the request to the router
    //
    let router_response = router_client.request(router_request).await.map_err(|err| {
      if err.is_connect() {
        LambdaRequestError::RouterUnreachable
      } else {
        LambdaRequestError::RouterConnectionError
      }
    })?; // If we fail to contact the router when picking up a request, then we want to exit
    let (router_response_parts, mut router_response_stream) = router_response.into_parts();
    log::debug!(
      "PoolId: {}, LambdaId: {}, ChannelId: {} - split response",
      self.pool_id,
      self.lambda_id,
      self.channel_id,
    );

    // If the router returned a 409 when we opened the channel
    // then we close the channel
    if router_response_parts.status == StatusCode::CONFLICT {
      if !self
        .goaway_received
        .load(std::sync::atomic::Ordering::Acquire)
      {
        log::info!("PoolId: {}, LambdaId: {}, ChannelId: {}, ChannelNum: {}, Reqs in Flight: {} - 409 received, exiting loop",
                  self.pool_id, self.lambda_id, self.channel_id, self.channel_number, self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire));

        // This is from a goaway so we do not need to ask to close
        self
          .goaway_received
          .store(true, std::sync::atomic::Ordering::Release);
      }
      router_tx.close().await.unwrap_or(());
      return Ok(Some(ChannelResult::GoAwayReceived));
    }

    if router_response_parts.status != StatusCode::OK {
      channel_result = if router_response_parts.status.is_server_error() {
        Some(ChannelResult::RouterStatus5xx)
      } else if router_response_parts.status.is_client_error() {
        Some(ChannelResult::RouterStatus4xx)
      } else {
        Some(ChannelResult::RouterStatusOther)
      };
      log::info!(
          "PoolId: {}, LambdaId: {}, ChannelId: {}, ChannelNum: {}, Reqs in Flight: {} - Channel Loop - non-200 received on ping, exiting: {:?}",
          self.pool_id,
          self.lambda_id,
          self.channel_id, self.channel_number, self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire),
          router_response_parts.status);

      // TODO: This is not from a goaway, so we need to ask
      // the router to close our invoke
      self.goaway_received.store(true, Ordering::Release);
      return Ok(channel_result);
    }

    // On first request this will release the ping task to start
    // We have to hold the pinger up else it will exit before connections to the router are established
    // We also count re-establishing a channel as an action since it can allow a request to flow in
    if self.last_active.load(Ordering::Acquire) == 0 {
      log::debug!("PoolId: {}, LambdaId: {}, ChannelId: {}, ChannelNum: {}, Reqs in Flight: {} - First request, releasing pinger",
                self.pool_id, self.lambda_id, self.channel_id, self.channel_number, self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire));
    }
    self
      .last_active
      .store(time::current_time_millis(), Ordering::Release);

    // Read until we get all the request headers so we can construct our app request
    let (app_req_builder, is_goaway, left_over_buf) = app_request::read_until_req_headers(
      self.app_endpoint.clone(),
      &mut router_response_stream,
      &self.pool_id,
      &self.lambda_id,
      &self.channel_id,
    )
    .await
    .map_err(|e| {
      log::error!("PoolId: {}, LambdaId: {}, ChannelId: {}, ChannelNum: {}, Reqs in Flight: {} - Error reading request headers: {}",
      self.pool_id, self.lambda_id, self.channel_id, self.channel_number, self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire), e);
      e
    })?; // TODO: We can continue in this case. This can happen if clients were sending lots of
         // headers then pausing and timing out or canceling their request to the router, which
         // would then cancel the request to the extension. We should be able to continue,
         // but we may want a circuit breaker that exits if this happens too many times

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
        channel_result = Some(ChannelResult::GoAwayReceived);
        log::debug!("PoolId: {}, LambdaId: {}, ChannelId: {}, ChannelNum: {}, Reqs in Flight: {} - GoAway received, exiting loop",
                    self.pool_id, self.lambda_id, self.channel_id, self.channel_number, self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire));

        // This is from a goaway so we do not need to ask to close
        self
          .goaway_received
          .store(true, std::sync::atomic::Ordering::Release);
      }
      router_tx.close().await.unwrap_or(());
      return Ok(channel_result);
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
    let (app_req_tx, app_req_recv) = mpsc::channel::<Result<Frame<Bytes>>>(32 * 1024);
    let app_req = app_req_builder
      .body(StreamBody::new(app_req_recv))
      .map_err(|e| {
        log::error!("PoolId: {}, LambdaId: {}, ChannelId: {}, ChannelNum: {}, Reqs in Flight: {} - Error building request to send to contained app: {}",
                    self.pool_id, self.lambda_id, self.channel_id, self.channel_number, self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire), e);
        // This should not close the channel as it can happen with invalid headers
        // This should continue on to the next request
        LambdaRequestError::ChannelErrorOther
    })?;

    // Relay the request body to the contained app
    // This is setup before the request is sent
    // because most servers will not send response headers
    // until they have received the request body and run the request.
    // Moving this after the request is sent will deadlock in cases
    // like the above.
    // FIXME: We need the ability to cancel this task if the app connection errors
    let relay_request_to_app_task = tokio::task::spawn(relay_request_to_app(
      left_over_buf,
      self.pool_id.clone(),
      self.lambda_id.clone(),
      self.channel_id.clone(),
      Arc::clone(&self.requests_in_flight),
      app_req_tx,
      router_response_stream,
    ));

    //
    // Send the request to the contained app.
    // request waits for tx connection to be ready.
    // https://github.com/hyperium/hyper-util/blob/5688b2733eee146a1df1fc3b5263cd2021974d9b/src/client/legacy/client.rs#L574-L576
    //
    let app_res = match app_client.request(app_req).await {
      Ok(res) => res,
      Err(err) => {
        if err.is_connect() {
          // FIXME: Cancel the request relay
          // Ask the router to close our invoke gracefully
          send_close_request(
            Arc::clone(&self.goaway_received),
            router_client,
            Arc::clone(&self.pool_id),
            Arc::clone(&self.lambda_id),
            self.router_endpoint.clone(),
          )
          .await;

          return Err(LambdaRequestError::AppConnectionUnreachable);
        }
        return Err(LambdaRequestError::AppConnectionError);
      }
    };
    let (app_res_parts, app_res_stream) = app_res.into_parts();

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

    let app_res_will_compress = self.compression
      && accepts_gzip
      && compressable(&app_res_parts.headers).map_err(|_| LambdaRequestError::ChannelErrorOther)?;

    // If we're going to gzip the response, add the content-encoding header
    if app_res_will_compress {
      let content_encoding = format!("content-encoding: {}\r\n", "gzip");
      let content_encoding_bytes = content_encoding.as_bytes();
      header_buffer.extend(content_encoding_bytes);
    }

    // Send the headers to the caller
    for header in app_res_parts.headers.iter() {
      let header_name = header.0.as_str();
      let header_value = header
        .1
        .to_str()
        .map_err(|_| LambdaRequestError::ChannelErrorOther)?;

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
        router_tx
          .send(Ok(Frame::data(header_buffer.into())))
          .await
          .map_err(|_| LambdaRequestError::ChannelErrorOther)?;

        header_buffer = Vec::new();
        header_buffer.extend(header_bytes);
      }
    }

    // End the headers
    header_buffer.extend(b"\r\n");
    router_tx
      .send(Ok(Frame::data(header_buffer.into())))
      .await
      .map_err(|_| LambdaRequestError::ChannelErrorOther)?;

    let mut encoder: Option<GzEncoder<Writer<BytesMut>>> = None;
    if app_res_will_compress {
      encoder = Some(GzEncoder::new(
        BytesMut::new().writer(),
        flate2::Compression::default(),
      ));
    }

    // Reads from: App response body stream
    // Writes to: Router request body stream
    let relay_response_to_router_task = tokio::task::spawn(relay_response_to_router(
      self.pool_id.clone(),
      self.lambda_id.clone(),
      self.channel_id.clone(),
      Arc::clone(&self.requests_in_flight),
      app_res_stream,
      encoder,
      router_tx,
    ));

    // Wait for both to finish
    match futures::future::try_join_all([relay_request_to_app_task, relay_response_to_router_task])
      .await
    {
      Ok(result) => {
        // Find the worst error, if any
        let mut worst_error = None;

        // This case can have errors in the vector
        for res in result {
          if let Err(err) = res {
            log::error!("PoolId: {}, LambdaId: {}, ChannelId: {} - Error in futures::future::try_join_all: {}", self.pool_id, self.lambda_id, self.channel_id, err);
            worst_error =
              Some(err.worse(worst_error.unwrap_or(LambdaRequestError::ChannelErrorOther)));
          }
        }

        if let Some(err) = worst_error {
          return Err(err);
        }
      }
      Err(err) => {
        // try_join_all will return an error if either of the futures panics

        log::error!(
          "PoolId: {}, LambdaId: {}, ChannelId: {} - pickup-request - Error in futures::future::try_join_all: {}",
          self.pool_id,
          self.lambda_id,
          self.channel_id,
          err
        );

        // We don't have details about which future failed, so we return a generic error
        return Err(LambdaRequestError::ChannelErrorOther);
      }
    }

    self.count.fetch_add(1, std::sync::atomic::Ordering::AcqRel);

    Ok(None)
  }
}
