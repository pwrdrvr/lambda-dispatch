use crate::prelude::*;

use std::time::Duration;

use bytes::Bytes;
use futures::channel::mpsc::Receiver;
use http_body_util::StreamBody;
use hyper::body::Frame;
use hyper_util::client::legacy::connect::HttpConnector;
use hyper_util::client::legacy::Client;
use hyper_util::rt::{TokioExecutor, TokioTimer};
use tokio::{
  io::{AsyncRead, AsyncWrite},
  net::TcpStream,
};
use tokio_rustls::client::TlsStream;

// Define a Stream trait that both TlsStream and TcpStream implement
pub trait Stream: AsyncRead + AsyncWrite + Send {}
impl Stream for TlsStream<TcpStream> {}
impl Stream for TcpStream {}

pub type AppClient = Client<HttpConnector, StreamBody<Receiver<Result<Frame<Bytes>>>>>;

pub fn create_app_client() -> AppClient {
  let mut http_connector = HttpConnector::new();
  http_connector.set_connect_timeout(Some(Duration::from_millis(500)));
  http_connector.set_nodelay(true);
  Client::builder(TokioExecutor::new())
    .pool_idle_timeout(Duration::from_secs(5))
    .pool_max_idle_per_host(100)
    .pool_timer(TokioTimer::new())
    .retry_canceled_requests(false)
    .build(http_connector)
}
