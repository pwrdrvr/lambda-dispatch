use std::sync::atomic::AtomicBool;
use std::sync::Arc;
use std::time::Duration;

use http_body_util::Empty;
use hyper::{
  body::{Bytes, Incoming},
  Request, Uri,
};
use hyper_util::rt::TokioIo;
use tokio::net::TcpStream;

async fn create_connection(
  healthcheck_addr: &str,
) -> Option<(
  hyper::client::conn::http1::SendRequest<Empty<Bytes>>,
  hyper::client::conn::http1::Connection<TokioIo<TcpStream>, Empty<Bytes>>,
)> {
  let tcp_stream = match TcpStream::connect(healthcheck_addr).await {
    Err(err) => {
      log::debug!(
        "Health check - Failed to connect to contained app: {:?}",
        err
      );
      return None;
    }
    Ok(tcp_stream) => {
      log::debug!("Health check - Connected to contained app");
      tcp_stream
    }
  };
  tcp_stream.set_nodelay(true).unwrap();
  let io = TokioIo::new(tcp_stream);
  match hyper::client::conn::http1::handshake(io).await {
    Err(err) => {
      log::debug!(
        "Health check - Failed to handshake with contained app: {:?}",
        err
      );
      None
    }
    Ok((sender, conn)) => {
      log::info!("Health check - Handshake with contained app success");
      Some((sender, conn))
    }
  }
}

async fn send_request(
  sender: &mut hyper::client::conn::http1::SendRequest<Empty<Bytes>>,
) -> Option<hyper::Response<Incoming>> {
  let req = Request::builder()
    .method("GET")
    .uri("/health")
    .header(hyper::header::HOST, "localhost")
    .body(http_body_util::Empty::<Bytes>::new())
    .unwrap();

  match sender.send_request(req).await {
    Err(err) => {
      log::debug!(
        "Health check - Failed to send request to contained app: {:?}",
        err
      );
      None
    }
    Ok(res) => {
      log::debug!("Health check - Send request to contained app success");
      Some(res)
    }
  }
}

pub async fn health_check_contained_app(
  goaway_received: Arc<AtomicBool>,
  healthcheck_url: &Uri,
) -> bool {
  let healthcheck_host = healthcheck_url.host().expect("uri has no host");
  let healthcheck_port = healthcheck_url.port_u16().unwrap_or(80);
  let healthcheck_addr = format!("{}:{}", healthcheck_host, healthcheck_port);

  log::info!(
    "Health check - Starting for contained app at: {}",
    healthcheck_url.to_string()
  );

  let mut sender = None;
  let mut conn = None;

  while !goaway_received.load(std::sync::atomic::Ordering::Acquire) {
    tokio::time::sleep(Duration::from_millis(10)).await;

    if sender.is_none() || conn.is_none() {
      let connection = create_connection(&healthcheck_addr).await;
      if let Some((s, c)) = connection {
        sender = Some(s);
        conn = Some(tokio::task::spawn(async move {
          if let Err(err) = c.await {
            log::error!("Health check - Connection failed: {:?}", err);
          }
        }));
      }
      continue;
    }

    let usable_sender = sender.as_mut().unwrap();

    if futures::future::poll_fn(|ctx| usable_sender.poll_ready(ctx))
      .await
      .is_err()
    {
      // The connection has errored
      sender.take();
      conn.take();
      continue;
    }

    let res = send_request(usable_sender).await;

    if let Some(res) = res {
      let (parts, _) = res.into_parts();
      if parts.status == hyper::StatusCode::OK {
        log::info!("Health check - Complete - Success");
        return true;
      } else {
        log::debug!("Health check - Failed: {:?}\nHeaders:", parts.status);
        for header in parts.headers.iter() {
          log::debug!("  {}: {}", header.0, header.1.to_str().unwrap());
        }
      }
    } else {
      // The connection errored with a non-HTTP error
      sender.take();
      conn.take();
    }
  }

  log::info!("Health check - Complete - Failed");
  false
}
