use std::pin::Pin;
use std::str::FromStr;

use crate::endpoint::Endpoint;
use crate::lambda_request_error::LambdaRequestError;
use hyper::{
  body::{Body, Incoming},
  header::HeaderName,
  Request, Uri,
};

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
        let app_url = format!("{}{}", app_endpoint, req.path.unwrap());
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

  Err(LambdaRequestError::RouterConnectionError)
}
