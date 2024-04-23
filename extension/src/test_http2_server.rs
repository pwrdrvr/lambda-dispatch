#[cfg(test)]
pub mod test_http2_server {
  use std::{net::SocketAddr, sync::mpsc, thread};

  use axum::Router;
  use hyper::{body::Incoming, Request};
  use hyper_util::rt::{TokioExecutor, TokioIo};
  use tokio::net::TcpListener;
  use tokio::sync::oneshot;
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
