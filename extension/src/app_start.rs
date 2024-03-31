use std::sync::atomic::AtomicBool;
use std::sync::Arc;
use std::time::Duration;

use hyper::{body::Bytes, Request, Uri};
use hyper_util::rt::TokioIo;
use tokio::net::TcpStream;

use crate::options::Options;

pub async fn health_check_contained_app(goaway_received: Arc<AtomicBool>, options: &Options) {
  let app_url: Uri = format!("http://127.0.0.1:{}/health", options.port)
    .parse()
    .unwrap();
  let app_host = app_url.host().expect("uri has no host");
  let app_port = app_url.port_u16().unwrap_or(80);
  let app_addr = format!("{}:{}", app_host, app_port);

  while goaway_received.load(std::sync::atomic::Ordering::Acquire) == false {
    // Delay 10 ms
    tokio::time::sleep(Duration::from_millis(10)).await;

    // Create http connection
    let tcp_stream = match TcpStream::connect(app_addr.clone()).await {
      Err(err) => {
        log::debug!("Failed to connect to contained app: {:?}", err);
        continue;
      }
      Ok(tcp_stream) => {
        log::debug!("Connected to contained app");
        tcp_stream
      }
    };
    tcp_stream.set_nodelay(true).unwrap();
    let io = TokioIo::new(tcp_stream);
    let (mut sender, conn) = match hyper::client::conn::http1::handshake(io).await {
      Err(err) => {
        log::debug!("Failed to handshake with contained app: {:?}", err);
        continue;
      }
      Ok((sender, conn)) => {
        log::debug!("Handshake with contained app success");
        (sender, conn)
      }
    };
    tokio::task::spawn(async move {
      if let Err(err) = conn.await {
        log::error!("Healthcheck connection failed: {:?}", err);
      }
    });
    let req = Request::builder()
      .method("GET")
      .uri("/health")
      .header(hyper::header::HOST, "localhost")
      .body(http_body_util::Empty::<Bytes>::new())
      .unwrap();

    while futures::future::poll_fn(|ctx| sender.poll_ready(ctx))
      .await
      .is_err()
    {
      // This gets hit when the connection for faults
      continue;
    }

    let res = match sender.send_request(req).await {
      Err(err) => {
        log::debug!("Failed to send request to contained app: {:?}", err);
        continue;
      }
      Ok(res) => {
        log::debug!("Send request to contained app success");
        res
      }
    };
    let (parts, _) = res.into_parts();
    if parts.status == hyper::StatusCode::OK {
      log::info!("Health check success");
      break;
    } else {
      log::debug!("Health check failed: {:?}\nHeaders:", parts.status);
      // Print all the headers received and the body
      for header in parts.headers.iter() {
        log::info!("  {}: {}", header.0, header.1.to_str().unwrap());
      }
    }
  }

  log::info!("Health check complete");
}
