use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::time::{Duration, SystemTime};
use std::{pin::Pin, sync::Arc};

use futures::channel::mpsc;
use futures::SinkExt;
use http_body_util::combinators::BoxBody;
use http_body_util::{BodyExt, StreamBody};
use httpdate::fmt_http_date;
use hyper::body::Body;
use hyper::client::conn::http2::SendRequest;
use hyper::StatusCode;
use hyper::{
  body::{Bytes, Frame, Incoming},
  Request,
};

use crate::time;

type Result<T> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;

pub async fn send_ping_requests(
  last_active: Arc<AtomicU64>,
  goaway_received: Arc<AtomicBool>,
  authority: String,
  mut sender: SendRequest<BoxBody<Bytes, Box<dyn std::error::Error + Send + Sync>>>,
  lambda_id: String,
  count: Arc<AtomicUsize>,
  scheme: String,
  host: String,
  port: u16,
  deadline_ms: u64,
  cancel_sleep: tokio_util::sync::CancellationToken,
  requests_in_flight: Arc<AtomicUsize>,
) {
  while goaway_received.load(std::sync::atomic::Ordering::Relaxed) == false {
    let last_active_grace_period_ms = 5000;
    let close_before_deadline_ms = 15000;
    let last_active_ago_ms = time::current_time_millis() - last_active.load(Ordering::Relaxed);
    // TODO: Compute time we should stop at based on the initial function timeout duration
    if last_active_ago_ms > 1 * last_active_grace_period_ms
      || time::current_time_millis() + close_before_deadline_ms > deadline_ms
    {
      if last_active_ago_ms > 1 * last_active_grace_period_ms {
        log::info!(
          "LambdaId: {}, Last Active: {} ms ago, Reqs in Flight: {} - Requesting close",
          lambda_id.clone(),
          last_active_ago_ms,
          requests_in_flight.load(Ordering::Relaxed)
        );
      } else if time::current_time_millis() + close_before_deadline_ms > deadline_ms {
        log::info!(
          "LambdaId: {}, Deadline: {} ms Away, Reqs in Flight: {} - Requesting close",
          lambda_id.clone(),
          deadline_ms - time::current_time_millis(),
          requests_in_flight.load(Ordering::Relaxed)
        );
      }
      goaway_received.store(true, Ordering::Relaxed);

      // Send Close request to router
      let close_url = format!(
        "{}://{}:{}/api/chunked/close/{}",
        scheme, host, port, lambda_id
      );
      let (mut close_tx, close_recv) = mpsc::channel::<Result<Frame<Bytes>>>(1);
      let boxed_close_body = BodyExt::boxed(StreamBody::new(close_recv));
      let close_req = Request::builder()
        .uri(&close_url)
        .method("GET")
        .header(hyper::header::DATE, fmt_http_date(SystemTime::now()))
        .header(hyper::header::HOST, authority.as_str())
        .header("X-Lambda-Id", lambda_id.to_string())
        .body(boxed_close_body)
        .unwrap();

      while futures::future::poll_fn(|ctx| sender.poll_ready(ctx))
        .await
        .is_err()
      {
        // This gets hit when the connection for HTTP/1.1 faults
        panic!(
          "Ping Loop - Router connection ready check threw error - connection has disconnected, should reconnect"
        );
      }

      let res = sender.send_request(close_req).await;
      close_tx.close().await.unwrap();
      match res {
        Ok(res) => {
          let (_, mut res_stream) = res.into_parts();

          // Rip through and discard so the response stream is closed
          while let Some(_chunk) =
            futures::future::poll_fn(|cx| Incoming::poll_frame(Pin::new(&mut res_stream), cx)).await
          {
          }
        }
        Err(err) => {
          log::error!(
            "LambdaId: {} - PingLoop - Close request failed: {:?}",
            lambda_id.clone(),
            err
          );
        }
      }

      break;
    }

    // Send a ping request
    let ping_url = format!(
      "{}://{}:{}/api/chunked/ping/{}",
      scheme, host, port, lambda_id
    );
    let (mut ping_tx, ping_recv) = mpsc::channel::<Result<Frame<Bytes>>>(1);
    let boxed_ping_body = BodyExt::boxed(StreamBody::new(ping_recv));
    let ping_req = Request::builder()
      .uri(&ping_url)
      .method("GET")
      .header(hyper::header::DATE, fmt_http_date(SystemTime::now()))
      .header(hyper::header::HOST, authority.as_str())
      .header("X-Lambda-Id", lambda_id.to_string())
      .body(boxed_ping_body)
      .unwrap();

    while futures::future::poll_fn(|ctx| sender.poll_ready(ctx))
      .await
      .is_err()
    {
      // This gets hit when the connection faults
      panic!("LambdaId: {} - Ping Loop - Connection ready check threw error - connection has disconnected, should reconnect", lambda_id.clone());
    }

    let res = sender.send_request(ping_req).await;
    ping_tx.close().await.unwrap();
    match res {
      Ok(res) => {
        let (parts, mut res_stream) = res.into_parts();

        // Rip through and discard so the response stream is closed
        while let Some(_chunk) =
          futures::future::poll_fn(|cx| Incoming::poll_frame(Pin::new(&mut res_stream), cx)).await
        {
        }

        if parts.status == 409 {
          log::info!(
            "LambdaId: {} - Ping Loop - 409 received on ping, exiting",
            lambda_id.clone()
          );
          goaway_received.store(true, Ordering::Relaxed);
          break;
        }

        if parts.status != StatusCode::OK {
          log::info!(
            "LambdaId: {} - Ping Loop - non-200 received on ping, exiting: {:?}",
            lambda_id.clone(),
            parts.status
          );
          goaway_received.store(true, Ordering::Relaxed);
          break;
        }
      }
      Err(err) => {
        log::error!(
          "LambdaId: {} - Ping Loop - Ping request failed: {:?}",
          lambda_id.clone(),
          err
        );
        goaway_received.store(true, Ordering::Relaxed);
      }
    }

    log::info!(
      "LambdaId: {}, Requests: {}, GoAway: {}, Reqs in Flight: {} - Ping Loop - Looping",
      lambda_id,
      count.load(Ordering::Relaxed),
      goaway_received.load(Ordering::Relaxed),
      requests_in_flight.load(Ordering::Relaxed)
    );

    tokio::select! {
        _ = cancel_sleep.cancelled() => {
          // The token was cancelled
          log::info!("LambdaId: {}, Reqs in Flight: {} - Ping Loop - Cancelled",
            lambda_id.clone(),
            requests_in_flight.load(Ordering::Relaxed)
          );
        }
        _ = tokio::time::sleep(Duration::from_secs(5)) => {
        }
    };
  }

  log::info!(
    "LambdaId: {}, Requests: {}, GoAway: {}, Reqs in Flight: {} - Ping Loop - Exiting",
    lambda_id,
    count.load(Ordering::Relaxed),
    goaway_received.load(Ordering::Relaxed),
    requests_in_flight.load(Ordering::Relaxed)
  );
}
