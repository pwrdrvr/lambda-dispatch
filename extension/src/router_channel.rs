use crate::app_client::AppClient;
use crate::lambda_request_error::LambdaRequestError;
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
  count: Arc<AtomicUsize>,
  compression: bool,
  goaway_received: Arc<AtomicBool>,
  last_active: Arc<AtomicU64>,
  requests_in_flight: Arc<AtomicUsize>,
  channel_url: Uri,
  app_endpoint: Endpoint,
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
      self.goaway_received.store(true, Ordering::Release);
      return Ok(channel_result);
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
        log::info!("PoolId: {}, LambdaId: {}, ChannelId: {}, ChannelNum: {}, Reqs in Flight: {} - GoAway received, exiting loop",
                    self.pool_id, self.lambda_id, self.channel_id, self.channel_number, self.requests_in_flight.load(std::sync::atomic::Ordering::Acquire));
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
          // FIXME: Close the router request/response
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
        panic!(
          "PoolId: {}, LambdaId: {}, ChannelId: {} - Error in futures::future::try_join_all: {}",
          self.pool_id, self.lambda_id, self.channel_id, err
        );
      }
    }

    self.count.fetch_add(1, std::sync::atomic::Ordering::AcqRel);

    Ok(None)
  }
}

#[cfg(test)]
mod tests {
  use super::*;

  use std::sync::Arc;

  use crate::app_client::create_app_client;
  use crate::endpoint::Endpoint;
  use crate::router_client::create_router_client;
  use crate::test_mock_router;

  use httpmock::{Method::GET, MockServer};
  use hyper::StatusCode;
  use tokio_test::assert_ok;

  #[tokio::test]
  async fn test_channel_status_305_http() {
    fixture_channel_status_code(
      StatusCode::USE_PROXY,
      Some(ChannelResult::RouterStatusOther),
      test_mock_router::ListenerType::Http,
    )
    .await;
  }

  #[tokio::test]
  async fn test_channel_status_305_https() {
    fixture_channel_status_code(
      StatusCode::USE_PROXY,
      Some(ChannelResult::RouterStatusOther),
      test_mock_router::ListenerType::Https,
    )
    .await;
  }

  #[tokio::test]
  async fn test_channel_status_400_http() {
    fixture_channel_status_code(
      StatusCode::BAD_REQUEST,
      Some(ChannelResult::RouterStatus4xx),
      test_mock_router::ListenerType::Http,
    )
    .await;
  }

  #[tokio::test]
  async fn test_channel_status_400_https() {
    fixture_channel_status_code(
      StatusCode::BAD_REQUEST,
      Some(ChannelResult::RouterStatus4xx),
      test_mock_router::ListenerType::Https,
    )
    .await;
  }

  #[tokio::test]
  async fn test_channel_status_409_http() {
    fixture_channel_status_code(
      StatusCode::CONFLICT,
      Some(ChannelResult::GoAwayReceived),
      test_mock_router::ListenerType::Http,
    )
    .await;
  }

  #[tokio::test]
  async fn test_channel_status_409_https() {
    fixture_channel_status_code(
      StatusCode::CONFLICT,
      Some(ChannelResult::GoAwayReceived),
      test_mock_router::ListenerType::Https,
    )
    .await;
  }

  #[tokio::test]
  async fn test_channel_status_500_http() {
    fixture_channel_status_code(
      StatusCode::INTERNAL_SERVER_ERROR,
      Some(ChannelResult::RouterStatus5xx),
      test_mock_router::ListenerType::Http,
    )
    .await;
  }

  #[tokio::test]
  async fn test_channel_status_500_https() {
    fixture_channel_status_code(
      StatusCode::INTERNAL_SERVER_ERROR,
      Some(ChannelResult::RouterStatus5xx),
      test_mock_router::ListenerType::Https,
    )
    .await;
  }

  async fn fixture_channel_status_code(
    status_code: StatusCode,
    expected_result: Option<ChannelResult>,
    listener_type: test_mock_router::ListenerType,
  ) {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();
    let channel_id = "channel_id".to_string();

    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::Get,
      channel_non_200_status_after_count: 0,
      channel_non_200_status_code: status_code,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type,
    });

    let router_endpoint: Endpoint = format!(
      "{}://localhost:{}",
      listener_type.to_string(),
      mock_router_server.server.addr.port()
    )
    .parse()
    .unwrap();

    // Start app server
    let mock_app_server = MockServer::start();
    let mock_app_healthcheck = mock_app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });
    let app_endpoint: Endpoint = mock_app_server.base_url().parse().unwrap();

    let app_client = create_app_client();
    let router_client = create_router_client();

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
      pool_id.into(),
      lambda_id.into(),
      channel_id,
    );
    let channel_start_result = channel.start(app_client, router_client).await;

    // Assert
    assert!(channel_start_result.is_ok(), "channel start result is ok");
    let channel_start_result = channel_start_result.unwrap();
    assert_eq!(channel_start_result, expected_result, "result expected");
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
    assert_eq!(
      mock_router_server
        .request_count
        .load(std::sync::atomic::Ordering::SeqCst),
      1
    );
    assert!(goaway_received.load(std::sync::atomic::Ordering::SeqCst));

    // Assert app server's healthcheck endpoint did not get called
    mock_app_healthcheck.assert_hits(0);
  }

  #[tokio::test]
  async fn test_channel_read_request_send_to_app_http() {
    fixture_channel_read_request_send_to_app(test_mock_router::ListenerType::Http).await;
  }

  #[tokio::test]
  async fn test_channel_read_request_send_to_app_https() {
    fixture_channel_read_request_send_to_app(test_mock_router::ListenerType::Https).await;
  }

  async fn fixture_channel_read_request_send_to_app(listener_type: test_mock_router::ListenerType) {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();
    let channel_id = "channel_id".to_string();

    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::Get,
      channel_non_200_status_after_count: 1,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: 0,
      listener_type,
    });

    let router_endpoint: Endpoint = format!(
      "{}://localhost:{}",
      listener_type.to_string(),
      mock_router_server.server.addr.port()
    )
    .parse()
    .unwrap();

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

    // Release the request after a few seconds
    tokio::spawn(async move {
      tokio::time::sleep(tokio::time::Duration::from_millis(500)).await;
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let app_client = create_app_client();
    let router_client = create_router_client();

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
      pool_id.into(),
      lambda_id.into(),
      channel_id,
    );
    let channel_start_result = channel.start(app_client, router_client).await;
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
      mock_router_server
        .request_count
        .load(std::sync::atomic::Ordering::SeqCst),
      2,
      "router channel requests received should be 2"
    );
    assert!(
      goaway_received.load(std::sync::atomic::Ordering::SeqCst),
      "goaway received should be true"
    );

    // Assert app server's healthcheck endpoint did not get called
    mock_app_healthcheck.assert_hits(0);

    // Assert that the test route did get called
    mock_app_bananas.assert_hits(1);
  }

  #[tokio::test]
  async fn test_channel_goaway_on_body_http() {
    fixture_channel_goaway_on_body(test_mock_router::ListenerType::Http).await;
  }

  #[tokio::test]
  async fn test_channel_goaway_on_body_https() {
    fixture_channel_goaway_on_body(test_mock_router::ListenerType::Https).await;
  }

  async fn fixture_channel_goaway_on_body(listener_type: test_mock_router::ListenerType) {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();
    let channel_id = "channel_id".to_string();

    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::GetGoAwayOnBody,
      channel_non_200_status_after_count: 1,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: 0,
      listener_type,
    });
    let router_endpoint: Endpoint = format!(
      "{}://localhost:{}",
      listener_type.to_string(),
      mock_router_server.server.addr.port()
    )
    .parse()
    .unwrap();

    // Start app server
    let mock_app_server = MockServer::start();
    let mock_app_healthcheck = mock_app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });
    let app_endpoint: Endpoint = mock_app_server.base_url().parse().unwrap();

    // Release the request after a few seconds
    tokio::spawn(async move {
      tokio::time::sleep(tokio::time::Duration::from_secs(1)).await;
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let app_client = create_app_client();
    let router_client = create_router_client();

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
      pool_id.into(),
      lambda_id.into(),
      channel_id,
    );
    let channel_start_result = channel.start(app_client, router_client).await;
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
      mock_router_server
        .request_count
        .load(std::sync::atomic::Ordering::SeqCst),
      1,
      "router channel requests received"
    );
    assert!(
      goaway_received.load(std::sync::atomic::Ordering::SeqCst),
      "goaway received should be true",
    );

    // Assert app server's healthcheck endpoint did not get called
    mock_app_healthcheck.assert_hits(0);
  }

  #[tokio::test]
  async fn test_channel_invalid_request_headers_should_continue() {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();
    let channel_id = "channel_id".to_string();

    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::GetInvalidHeaders,
      channel_non_200_status_after_count: 2,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: 0,
      listener_type: test_mock_router::ListenerType::Http,
    });
    let router_endpoint: Endpoint =
      format!("http://localhost:{}", mock_router_server.server.addr.port())
        .parse()
        .unwrap();

    // Start app server
    let mock_app_server = MockServer::start();
    let mock_app_healthcheck = mock_app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });
    let app_endpoint: Endpoint = mock_app_server.base_url().parse().unwrap();

    // Release the request after a few seconds
    tokio::spawn(async move {
      tokio::time::sleep(tokio::time::Duration::from_millis(500)).await;
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let app_client = create_app_client();
    let router_client = create_router_client();

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
      pool_id.into(),
      lambda_id.into(),
      channel_id,
    );
    let channel_start_result = channel.start(app_client, router_client).await;
    // Assert
    assert_eq!(
      channel_start_result,
      Ok(Some(ChannelResult::GoAwayReceived)),
    );
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
      mock_router_server
        .request_count
        .load(std::sync::atomic::Ordering::SeqCst),
      3,
      "router channel requests received"
    );
    assert_eq!(
      goaway_received.load(std::sync::atomic::Ordering::SeqCst),
      true,
      "goaway received should be true",
    );

    // Assert app server's healthcheck endpoint did not get called
    mock_app_healthcheck.assert_hits(0);
  }
}
