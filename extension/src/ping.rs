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
  deadline: u64,
) {
  while goaway_received.load(std::sync::atomic::Ordering::Relaxed) == false {
    let last_active_grace_period_ms = 5000;
    let close_before_deadline_ms = 15000;
    let last_active_ago_ms = time::current_time_millis() - last_active.load(Ordering::Relaxed);
    // TODO: Compute time we should stop at based on the initial function timeout duration
    if last_active_ago_ms > 1 * last_active_grace_period_ms
      || time::current_time_millis() + close_before_deadline_ms > deadline
    {
      if last_active_ago_ms > 1 * last_active_grace_period_ms {
        println!(
          "Last active: {} ms ago, requesting close",
          last_active_ago_ms
        );
      } else if time::current_time_millis() + close_before_deadline_ms > deadline {
        println!(
          "Deadline: {} ms away, requesting close",
          deadline - time::current_time_millis()
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
          "Connection ready check threw error - connection has disconnected, should reconnect"
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
          println!("Close request failed: {:?}", err);
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
      panic!("Connection ready check threw error - connection has disconnected, should reconnect");
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
          println!("409 received on ping, exiting");
          goaway_received.store(true, Ordering::Relaxed);
          break;
        }

        if parts.status != StatusCode::OK {
          println!("non-200 received on ping, exiting: {:?}", parts.status);
          goaway_received.store(true, Ordering::Relaxed);
          break;
        }
      }
      Err(err) => {
        println!("Ping request failed: {:?}", err);
        goaway_received.store(true, Ordering::Relaxed);
      }
    }

    println!(
      "X-Lambda-ID: {}, Requests: {}, GoAway: {}",
      lambda_id,
      count.load(Ordering::Relaxed),
      goaway_received.load(Ordering::Relaxed)
    );
    tokio::time::sleep(Duration::from_secs(5)).await;
  }
  println!(
    "X-Lambda-ID: {}, Requests: {}, GoAway: {}",
    lambda_id,
    count.load(Ordering::Relaxed),
    goaway_received.load(Ordering::Relaxed)
  );
}
