#[cfg(test)]
pub mod test_http2_server {
  use std::sync::Arc;
  use std::{net::SocketAddr, sync::mpsc, thread};

  use axum::Router;
  use hyper::{body::Incoming, Request};
  use hyper_util::rt::TokioExecutor;
  use hyper_util::rt::TokioIo;
  use rustls::ServerConfig;
  use rustls_pemfile::{read_one, Item};
  use rustls_pki_types::CertificateDer;
  use rustls_pki_types::PrivatePkcs8KeyDer;
  use std::fs::File;
  use std::io::BufReader;
  use std::iter;
  use tokio::net::TcpListener;
  use tokio::sync::oneshot;
  use tokio_rustls::TlsAcceptor;
  use tower::Service;

  async fn start_mock_server() -> TcpListener {
    TcpListener::bind(("127.0.0.1", 0)).await.unwrap()
  }

  pub struct Serve {
    pub addr: SocketAddr,
    pub shutdown_signal: Option<oneshot::Sender<()>>,
    pub thread: Option<thread::JoinHandle<()>>,
  }

  // Exit the server thread when the `Serve` instance is dropped.
  impl Drop for Serve {
    fn drop(&mut self) {
      drop(self.shutdown_signal.take());
      drop(self.thread.take());
    }
  }

  pub fn load_cert_and_key() -> Result<
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

  pub fn run_http2_tls_app(app: Router) -> Serve {
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

                      if let Err(err) = hyper::server::conn::http2::Builder::new(TokioExecutor::new())
                      // `serve_connection_with_upgrades` is required for websockets. If you don't need
                      // that you can use `serve_connection` instead.
                      .serve_connection(socket, hyper_service)
                      .await
                    {
                      println!("failed to serve connection: {:#}", err);
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

  pub fn run_http2_app(app: Router) -> Serve {
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
            let listener = start_mock_server().await;
            addr_tx
              .send(listener.local_addr().unwrap())
              .expect("server addr tx");

            loop {
              // In this example we discard the remote address. See `fn serve_with_connect_info` for how
              // to expose that.
              tokio::select! {
                res = listener.accept() => {
                  let (socket, _remote_addr) = res.unwrap();

                  // We don't need to call `poll_ready` because `Router` is always ready.
                  let tower_service = app.clone();

                  // Spawn a task to handle the connection. That way we can multiple connections
                  // concurrently.
                  tokio::spawn(async move {
                    // Hyper has its own `AsyncRead` and `AsyncWrite` traits and doesn't use tokio.
                    // `TokioIo` converts between them.
                    let socket = TokioIo::new(socket);

                    // Hyper also has its own `Service` trait and doesn't use tower. We can use
                    // `hyper::service::service_fn` to create a hyper `Service` that calls our app through
                    // `tower::Service::call`
                    let hyper_service =
                      hyper::service::service_fn(move |request: Request<Incoming>| {
                        // We have to clone `tower_service` because hyper's `Service` uses `&self` whereas
                        // tower's `Service` requires `&mut self`.
                        //
                        // We don't need to call `poll_ready` since `Router` is always ready.
                        tower_service.clone().call(request)
                      });

                    // `TokioExecutor` tells hyper to use `tokio::spawn` to spawn tasks.
                    if let Err(err) = hyper::server::conn::http2::Builder::new(TokioExecutor::new())
                      // `serve_connection_with_upgrades` is required for websockets. If you don't need
                      // that you can use `serve_connection` instead.
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
}
