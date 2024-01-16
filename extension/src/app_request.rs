use std::pin::Pin;
use std::str::FromStr;

use hyper::body::Body;
use hyper::header::HeaderName;
use hyper::{body::Incoming, Request, Uri};
use tokio::io::{AsyncRead, AsyncWrite};
use tokio::net::TcpStream;

use tokio_rustls::client::TlsStream;

// Define a Stream trait that both TlsStream and TcpStream implement
pub trait Stream: AsyncRead + AsyncWrite + Send {}
impl Stream for TlsStream<TcpStream> {}
impl Stream for TcpStream {}

pub async fn read_until_req_headers(
  res_stream: &mut Incoming,
  lambda_id: String,
  channel_id: String,
  app_url: Uri,
) -> anyhow::Result<(hyper::http::request::Builder, bool, Vec<u8>)> {
  let mut buf = Vec::<u8>::with_capacity(32 * 1024);

  while let Some(chunk) =
    futures::future::poll_fn(|cx| Incoming::poll_frame(Pin::new(res_stream), cx)).await
  {
    let mut inc_rec_headers = [httparse::EMPTY_HEADER; 64];
    let mut req = httparse::Request::new(&mut inc_rec_headers);
    let app_url = app_url.clone();

    // Read and collect the response body
    let data = chunk?.into_data().unwrap();
    buf.extend_from_slice(&data);

    // Try to parse the headers
    match req.parse(&buf) {
      Ok(httparse::Status::Complete(offset)) => {
        // println!("Path: {}, Headers parsed: {:?}", req.path.unwrap(), req.headers);
        // println!("Got offset: {}", offset);

        if req.path.unwrap() == "/_lambda_dispatch/goaway" {
          return Ok((Request::builder(), true, Vec::<u8>::new()));
        }

        let left_over_buf = buf[offset..].to_vec();
        let app_url = format!(
          "http://{}:{}{}",
          app_url.host().unwrap(),
          app_url.port().unwrap(),
          req.path.unwrap()
        );

        let mut app_req_bld = Request::builder()
          .uri(app_url)
          .method(req.method.unwrap())
          .header("X-Lambda-Id", lambda_id.to_string())
          .header("X-Channel-Id", channel_id.to_string());

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
        println!("Partial header received, waiting for more data");
      }
      Err(e) => {
        Err(anyhow::anyhow!("Failed to parse headers: {:?}", e))?;
      }
    }
  }

  Err(anyhow::anyhow!("Failed to get a request"))
}
