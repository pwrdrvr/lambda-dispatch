use std::pin::Pin;
use std::str::FromStr;

use crate::endpoint::Endpoint;
use crate::prelude::*;
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
) -> Result<(hyper::http::request::Builder, bool, Vec<u8>)> {
  let mut buf = Vec::<u8>::with_capacity(32 * 1024);

  while let Some(chunk) =
    futures::future::poll_fn(|cx| Incoming::poll_frame(Pin::new(res_stream), cx)).await
  {
    let mut inc_rec_headers = [httparse::EMPTY_HEADER; 64];
    let mut req = httparse::Request::new(&mut inc_rec_headers);

    // Read and collect the response body
    let data = chunk?.into_data().unwrap();
    buf.extend_from_slice(&data);

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
          let header_name = HeaderName::from_str(header.name)?;
          app_req_headers.insert(
            header_name,
            hyper::header::HeaderValue::from_bytes(header.value).unwrap(),
          );
        }

        return Ok((app_req_bld, false, left_over_buf));
      }
      Ok(httparse::Status::Partial) => {
        log::debug!("Partial header received, waiting for more data");
      }
      Err(e) => {
        Err(anyhow::anyhow!("Failed to parse headers: {:?}", e))?;
      }
    }
  }

  Err(anyhow::anyhow!("Failed to get a request"))
}

#[cfg(test)]
mod tests {
  use std::{sync::Arc, time::SystemTime};

  use crate::connect_to_router;
  use crate::{endpoint::Endpoint, test_http2_server::test_http2_server::run_http2_app};

  use super::*;
  use axum::http::HeaderValue;
  use axum::response::Response;
  use axum::{extract::Path, routing::post, Router};
  use axum_extra::body::AsyncReadBody;
  use futures::channel::mpsc;
  use http_body_util::{BodyExt, StreamBody};
  use httpdate::fmt_http_date;
  use hyper::http::HeaderName;
  use hyper::StatusCode;
  use hyper::{
    body::{Bytes, Frame},
    Request, Uri,
  };
  use tokio::io::AsyncWriteExt;
  use tokio_test::{assert_err, assert_ok};

  #[tokio::test]
  async fn test_read_until_req_headers_valid_req() {
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
        move |Path((_lambda_id, _channel_id)): Path<(String, String)>| {
          let request_count = Arc::clone(&request_count_clone);
          async move {
            // Increment the request count
            request_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);

            if request_count.load(std::sync::atomic::Ordering::SeqCst) == 1 {
              let req =
                b"GET /bananas HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n\r\nHELLO WORLD";

              (StatusCode::OK, &req[..])
            } else {
              let error_message = b"Request already made";
              (StatusCode::CONFLICT, &error_message[..])
            }
          }
        },
      ),
    );

    let mock_router_server = run_http2_app(app);

    let channel_url: Uri = format!(
      "http://localhost:{}/api/chunked/request/{}/{}",
      mock_router_server.addr.port(),
      lambda_id,
      channel_id
    )
    .parse()
    .unwrap();

    let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
      .parse()
      .unwrap();

    let pool_id_arc: PoolId = pool_id.clone().into();
    let lambda_id_arc: LambdaId = lambda_id.clone().into();

    // Setup our connection to the router
    let mut sender = connect_to_router::connect_to_router(
      router_endpoint.clone(),
      Arc::clone(&pool_id_arc),
      Arc::clone(&lambda_id_arc),
    )
    .await
    .unwrap();

    // Create the router request
    let (_tx, recv) = mpsc::channel::<Result<Frame<Bytes>>>(32 * 1024);
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

    //
    // Make the request to the router
    //
    sender
      .ready()
      .await
      .context("PoolId: {}, LambdaId: {}, ChannelId: {} - Router connection ready check threw error - connection has disconnected, should reconnect").unwrap();

    let res = sender.send_request(req).await.unwrap();
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
    assert_eq!(request_count.load(std::sync::atomic::Ordering::SeqCst), 1);
  }

  #[tokio::test]
  async fn test_read_until_req_headers_go_away_path() {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();
    let channel_id = "channel_id".to_string();

    // Start router server
    // Use an arc int to count how many times the request endpoint was called
    let request_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let request_count_clone = Arc::clone(&request_count);
    let app = Router::new()
      .route(
        "/api/chunked/request/:lambda_id/:channel_id",
        post(
          move |Path((_lambda_id, _channel_id)): Path<(String, String)>| {
            let request_count = Arc::clone(&request_count_clone);
            async move {
              // Increment the request count
              request_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);

              if request_count.load(std::sync::atomic::Ordering::SeqCst) == 1 {
                let req =
                  b"GET /_lambda_dispatch/goaway HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n\r\nHELLO WORLD";

                (StatusCode::OK, &req[..])
              } else {
                let error_message = b"Request already made";
                (StatusCode::CONFLICT, &error_message[..])
              }
            }
          },
        ),

      );

    let mock_router_server = run_http2_app(app);

    let channel_url: Uri = format!(
      "http://localhost:{}/api/chunked/request/{}/{}",
      mock_router_server.addr.port(),
      lambda_id,
      channel_id
    )
    .parse()
    .unwrap();

    let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
      .parse()
      .unwrap();

    let pool_id_arc: PoolId = pool_id.clone().into();
    let lambda_id_arc: LambdaId = lambda_id.clone().into();

    // Setup our connection to the router
    let mut sender = connect_to_router::connect_to_router(
      router_endpoint.clone(),
      Arc::clone(&pool_id_arc),
      Arc::clone(&lambda_id_arc),
    )
    .await
    .unwrap();

    // Create the router request
    let (_tx, recv) = mpsc::channel::<Result<Frame<Bytes>>>(32 * 1024);
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

    //
    // Make the request to the router
    //
    sender
      .ready()
      .await
      .context("PoolId: {}, LambdaId: {}, ChannelId: {} - Router connection ready check threw error - connection has disconnected, should reconnect").unwrap();

    let res = sender.send_request(req).await.unwrap();
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
    assert_eq!(request_count.load(std::sync::atomic::Ordering::SeqCst), 1);
  }

  #[tokio::test]
  async fn test_read_until_req_headers_connection_closed() {
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

            // Create a channel for the stream
            let (mut tx, rx) = tokio::io::duplex(65_536);

            // Spawn a task to write to the stream
            tokio::spawn(async move {
              // Wait for the release
              release_request_rx.lock().await.recv().await.unwrap();

              // Close the stream
              tx.shutdown().await.unwrap();
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

    let channel_url: Uri = format!(
      "http://localhost:{}/api/chunked/request/{}/{}",
      mock_router_server.addr.port(),
      lambda_id,
      channel_id
    )
    .parse()
    .unwrap();

    let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
      .parse()
      .unwrap();

    let pool_id_arc: PoolId = pool_id.clone().into();
    let lambda_id_arc: LambdaId = lambda_id.clone().into();

    // Setup our connection to the router
    let mut sender = connect_to_router::connect_to_router(
      router_endpoint.clone(),
      Arc::clone(&pool_id_arc),
      Arc::clone(&lambda_id_arc),
    )
    .await
    .unwrap();

    // Create the router request
    let (_tx, recv) = mpsc::channel::<Result<Frame<Bytes>>>(32 * 1024);
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

    // Make the request to the router
    sender
      .ready()
      .await
      .context("PoolId: {}, LambdaId: {}, ChannelId: {} - Router connection ready check threw error - connection has disconnected, should reconnect").unwrap();

    let res = sender.send_request(req).await.unwrap();
    let (parts, mut res_stream) = res.into_parts();

    // Blow up the mock router server
    // Release the request after a few seconds
    tokio::spawn(async move {
      tokio::time::sleep(tokio::time::Duration::from_secs(1)).await;
      release_request_tx.send(()).await.unwrap();
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
    assert_err!(&result);
    assert_eq!(request_count.load(std::sync::atomic::Ordering::SeqCst), 1);
  }

  #[tokio::test]
  async fn test_read_until_req_headers_partial_reads() {
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

            // Create a channel for the stream
            let (mut tx, rx) = tokio::io::duplex(65_536);

            // Spawn a task to write to the stream
            tokio::spawn(async move {
              // Write some of the headers
              let data = b"GET /bananas HTTP/1.1\r\nHost: localhost\r\nTest-Header: foo\r\n";
              tx.write_all(data).await.unwrap();

              // Wait for the release
              release_request_rx.lock().await.recv().await.unwrap();

              // Write the rest of the headers
              let data = b"Test-Headers: bar\r\nTest-Headerss: baz\r\n\r\nHELLO WORLD";
              tx.write_all(data).await.unwrap();

              // Close the stream
              tx.shutdown().await.unwrap();
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

    let channel_url: Uri = format!(
      "http://localhost:{}/api/chunked/request/{}/{}",
      mock_router_server.addr.port(),
      lambda_id,
      channel_id
    )
    .parse()
    .unwrap();

    let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
      .parse()
      .unwrap();

    let pool_id_arc: PoolId = pool_id.clone().into();
    let lambda_id_arc: LambdaId = lambda_id.clone().into();

    // Setup our connection to the router
    let mut sender = connect_to_router::connect_to_router(
      router_endpoint.clone(),
      Arc::clone(&pool_id_arc),
      Arc::clone(&lambda_id_arc),
    )
    .await
    .unwrap();

    // Create the router request
    let (_tx, recv) = mpsc::channel::<Result<Frame<Bytes>>>(32 * 1024);
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

    // Make the request to the router
    sender
      .ready()
      .await
      .context("PoolId: {}, LambdaId: {}, ChannelId: {} - Router connection ready check threw error - connection has disconnected, should reconnect").unwrap();

    let res = sender.send_request(req).await.unwrap();
    let (parts, mut res_stream) = res.into_parts();

    // Release the request after a few seconds
    tokio::spawn(async move {
      tokio::time::sleep(tokio::time::Duration::from_secs(1)).await;
      release_request_tx.send(()).await.unwrap();
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
      request_count.load(std::sync::atomic::Ordering::SeqCst),
      1,
      "request count"
    );
  }
}
