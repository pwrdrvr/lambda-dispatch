use std::sync::atomic::{AtomicBool, Ordering};
use std::{pin::Pin, sync::Arc};

use futures::Future;
use hyper::Uri;
use lambda_runtime::LambdaEvent;
use tokio::io::{AsyncRead, AsyncWrite};
use tokio::net::TcpStream;
use tokio::time::timeout;
use tower::Service;

use crate::endpoint::{Endpoint, Scheme};
use crate::lambda_request::LambdaRequest;
use crate::options::Options;
use crate::prelude::*;
use crate::time::current_time_millis;
use crate::{
  app_start,
  messages::{WaiterRequest, WaiterResponse},
};

use tokio_rustls::client::TlsStream;

// Define a Stream trait that both TlsStream and TcpStream implement
pub trait Stream: AsyncRead + AsyncWrite + Send {}
impl Stream for TlsStream<TcpStream> {}
impl Stream for TcpStream {}

#[derive(Clone)]
pub struct LambdaService {
  options: Options,
  initialized: Arc<AtomicBool>,
  healthcheck_url: Uri,
}

impl LambdaService {
  pub fn new(options: Options, initialized: Arc<AtomicBool>, healthcheck_url: Uri) -> Self {
    LambdaService {
      options,
      initialized,
      healthcheck_url,
    }
  }

  //
  // This is called by the Tower.Service trait impl below
  //
  async fn fetch_response(
    &self,
    event: LambdaEvent<WaiterRequest>,
  ) -> Result<WaiterResponse, Error> {
    // extract some useful info from the request
    let pool_id: PoolId = event
      .payload
      .pool_id
      .unwrap_or_else(|| "default".to_string())
      .into();
    let lambda_id: LambdaId = event.payload.id.into();
    let channel_count: u8 = event.payload.number_of_channels;
    let router_endpoint = event.payload.router_url.parse()?;

    log::info!(
      "PoolId: {}, LambdaId: {} - Received request",
      pool_id,
      lambda_id
    );

    let app_endpoint = Endpoint::new(Scheme::Http, "127.0.0.1", self.options.port);

    if !self.initialized.load(Ordering::SeqCst) {
      let fake_goaway_received = Arc::new(AtomicBool::new(false));
      let result = timeout(
        self.options.async_init_timeout,
        app_start::health_check_contained_app(
          Arc::clone(&fake_goaway_received),
          &self.healthcheck_url,
        ),
      )
      .await;

      match result {
        Ok(success) => {
          if !success {
            log::error!(
              "PoolId: {}, LambdaId: {} - Async init Health check returned false before timeout, bailing",
              pool_id,
              lambda_id
            );

            // goaway_received is a private var above, it can't be set to false
            // so this can never happen
            panic!("Health check returned false before timeout");
          }
          self.initialized.store(success, Ordering::SeqCst);
        }
        Err(_) => {
          log::error!(
            "PoolId: {}, LambdaId: {} - Async init Health check not ready before timeout, bailing",
            pool_id,
            lambda_id
          );

          // Set goaway_received to true to the loop will exit
          fake_goaway_received.store(true, Ordering::SeqCst);

          // We'll panic in the tower service if this happens
          return Err(anyhow::anyhow!(
            "Health check returned false before timeout"
          ));
        }
      }

      self.initialized.store(true, Ordering::SeqCst);
    }

    // prepare the response
    let resp = WaiterResponse {
      pool_id: pool_id.to_string(),
      id: lambda_id.to_string(),
    };

    if event.payload.init_only {
      log::info!(
        "PoolId: {}, LambdaId: {} - Returning from init-only request",
        pool_id,
        lambda_id
      );
      return Ok(resp);
    }

    // If the sent_time is more than 5 seconds old, just return
    // This is mostly needed locally where requests get stuck in the queue
    // Do not do this in a deployed env because an app that takes > 5 seconds to start
    // will get much longer initial request times
    let sent_time = chrono::DateTime::parse_from_rfc3339(&event.payload.sent_time)
      .context("unable to parse sent_time in lambda event payload")?;
    if self.options.local_env
      && sent_time.timestamp_millis() < (current_time_millis() - 5000).try_into().unwrap()
    {
      log::info!(
        "PoolId: {}, LambdaId: {} - Returning from stale request",
        pool_id,
        lambda_id
      );
      return Ok(resp);
    }

    log::info!(
      "PoolId: {}, LambdaId: {}, Timeout: {}s - Invoked",
      pool_id,
      lambda_id,
      (event.context.deadline - current_time_millis()) / 1000
    );
    let mut deadline_ms = event.context.deadline;
    if (deadline_ms - current_time_millis()) > 15 * 60 * 1000 {
      log::warn!("Deadline is greater than 15 minutes, trimming to 1 minute");
      deadline_ms = current_time_millis() + 60 * 1000;
    }
    // check if env var is set to force deadline for testing
    if let Some(force_deadline_secs) = self.options.force_deadline_secs {
      log::warn!(
        "Forcing deadline to {} seconds",
        force_deadline_secs.as_secs()
      );
      deadline_ms = current_time_millis() + force_deadline_secs.as_millis() as u64;
    }

    // run until we get a GoAway or deadline is about to be reached
    let mut lambda_request = LambdaRequest::new(
      app_endpoint,
      self.options.compression,
      pool_id,
      lambda_id,
      channel_count,
      router_endpoint,
      deadline_ms,
    );
    lambda_request.start().await?;

    Ok(resp)
  }
}

// Tower.Service is the interface required by lambda_runtime::run
impl Service<LambdaEvent<WaiterRequest>> for LambdaService {
  type Response = WaiterResponse;
  type Error = Error;
  type Future = Pin<Box<dyn Future<Output = Result<Self::Response, Self::Error>> + Send>>;

  fn poll_ready(
    &mut self,
    _cx: &mut core::task::Context<'_>,
  ) -> core::task::Poll<Result<(), Self::Error>> {
    core::task::Poll::Ready(Ok(()))
  }

  fn call(&mut self, event: LambdaEvent<WaiterRequest>) -> Self::Future {
    let adapter = self.clone();
    Box::pin(async move {
      match adapter.fetch_response(event).await {
        Ok(response) => Ok(response),
        Err(e) => {
          panic!("Error fetching response: {}", e);
        }
      }
    })
  }
}

#[cfg(test)]
mod tests {
  use std::{net::SocketAddr, sync::mpsc, thread};

  use super::*;
  use axum::{
    extract::Path,
    routing::{get, post},
    Router,
  };
  use httpmock::{Method::GET, MockServer};
  use hyper::{body::Incoming, Request};
  use hyper_util::rt::{TokioExecutor, TokioIo};
  use tokio::net::TcpListener;
  use tokio::sync::oneshot;

  async fn start_mock_server() -> TcpListener {
    TcpListener::bind(("127.0.0.1", 0)).await.unwrap()
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

  fn run_http2_app(app: Router) -> Serve {
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

  #[tokio::test]
  async fn test_not_initialized_healthcheck_200_ok() {
    // If you want to view logs during a test, uncomment this
    // let _ = env_logger::builder().is_test(true).try_init();

    // Start router server
    let (release_request_tx, release_request_rx) = tokio::sync::mpsc::channel::<()>(1);
    let release_request_rx = Arc::new(tokio::sync::Mutex::new(release_request_rx));
    // Use an arc int to count how many times the request endpoint was called
    let request_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let request_count_clone = Arc::clone(&request_count);
    let ping_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let ping_count_clone = Arc::clone(&ping_count);
    let close_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let close_count_clone = Arc::clone(&close_count);
    let app = Router::new()
      .route(
        "/api/chunked/request/:lambda_id/:channel_id",
        post(
          move |Path((lambda_id, channel_id)): Path<(String, String)>| {
            let request_count = Arc::clone(&request_count_clone);
            let release_request_rx = Arc::clone(&release_request_rx);
            async move {
              log::info!(
                "Request! LambdaID: {}, ChannelID: {}",
                lambda_id,
                channel_id
              );

              // Increment the request count
              request_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);

              // Wait for the release signal
              let mut release_request_rx = release_request_rx.lock().await;
              release_request_rx.recv().await.unwrap();
              format!(
                "Request! LambdaID: {}, ChannelID: {}",
                lambda_id, channel_id
              )
            }
          },
        ),
      )
      .route(
        "/api/chunked/ping/:lambda_id",
        get(|Path(lambda_id): Path<String>| async move {
          let ping_count = Arc::clone(&ping_count_clone);
          // Increment
          ping_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
          log::info!("Ping! LambdaID: {}", lambda_id);
          format!("Ping! LambdaID: {}", lambda_id)
        }),
      )
      .route(
        "/api/chunked/close/:lambda_id",
        get(|Path(lambda_id): Path<String>| async move {
          let close_count = Arc::clone(&close_count_clone);
          // Increment
          close_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
          log::info!("Close! LambdaID: {}", lambda_id);
          format!("Close! LambdaID: {}", lambda_id)
        }),
      );

    let server = run_http2_app(app);

    log::info!("Router server running on port: {}", server.addr.port());

    // Start app server
    let app_server = MockServer::start();
    let app_healthcheck_mock = app_server.mock(|when, then| {
      when.method(GET).path("/health");
      then.status(200).body("OK");
    });

    let mut options = Options::default();
    // Tell the service the port of the mock contained app
    options.port = server.addr.port();
    let initialized = false;

    let app_healthcheck_url: Uri = format!("{}/health", app_server.base_url()).parse().unwrap();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      app_healthcheck_url,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: format!("http://localhost:{}", server.addr.port()),
      number_of_channels: 1,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: false,
    };
    let mut context = lambda_runtime::Context::default();
    context.deadline = current_time_millis() + 60 * 1000;
    let event = LambdaEvent {
      payload: request,
      context,
    };

    // Release the request after a few seconds
    tokio::spawn(async move {
      tokio::time::sleep(tokio::time::Duration::from_secs(1)).await;
      release_request_tx.send(()).await.unwrap();
    });

    // Act
    // It should not be possible to get past this unless the service
    // has decided to exit itself
    let response = service.fetch_response(event).await;

    // Assert
    assert!(
      response.is_ok(),
      "fetch_response failed: {:?}",
      response.err()
    );
    // Get the WaiterResponse out of the response
    let resp = response.unwrap();
    assert_eq!(resp.id, "test_id", "Lambda ID");
    assert_eq!(resp.pool_id, "test_pool", "Pool ID");
    assert_eq!(
      request_count.load(std::sync::atomic::Ordering::SeqCst),
      1,
      "Request count"
    );
    // The ping count will be 0 because we do not start the response stream and leave
    // it open, which allows the pings to start
    assert_eq!(
      ping_count.load(std::sync::atomic::Ordering::SeqCst),
      0,
      "Ping count"
    );
    // The close count will be 0 because the router response indicated we should
    // close so we do not need to call back asking to close
    assert_eq!(
      close_count.load(std::sync::atomic::Ordering::SeqCst),
      0,
      "Close count"
    );

    // Assert app server's healthcheck endpoint got called
    app_healthcheck_mock.assert();
  }

  #[tokio::test]
  async fn test_spillover_healthcheck_blackhole_timeout() {
    // If you want to view logs during a test, uncomment this
    let _ = env_logger::builder().is_test(true).try_init();
    let mut options = Options::default();
    options.async_init_timeout = std::time::Duration::from_millis(1000);
    let initialized = false;

    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      // 192.0.2.0/24 (TEST-NET-1)
      "http://192.0.2.0:54321/health".parse().unwrap(),
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      // 192.0.2.0/24 (TEST-NET-1)
      router_url: format!("http://192.0.2.0:{}", 12345),
      number_of_channels: 1,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: false,
    };
    let mut context = lambda_runtime::Context::default();
    context.deadline = current_time_millis() + 60 * 1000;
    let event = LambdaEvent {
      payload: request,
      context,
    };

    // Act
    let start = std::time::Instant::now();
    let response = service.fetch_response(event).await;
    let duration = std::time::Instant::now().duration_since(start);

    assert!(response.is_err(), "fetch_response should have failed",);
    assert!(
      duration >= std::time::Duration::from_secs(1),
      "Connection should take at least 1 seconds"
    );
    assert!(
      duration <= std::time::Duration::from_secs(2),
      "Connection should take at most 2 seconds"
    );
  }
}
