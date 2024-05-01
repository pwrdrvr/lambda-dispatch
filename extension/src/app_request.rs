use std::pin::Pin;
use std::str::FromStr;

use crate::endpoint::Endpoint;
use crate::lambda_request_error::LambdaRequestError;
use hyper::body::Body;
use hyper::header::HeaderName;
use hyper::Uri;
use hyper::{body::Incoming, Request};

pub async fn read_until_req_headers(
  app_endpoint: Endpoint,
  res_stream: &mut Incoming,
  pool_id: &str,
  lambda_id: &str,
  channel_id: &str,
) -> Result<(hyper::http::request::Builder, bool, Vec<u8>), LambdaRequestError> {
  let mut buf = Vec::<u8>::with_capacity(32 * 1024);

  while let Some(chunk) =
    futures::future::poll_fn(|cx| Incoming::poll_frame(Pin::new(res_stream), cx)).await
  {
    let mut inc_rec_headers = [httparse::EMPTY_HEADER; 64];
    let mut req = httparse::Request::new(&mut inc_rec_headers);

    // Read and collect the response body
    let chunk = match chunk {
      Ok(value) => value,
      Err(e) => {
        log::error!(
          "PoolId: {}, LambdaId: {}, ChannelId: {}, - Error reading from res_stream: {:?}",
          pool_id,
          lambda_id,
          channel_id,
          e
        );
        return Err(LambdaRequestError::RouterConnectionError);
      }
    };
    let chunk_data = match chunk.data_ref() {
      Some(data) => data,
      None => {
        // Not a data frame
        continue;
      }
    };
    buf.extend_from_slice(chunk_data);

    // Try to parse the headers
    match req.parse(&buf) {
      Ok(httparse::Status::Complete(offset)) => {
        if req.path.unwrap() == "/_lambda_dispatch/goaway" {
          return Ok((Request::builder(), true, Vec::<u8>::new()));
        }

        log::debug!(
          "Path: {}, Headers parsed: {:?}",
          req.path.unwrap(),
          req.headers
        );

        let left_over_buf = buf[offset..].to_vec();

        // The app_url is only the path
        // Next.js, for one, gives a 308 redirect if you give it `http://localhost:3000/`
        // and it mangles that to `http:/localhost:3000/`
        let app_url = app_endpoint.url().join(req.path.unwrap()).unwrap();
        let app_url = Uri::from_str(app_url.as_str()).unwrap();

        let mut app_req_bld = Request::builder()
          .uri(app_url)
          .method(req.method.unwrap())
          .header("X-Pool-Id", pool_id)
          .header("X-Lambda-Id", lambda_id)
          .header("X-Channel-Id", channel_id);

        // Write the headers to the request
        let app_req_headers = app_req_bld.headers_mut().unwrap();
        for header in req.headers {
          let header_name = HeaderName::from_str(header.name).map_err(|e| {
            log::error!(
              "PoolId: {}, LambdaId: {}, ChannelId: {}, - Failed to parse header name: {}",
              pool_id,
              lambda_id,
              channel_id,
              e
            );
            LambdaRequestError::ChannelErrorOther
          })?;
          app_req_headers.insert(
            header_name,
            hyper::header::HeaderValue::from_bytes(header.value).map_err(|e| {
              log::error!(
                "PoolId: {}, LambdaId: {}, ChannelId: {}, - Failed to parse header value: {}",
                pool_id,
                lambda_id,
                channel_id,
                e,
              );
              LambdaRequestError::ChannelErrorOther
            })?,
          );
        }

        return Ok((app_req_bld, false, left_over_buf));
      }
      Ok(httparse::Status::Partial) => {
        log::debug!("Partial header received, waiting for more data");
      }
      Err(e) => {
        log::error!(
          "PoolId: {}, LambdaId: {}, ChannelId: {}, - Failed to parse request headers: {}",
          pool_id,
          lambda_id,
          channel_id,
          e
        );
        return Err(LambdaRequestError::ChannelErrorOther);
      }
    }
  }

  return Err(LambdaRequestError::RouterConnectionError);
}

#[cfg(test)]
mod tests {
  use std::time::SystemTime;

  use crate::endpoint::Endpoint;
  use crate::router_client::create_router_client;
  use crate::test_mock_router;

  use super::*;
  use axum::http::HeaderValue;
  use futures::channel::mpsc;
  use http_body_util::{BodyExt, StreamBody};
  use httpdate::fmt_http_date;
  use hyper::http::HeaderName;
  use hyper::StatusCode;
  use hyper::{
    body::{Bytes, Frame},
    Request, Uri,
  };
  use tokio_test::assert_ok;
  use url::Url;

  #[tokio::test]
  async fn test_url_join() {
    let base_url = Url::parse("http://example.com").unwrap();
    let joined_url = base_url.join("http://foo/bar").unwrap();
    assert_eq!(joined_url.as_str(), "http://foo/bar");

    let base_url = Url::parse("http://example.com:54321").unwrap();
    let joined_url = base_url.join("").unwrap();
    assert_eq!(joined_url.as_str(), "http://example.com:54321/");

    let base_url = Url::parse("http://example.com").unwrap();
    let joined_url = base_url.join("http:///foo").unwrap();
    assert_eq!(joined_url.as_str(), "http://foo/");

    let base_url = Url::parse("http://example.com").unwrap();
    // This case results in:
    //   called `Result::unwrap()` on an `Err` value: EmptyHost
    // To reproduce, load a router URL with the path `//`
    let joined_url = base_url.join("//");
    assert!(joined_url.is_err());
    assert_eq!(joined_url.err().unwrap(), url::ParseError::EmptyHost);
  }

  #[tokio::test]
  async fn test_read_until_req_headers_valid_req() {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();
    let channel_id = "channel_id".to_string();

    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::Get,
      channel_non_200_status_after_count: -1,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    // Let the router return right away
    tokio::spawn(async move {
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let channel_url: Uri = format!(
      "http://localhost:{}/api/chunked/request/{}/{}",
      mock_router_server.server.addr.port(),
      lambda_id,
      channel_id
    )
    .parse()
    .unwrap();

    // Setup our connection to the router
    let router_client = create_router_client();

    // Create the router request
    let (_tx, recv) = mpsc::channel::<crate::prelude::Result<Frame<Bytes>>>(32 * 1024);
    let boxed_body = BodyExt::boxed(StreamBody::new(recv));
    let req = Request::builder()
      .uri(&channel_url)
      .method("POST")
      .header(hyper::header::DATE, fmt_http_date(SystemTime::now()))
      .header(hyper::header::HOST, channel_url.host().unwrap())
      // The content-type that we're sending to the router is opaque
      // as it contains another HTTP request/response, so may start as text
      // with request/headers and then be binary after that - it should not be parsed
      // by anything other than us
      .header(hyper::header::CONTENT_TYPE, "application/octet-stream")
      .header("X-Pool-Id", pool_id.to_string())
      .header("X-Lambda-Id", &lambda_id)
      .header("X-Channel-Id", &channel_id)
      .body(boxed_body)
      .unwrap();

    let res = router_client.request(req).await.unwrap();
    let (parts, mut res_stream) = res.into_parts();

    let app_endpoint = "http://localhost:3000".parse::<Endpoint>().unwrap();

    // Act
    let result = read_until_req_headers(
      app_endpoint,
      &mut res_stream,
      &pool_id,
      &lambda_id,
      &channel_id,
    )
    .await;

    // Assert
    assert_eq!(
      parts.headers.get(HeaderName::from_static("content-type")),
      Some(&hyper::http::HeaderValue::from_static(
        "application/octet-stream"
      ))
    );
    assert_ok!(&result);
    let (app_req_builder, goaway, left_over_buf) = result.unwrap();
    let host_header = app_req_builder.headers_ref().unwrap().get("host");
    assert_eq!(host_header, Some(&HeaderValue::from_static("localhost")));
    let test_header = app_req_builder.headers_ref().unwrap().get("test-header");
    assert_eq!(test_header, Some(&HeaderValue::from_static("foo")));
    let app_req_uri = app_req_builder.uri_ref().unwrap();
    assert_eq!(
      app_req_uri,
      &Uri::from_static("http://localhost:3000/bananas")
    );
    assert_eq!(goaway, false);
    assert_eq!(left_over_buf.is_empty(), false);
    assert_eq!(left_over_buf, b"HELLO WORLD");
    assert_eq!(
      mock_router_server
        .request_count
        .load(std::sync::atomic::Ordering::SeqCst),
      1
    );
  }

  #[tokio::test]
  async fn test_read_until_req_headers_no_host_header() {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();
    let channel_id = "channel_id".to_string();

    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::GetNoHost,
      channel_non_200_status_after_count: 1,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    // Let the router return right away
    tokio::spawn(async move {
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let channel_url: Uri = format!(
      "http://localhost:{}/api/chunked/request/{}/{}",
      mock_router_server.server.addr.port(),
      lambda_id,
      channel_id
    )
    .parse()
    .unwrap();

    // Setup our connection to the router
    let router_client = create_router_client();

    // Create the router request
    let (_tx, recv) = mpsc::channel::<crate::prelude::Result<Frame<Bytes>>>(32 * 1024);
    let boxed_body = BodyExt::boxed(StreamBody::new(recv));
    let req = Request::builder()
      .uri(&channel_url)
      .method("POST")
      .header(hyper::header::DATE, fmt_http_date(SystemTime::now()))
      .header(hyper::header::HOST, channel_url.host().unwrap())
      // The content-type that we're sending to the router is opaque
      // as it contains another HTTP request/response, so may start as text
      // with request/headers and then be binary after that - it should not be parsed
      // by anything other than us
      .header(hyper::header::CONTENT_TYPE, "application/octet-stream")
      .header("X-Pool-Id", pool_id.to_string())
      .header("X-Lambda-Id", &lambda_id)
      .header("X-Channel-Id", &channel_id)
      .body(boxed_body)
      .unwrap();

    let res = router_client.request(req).await.unwrap();
    let (parts, mut res_stream) = res.into_parts();

    let app_endpoint = "http://localhost:3000".parse::<Endpoint>().unwrap();

    // Act
    let result = read_until_req_headers(
      app_endpoint,
      &mut res_stream,
      &pool_id,
      &lambda_id,
      &channel_id,
    )
    .await;

    // Assert
    assert_eq!(
      parts.headers.get(HeaderName::from_static("content-type")),
      Some(&hyper::http::HeaderValue::from_static(
        "application/octet-stream"
      ))
    );
    assert_ok!(&result);
    let (app_req_builder, goaway, left_over_buf) = result.unwrap();
    let host_header = app_req_builder.headers_ref().unwrap().get("host");
    assert_eq!(host_header, None);
    let test_header = app_req_builder.headers_ref().unwrap().get("test-header");
    assert_eq!(test_header, Some(&HeaderValue::from_static("foo")));
    let app_req_uri = app_req_builder.uri_ref().unwrap();
    assert_eq!(
      app_req_uri,
      &Uri::from_static("http://localhost:3000/bananas/no_host_header")
    );
    assert_eq!(goaway, false);
    assert_eq!(left_over_buf.is_empty(), true);
    assert_eq!(
      mock_router_server
        .request_count
        .load(std::sync::atomic::Ordering::SeqCst),
      1
    );
  }

  #[tokio::test]
  async fn test_read_until_req_headers_go_away_path() {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();
    let channel_id = "channel_id".to_string();

    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::GetGoAwayOnBody,
      channel_non_200_status_after_count: -1,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    // Release the response before we make the request
    // This way we get all the bytes in one chunk
    tokio::spawn(async move {
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let channel_url: Uri = format!(
      "http://localhost:{}/api/chunked/request/{}/{}",
      mock_router_server.server.addr.port(),
      lambda_id,
      channel_id
    )
    .parse()
    .unwrap();

    let router_client = create_router_client();

    // Create the router request
    let (_tx, recv) = mpsc::channel::<crate::prelude::Result<Frame<Bytes>>>(32 * 1024);
    let boxed_body = BodyExt::boxed(StreamBody::new(recv));
    let req = Request::builder()
      .uri(&channel_url)
      .method("POST")
      .header(hyper::header::DATE, fmt_http_date(SystemTime::now()))
      .header(hyper::header::HOST, channel_url.host().unwrap())
      // The content-type that we're sending to the router is opaque
      // as it contains another HTTP request/response, so may start as text
      // with request/headers and then be binary after that - it should not be parsed
      // by anything other than us
      .header(hyper::header::CONTENT_TYPE, "application/octet-stream")
      .header("X-Pool-Id", pool_id.to_string())
      .header("X-Lambda-Id", &lambda_id)
      .header("X-Channel-Id", &channel_id)
      .body(boxed_body)
      .unwrap();

    let res = router_client.request(req).await.unwrap();
    let (parts, mut res_stream) = res.into_parts();

    let app_endpoint = "http://localhost:3000".parse::<Endpoint>().unwrap();

    // Act
    let result = read_until_req_headers(
      app_endpoint,
      &mut res_stream,
      &pool_id,
      &lambda_id,
      &channel_id,
    )
    .await;

    // Assert
    assert_eq!(
      parts.headers.get(HeaderName::from_static("content-type")),
      Some(&hyper::http::HeaderValue::from_static(
        "application/octet-stream"
      ))
    );
    assert_ok!(&result);
    let (app_req_builder, goaway, left_over_buf) = result.unwrap();
    let host_header = app_req_builder.headers_ref().unwrap().get("host");
    assert_eq!(host_header, None);
    let test_header = app_req_builder.headers_ref().unwrap().get("test-header");
    assert_eq!(test_header, None);
    let app_req_uri = app_req_builder.uri_ref().unwrap();
    assert_eq!(app_req_uri, &Uri::from_static("/"));
    assert_eq!(goaway, true);
    assert_eq!(left_over_buf.is_empty(), true);
    assert_eq!(
      mock_router_server
        .request_count
        .load(std::sync::atomic::Ordering::SeqCst),
      1
    );
  }

  #[tokio::test]
  async fn test_read_until_req_headers_connection_closed() {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();
    let channel_id = "channel_id".to_string();

    // Start router server
    let mock_router_server = test_mock_router::setup_router(test_mock_router::RouterParams {
      request_method: test_mock_router::RequestMethod::ShutdownWithoutResponse,
      channel_non_200_status_after_count: -1,
      channel_non_200_status_code: StatusCode::CONFLICT,
      channel_panic_response_from_extension_on_count: -1,
      channel_panic_request_to_extension_before_start_on_count: -1,
      channel_panic_request_to_extension_after_start: false,
      channel_panic_request_to_extension_before_close: false,
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    let channel_url: Uri = format!(
      "http://localhost:{}/api/chunked/request/{}/{}",
      mock_router_server.server.addr.port(),
      lambda_id,
      channel_id
    )
    .parse()
    .unwrap();

    let router_client = create_router_client();

    // Create the router request
    let (_tx, recv) = mpsc::channel::<crate::prelude::Result<Frame<Bytes>>>(32 * 1024);
    let boxed_body = BodyExt::boxed(StreamBody::new(recv));
    let req = Request::builder()
      .uri(&channel_url)
      .method("POST")
      .header(hyper::header::DATE, fmt_http_date(SystemTime::now()))
      .header(hyper::header::HOST, channel_url.host().unwrap())
      // The content-type that we're sending to the router is opaque
      // as it contains another HTTP request/response, so may start as text
      // with request/headers and then be binary after that - it should not be parsed
      // by anything other than us
      .header(hyper::header::CONTENT_TYPE, "application/octet-stream")
      .header("X-Pool-Id", pool_id.to_string())
      .header("X-Lambda-Id", &lambda_id)
      .header("X-Channel-Id", &channel_id)
      .body(boxed_body)
      .unwrap();

    let res = router_client.request(req).await.unwrap();
    let (parts, mut res_stream) = res.into_parts();

    // Blow up the mock router server
    // Release the request after a few seconds
    tokio::spawn(async move {
      tokio::time::sleep(tokio::time::Duration::from_secs(1)).await;
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });
    let app_endpoint = "http://localhost:3000".parse::<Endpoint>().unwrap();

    // Act
    let result = read_until_req_headers(
      app_endpoint,
      &mut res_stream,
      &pool_id,
      &lambda_id,
      &channel_id,
    )
    .await;

    // Assert
    assert_eq!(
      parts.headers.get(HeaderName::from_static("content-type")),
      Some(&hyper::http::HeaderValue::from_static(
        "application/octet-stream"
      ))
    );
    assert!(result.is_err());
    assert_eq!(
      result.err().unwrap(),
      LambdaRequestError::RouterConnectionError
    );
    assert_eq!(
      mock_router_server
        .request_count
        .load(std::sync::atomic::Ordering::SeqCst),
      1
    );
  }

  #[tokio::test]
  async fn test_read_until_req_headers_partial_reads() {
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
      ping_panic_after_count: -1,
      listener_type: test_mock_router::ListenerType::Http,
    });

    let channel_url: Uri = format!(
      "http://localhost:{}/api/chunked/request/{}/{}",
      mock_router_server.server.addr.port(),
      lambda_id,
      channel_id
    )
    .parse()
    .unwrap();

    let router_client = create_router_client();

    // Create the router request
    let (_tx, recv) = mpsc::channel::<crate::prelude::Result<Frame<Bytes>>>(32 * 1024);
    let boxed_body = BodyExt::boxed(StreamBody::new(recv));
    let req = Request::builder()
      .uri(&channel_url)
      .method("POST")
      .header(hyper::header::DATE, fmt_http_date(SystemTime::now()))
      .header(hyper::header::HOST, channel_url.host().unwrap())
      // The content-type that we're sending to the router is opaque
      // as it contains another HTTP request/response, so may start as text
      // with request/headers and then be binary after that - it should not be parsed
      // by anything other than us
      .header(hyper::header::CONTENT_TYPE, "application/octet-stream")
      .header("X-Pool-Id", pool_id.to_string())
      .header("X-Lambda-Id", &lambda_id)
      .header("X-Channel-Id", &channel_id)
      .body(boxed_body)
      .unwrap();

    let res = router_client.request(req).await.unwrap();
    let (parts, mut res_stream) = res.into_parts();

    // Release the request after a few seconds
    // This will pause the headers, then finish them with the body too
    tokio::spawn(async move {
      tokio::time::sleep(tokio::time::Duration::from_secs(1)).await;
      mock_router_server
        .release_request_tx
        .send(())
        .await
        .unwrap();
    });

    let app_endpoint = "http://localhost:3000".parse::<Endpoint>().unwrap();

    // Act
    let result = read_until_req_headers(
      app_endpoint,
      &mut res_stream,
      &pool_id,
      &lambda_id,
      &channel_id,
    )
    .await;

    // Assert
    assert_eq!(
      parts.headers.get(HeaderName::from_static("content-type")),
      Some(&hyper::http::HeaderValue::from_static(
        "application/octet-stream"
      ))
    );
    assert_ok!(&result);
    let (app_req_builder, goaway, left_over_buf) = result.unwrap();
    let host_header = app_req_builder.headers_ref().unwrap().get("host");
    assert_eq!(host_header, Some(&HeaderValue::from_static("localhost")));
    let test_header = app_req_builder.headers_ref().unwrap().get("test-header");
    assert_eq!(test_header, Some(&HeaderValue::from_static("foo")));
    let test_headers = app_req_builder.headers_ref().unwrap().get("test-headers");
    assert_eq!(test_headers, Some(&HeaderValue::from_static("bar")));
    let test_headerss = app_req_builder.headers_ref().unwrap().get("test-headerss");
    assert_eq!(test_headerss, Some(&HeaderValue::from_static("baz")));
    let app_req_uri = app_req_builder.uri_ref().unwrap();
    assert_eq!(
      app_req_uri,
      &Uri::from_static("http://localhost:3000/bananas")
    );
    assert_eq!(goaway, false);
    assert_eq!(left_over_buf.is_empty(), false);
    assert_eq!(left_over_buf, b"HELLO WORLD");
    assert_eq!(
      mock_router_server
        .request_count
        .load(std::sync::atomic::Ordering::SeqCst),
      1,
      "request count"
    );
  }
}
