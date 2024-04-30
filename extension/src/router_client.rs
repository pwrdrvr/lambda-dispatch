use std::{sync::Arc, time::Duration};

use http_body_util::combinators::BoxBody;
use hyper::body::Bytes;
use hyper_rustls::{HttpsConnector, HttpsConnectorBuilder};
use hyper_util::{
  client::legacy::{connect::HttpConnector, Client},
  rt::{TokioExecutor, TokioTimer},
};
use tokio::{
  io::{AsyncRead, AsyncWrite},
  net::TcpStream,
};
use tokio_rustls::client::TlsStream;

use crate::cert::AcceptAnyServerCert;
use crate::prelude::*;

// Define a Stream trait that both TlsStream and TcpStream implement
pub trait Stream: AsyncRead + AsyncWrite + Send {}
impl Stream for TlsStream<TcpStream> {}
impl Stream for TcpStream {}

pub type RouterClient =
  Client<HttpsConnector<hyper_util::client::legacy::connect::HttpConnector>, BoxBody<Bytes, Error>>;

pub fn create_router_client() -> RouterClient {
  let root_cert_store = rustls::RootCertStore::empty();
  let mut config = rustls::ClientConfig::builder()
    .with_root_certificates(root_cert_store)
    .with_no_client_auth();
  // We're going to accept non-validatable certificates
  config
    .dangerous()
    .set_certificate_verifier(Arc::new(AcceptAnyServerCert));

  let mut http_connector = HttpConnector::new();
  http_connector.set_connect_timeout(Some(Duration::from_millis(500)));
  http_connector.set_nodelay(true);

  let https = HttpsConnectorBuilder::new()
    .with_tls_config(config)
    .https_or_http()
    .enable_http2()
    .wrap_connector(http_connector);

  Client::builder(TokioExecutor::new())
    .pool_idle_timeout(Duration::from_secs(5))
    .pool_max_idle_per_host(100)
    .pool_timer(TokioTimer::new())
    .retry_canceled_requests(false)
    .http2_only(true)
    .build(https)
}
