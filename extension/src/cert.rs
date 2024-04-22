/// A server certificate verifier that accepts any certificate.
#[derive(Debug)]
pub struct AcceptAnyServerCert;

impl rustls::client::danger::ServerCertVerifier for AcceptAnyServerCert {
  fn verify_server_cert(
    &self,
    _end_entity: &rustls_pki_types::CertificateDer<'_>,
    _intermediates: &[rustls_pki_types::CertificateDer<'_>],
    _server_name: &rustls_pki_types::ServerName<'_>,
    _ocsp_response: &[u8],
    _now: rustls_pki_types::UnixTime,
  ) -> Result<rustls::client::danger::ServerCertVerified, rustls::Error> {
    Ok(rustls::client::danger::ServerCertVerified::assertion())
  }

  fn verify_tls12_signature(
    &self,
    _message: &[u8],
    _cert: &rustls_pki_types::CertificateDer<'_>,
    _dss: &rustls::DigitallySignedStruct,
  ) -> Result<rustls::client::danger::HandshakeSignatureValid, rustls::Error> {
    Ok(rustls::client::danger::HandshakeSignatureValid::assertion())
  }

  fn verify_tls13_signature(
    &self,
    _message: &[u8],
    _cert: &rustls_pki_types::CertificateDer<'_>,
    _dss: &rustls::DigitallySignedStruct,
  ) -> Result<rustls::client::danger::HandshakeSignatureValid, rustls::Error> {
    Ok(rustls::client::danger::HandshakeSignatureValid::assertion())
  }

  fn supported_verify_schemes(&self) -> Vec<rustls::SignatureScheme> {
    rustls::crypto::ring::default_provider()
      .signature_verification_algorithms
      .supported_schemes()
  }
}

#[cfg(test)]
mod tests {
  use super::*;

  use axum::Router;
  use hyper::{body::Incoming, Request};
  use hyper_util::rt::TokioIo;
  use rustls::{ClientConfig, ServerConfig};
  use rustls_pemfile::{read_one, Item};
  use rustls_pki_types::{CertificateDer, PrivatePkcs8KeyDer, ServerName};
  use std::fs::File;
  use std::io::BufReader;
  use std::iter;
  use std::{
    net::SocketAddr,
    sync::{mpsc, Arc},
    thread,
  };
  use tokio::{
    net::{TcpListener, TcpStream},
    sync::oneshot,
  };
  use tokio_rustls::{TlsAcceptor, TlsConnector};
  use tower::Service;

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

  fn load_cert_and_key() -> Result<
    (
      Vec<CertificateDer<'static>>,
      rustls_pki_types::PrivateKeyDer<'static>,
    ),
    Box<dyn std::error::Error>,
  > {
    // Load the certificate from the PEM file
    let cert_file = File::open("./test/fakecert/cert.pem")?;
    let mut cert_reader = BufReader::new(cert_file);
    let mut certs = Vec::new();

    for item in iter::from_fn(|| read_one(&mut cert_reader).transpose()) {
      if let Ok(Item::X509Certificate(cert)) = item {
        certs.push(CertificateDer::from(cert));
      }
    }

    // Load the private key from the PEM file
    let key_file = File::open("./test/fakecert/key.pem")?;
    let mut key_reader = BufReader::new(key_file);
    let mut key_der = None;

    for item in iter::from_fn(|| read_one(&mut key_reader).transpose()) {
      if let Ok(Item::Pkcs8Key(key)) = item {
        key_der = Some(rustls_pki_types::PrivateKeyDer::Pkcs8(
          PrivatePkcs8KeyDer::from(key),
        ));
        break;
      }
    }

    match key_der {
      Some(key) => Ok((certs, key)),
      None => Err("Private key not found in PEM file".into()),
    }
  }

  fn run_tls_server(app: Router) -> Serve {
    // let (certs, rsa_key_parsed) = generate_cert_and_key();
    let (certs, rsa_key_parsed) = load_cert_and_key().unwrap();

    // Configure TLS
    let config = ServerConfig::builder()
      .with_no_client_auth()
      .with_single_cert(certs, rsa_key_parsed)
      .expect("failed to configure TLS");
    let acceptor = TlsAcceptor::from(Arc::new(config));

    let (addr_tx, addr_rx) = mpsc::channel();
    let (shutdown_tx, mut shutdown_rx) = oneshot::channel();

    let thread_name = format!(
      "test-server-{}",
      thread::current()
        .name()
        .unwrap_or("<unknown test case name>")
    );
    let thread = thread::Builder::new()
      .name(thread_name)
      .spawn(move || {
        tokio::runtime::Builder::new_current_thread()
          .enable_all()
          .build()
          .expect("new rt")
          .block_on(async move {
            // Create a TCP listener
            let listener = TcpListener::bind(("127.0.0.1", 0)).await.unwrap();

            addr_tx
              .send(listener.local_addr().unwrap())
              .expect("server addr tx");

            // Accept incoming connections and serve them
            loop {
              tokio::select! {
                res = listener.accept() => {
                  let (socket, _) = res.unwrap();
                  let secure_socket = acceptor.accept(socket).await.unwrap();

                  let tower_service = app.clone();

                  tokio::spawn(async move {
                    let socket = TokioIo::new(secure_socket);

                    let hyper_service =
                      hyper::service::service_fn(move |request: Request<Incoming>| {
                        tower_service.clone().call(request)
                      });

                    if let Err(err) = hyper::server::conn::http1::Builder::new()
                      .serve_connection(socket, hyper_service)
                      .await
                    {
                      log::error!("failed to serve connection: {:#}", err);
                    }
                  });
                }
                _ = &mut shutdown_rx => {
                  break;
                }
              }
            }

            log::info!("Server shutting down");
          })
      })
      .expect("thread spawn");

    Serve {
      addr: addr_rx.recv().expect("server addr rx"),
      shutdown_signal: Some(shutdown_tx),
      thread: Some(thread),
    }
  }

  struct Serve {
    addr: SocketAddr,
    shutdown_signal: Option<oneshot::Sender<()>>,
    thread: Option<thread::JoinHandle<()>>,
  }

  // Exit the server thread when the `Serve` instance is dropped.
  impl Drop for Serve {
    fn drop(&mut self) {
      drop(self.shutdown_signal.take());
      drop(self.thread.take());
    }
  }

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
    let server = run_tls_server(app);

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
    let server = run_tls_server(app);

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
}
