use axum::Router;
use rustls::ClientConfig;
use rustls_pki_types::ServerName;
use std::sync::Arc;
use tokio::net::TcpStream;
use tokio_rustls::TlsConnector;

mod support;
mod utils;
use support::http2_server::run_http2_tls_app;

use extension::cert::*;

// This does actually work but it's really CPU intensive
// fn generate_cert_and_key() -> (
//   Vec<CertificateDer<'static>>,
//   rustls_pki_types::PrivateKeyDer<'static>,
// ) {
// use rand::rngs::OsRng;
// use rcgen::{date_time_ymd, CertificateParams, DistinguishedName, SanType};
// use rsa::{pkcs8::EncodePrivateKey, RsaPrivateKey};
//   // Generate a self-signed RSA certificate
//   let mut params: CertificateParams = Default::default();
//   params.not_before = date_time_ymd(2021, 5, 19);
//   params.not_after = date_time_ymd(4096, 1, 1);
//   params.distinguished_name = DistinguishedName::new();
//   params.subject_alt_names = vec![SanType::DnsName("localhost".try_into().unwrap())];

//   let mut rng = OsRng;
//   let bits = 2048;
//   let private_key = RsaPrivateKey::new(&mut rng, bits).unwrap();
//   let private_key_der = private_key.to_pkcs8_der().unwrap();
//   let key_pair = rcgen::KeyPair::try_from(private_key_der.as_bytes()).unwrap();

//   let cert = params.self_signed(&key_pair).unwrap();
//   let certs = vec![cert.der().clone()];

//   let rsa_key_parsed = PrivatePkcs8KeyDer::from(private_key_der.as_bytes().to_vec());

//   (
//     certs,
//     rustls_pki_types::PrivateKeyDer::Pkcs8(rsa_key_parsed),
//   )
// }

#[tokio::test]
async fn test_accept_any_server_cert() {
  let root_cert_store = rustls::RootCertStore::empty();
  let mut config = ClientConfig::builder()
    .with_root_certificates(root_cert_store)
    .with_no_client_auth();
  config
    .dangerous()
    .set_certificate_verifier(Arc::new(AcceptAnyServerCert));

  let app = Router::new();
  let server = run_http2_tls_app(app);

  log::info!("TLS server running on port: {}", server.addr.port());

  let connector = TlsConnector::from(Arc::new(config));
  let stream = TcpStream::connect(server.addr).await.unwrap();
  let domain = ServerName::try_from("localhost").unwrap();
  let tls_stream = connector.connect(domain, stream).await;

  // Make sure we connected
  assert!(tls_stream.is_ok());
}

#[tokio::test]
async fn test_accept_fails_without_any_server_cert() {
  let root_cert_store = rustls::RootCertStore::empty();
  let config = ClientConfig::builder()
    .with_root_certificates(root_cert_store)
    .with_no_client_auth();

  let app = Router::new();
  let server = run_http2_tls_app(app);

  log::info!("TLS server running on port: {}", server.addr.port());

  let connector = TlsConnector::from(Arc::new(config));
  let stream = TcpStream::connect(server.addr).await.unwrap();
  let domain = ServerName::try_from("localhost").unwrap();
  let tls_stream = connector.connect(domain, stream).await;

  // Make sure we failed to connect
  assert!(tls_stream.is_err());
}

#[test]
fn test_accept_any_server_cert_debug() {
  let verifier = AcceptAnyServerCert;

  assert_eq!(format!("{:?}", verifier), "AcceptAnyServerCert");
}
