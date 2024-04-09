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

use crate::prelude::*;
use crate::time;

pub async fn send_ping_requests(
  last_active: Arc<AtomicU64>,
  goaway_received: Arc<AtomicBool>,
  authority: String,
  mut sender: SendRequest<BoxBody<Bytes, Error>>,
  lambda_id: String,
  count: Arc<AtomicUsize>,
  scheme: String,
  host: String,
  port: u16,
  deadline_ms: u64,
  cancel_token: tokio_util::sync::CancellationToken,
  requests_in_flight: Arc<AtomicUsize>,
) {
  let start_time = time::current_time_millis();
  let mut last_ping_time = start_time;

  while !goaway_received.load(std::sync::atomic::Ordering::Acquire) && !cancel_token.is_cancelled()
  {
    let last_active_grace_period_ms = 250;
    let close_before_deadline_ms = 15000;
    let last_active = last_active.load(Ordering::Acquire);
    let last_active_ago_ms = if last_active == 0 {
      0
    } else {
      time::current_time_millis() - last_active
    };

    // Compute stats for log messages
    let count = count.load(Ordering::Acquire);
    let requests_in_flight = requests_in_flight.load(Ordering::Acquire);
    let elapsed = time::current_time_millis() - start_time;
    let rps = format!("{:.1}", count as f64 / (elapsed as f64 / 1000.0));

    // TODO: Compute time we should stop at based on the initial function timeout duration
    if (last_active != 0
      && last_active_ago_ms > last_active_grace_period_ms
      && requests_in_flight == 0)
      || time::current_time_millis() + close_before_deadline_ms > deadline_ms
    {
      if last_active != 0
        && last_active_ago_ms > last_active_grace_period_ms
        && requests_in_flight == 0
      {
        log::info!(
          "LambdaId: {}, Last Active: {} ms ago, Reqs in Flight: {}, Elapsed: {} ms, RPS: {} - Requesting close: Last Active",
          lambda_id,
          last_active_ago_ms,
          requests_in_flight,
          elapsed,
          rps
        );
      } else if time::current_time_millis() + close_before_deadline_ms > deadline_ms {
        log::info!(
          "LambdaId: {}, Deadline: {} ms Away, Reqs in Flight: {}, Elapsed: {} ms, RPS: {} - Requesting close: Deadline",
          lambda_id,
          deadline_ms - time::current_time_millis(),
          requests_in_flight,
          elapsed,
          rps
        );
      }

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
        .header("X-Lambda-Id", &lambda_id)
        .body(boxed_close_body)
        .unwrap();

      if sender.ready().await.is_err() {
        goaway_received.store(true, Ordering::Release);

        // This gets hit when the connection for HTTP/1.1 faults
        panic!(
          "Ping Loop - Router connection ready check threw error - connection has disconnected, should reconnect"
        );
      }

      let res = sender.send_request(close_req).await;
      close_tx.close().await.unwrap();
      match res {
        Ok(mut res) => {
          // Rip through and discard so the response stream is closed
          while res.frame().await.is_some() {}
        }
        Err(err) => {
          log::error!(
            "LambdaId: {} - PingLoop - Close request failed: {:?}",
            lambda_id,
            err
          );
        }
      }

      // Now mark that we are going away, after router has responded to our close request
      goaway_received.store(true, Ordering::Release);
      break;
    }

    // Send a ping request after we have initialized
    if last_active > 0 && (time::current_time_millis() - last_ping_time) >= 5000 {
      last_ping_time = time::current_time_millis();

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
        .header("X-Lambda-Id", &lambda_id)
        .body(boxed_ping_body)
        .unwrap();

      if sender.ready().await.is_err() {
        // This gets hit when the connection faults
        panic!("LambdaId: {} - Ping Loop - Connection ready check threw error - connection has disconnected, should reconnect", lambda_id);
      }

      let res = sender.send_request(ping_req).await;
      ping_tx.close().await.unwrap();
      match res {
        Ok(res) => {
          let (parts, mut res_stream) = res.into_parts();

          // Rip through and discard so the response stream is closed
          while res_stream.frame().await.is_some() {}

          if parts.status == 409 {
            log::info!(
              "LambdaId: {} - Ping Loop - 409 received on ping, exiting",
              lambda_id
            );
            goaway_received.store(true, Ordering::Release);
            break;
          }

          if parts.status != StatusCode::OK {
            log::info!(
              "LambdaId: {} - Ping Loop - non-200 received on ping, exiting: {:?}",
              lambda_id,
              parts.status
            );
            goaway_received.store(true, Ordering::Release);
            break;
          }
        }
        Err(err) => {
          log::error!(
            "LambdaId: {} - Ping Loop - Ping request failed: {:?}",
            lambda_id,
            err
          );
          goaway_received.store(true, Ordering::Release);
        }
      }

      log::info!(
        "LambdaId: {}, Requests: {}, GoAway: {}, Reqs in Flight: {}, Elapsed: {} ms, RPS: {} - Ping Loop - Looping",
        lambda_id,
        count,
        goaway_received.load(Ordering::Acquire),
        requests_in_flight,
        elapsed,
        rps
      );
    }

    tokio::select! {
        _ = cancel_token.cancelled() => {
          // The token was cancelled
          log::info!("LambdaId: {}, Requests: {}, GoAway: {}, Reqs in Flight: {}, Elapsed: {} ms, RPS: {} - Ping Loop - Cancelled",
            lambda_id,
            count,
            goaway_received.load(Ordering::Acquire),
            requests_in_flight,
            elapsed,
            rps
          );
        }
        _ = tokio::time::sleep(Duration::from_millis(100)) => {
        }
    };
  }

  let count = count.load(Ordering::Acquire);
  let elapsed = time::current_time_millis() - start_time;
  log::info!(
    "LambdaId: {}, Requests: {}, GoAway: {}, Reqs in Flight: {}, Elapsed: {} ms, RPS: {} - Ping Loop - Exiting",
    lambda_id,
    count,
    goaway_received.load(Ordering::Acquire),
    requests_in_flight.load(Ordering::Acquire),
    elapsed,
    format!("{:.1}", count as f64 / (elapsed as f64 / 1000.0))
  );
}
