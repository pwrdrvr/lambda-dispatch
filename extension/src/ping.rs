use std::{
  borrow::Cow,
  sync::{
    atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering},
    Arc,
  },
  time::{Duration, SystemTime},
};

use futures::{channel::mpsc, SinkExt};
use http_body_util::{BodyExt, StreamBody};
use httpdate::fmt_http_date;
use hyper::{
  body::{Bytes, Frame},
  Request, StatusCode,
};

use crate::time;
use crate::{endpoint::Endpoint, messages};
use crate::{prelude::*, router_client::RouterClient};

#[derive(PartialEq, Debug)]
pub enum PingResult {
  GoAway,
  Deadline,
  LastActive,
  CancelToken,
  ConnectionError,
  StatusCode5xx,
  StatusCode4xx,
  StatusCodeOther,
}

impl From<PingResult> for Option<messages::ExitReason> {
  fn from(result: PingResult) -> Self {
    match result {
      PingResult::GoAway => Some(messages::ExitReason::RouterGoAway),
      PingResult::Deadline => Some(messages::ExitReason::SelfDeadline),
      PingResult::LastActive => Some(messages::ExitReason::SelfLastActive),
      PingResult::CancelToken => None,
      PingResult::ConnectionError => Some(messages::ExitReason::RouterConnectionError),
      PingResult::StatusCode5xx => Some(messages::ExitReason::RouterStatus5xx),
      PingResult::StatusCode4xx => Some(messages::ExitReason::RouterStatus4xx),
      PingResult::StatusCodeOther => Some(messages::ExitReason::RouterStatus4xx),
    }
  }
}

pub async fn send_ping_requests(
  last_active: Arc<AtomicU64>,
  goaway_received: Arc<AtomicBool>,
  router_client: RouterClient,
  pool_id: PoolId,
  lambda_id: LambdaId,
  count: Arc<AtomicUsize>,
  router_endpoint: Endpoint,
  deadline_ms: u64,
  cancel_token: tokio_util::sync::CancellationToken,
  requests_in_flight: Arc<AtomicUsize>,
) -> Option<PingResult> {
  let mut ping_result = None;
  let start_time = time::current_time_millis();
  let mut last_ping_time = start_time;

  let scheme = router_endpoint.scheme();
  let host = router_endpoint.host();
  let port = router_endpoint.port();

  // Compute host header now in case we need to allocate
  let host_header = router_endpoint.host_header();

  let ping_url = format!(
    "{}://{}:{}/api/chunked/ping/{}",
    scheme.as_ref(),
    host,
    port,
    lambda_id
  );
  let close_url = format!(
    "{}://{}:{}/api/chunked/close/{}",
    scheme.as_ref(),
    host,
    port,
    lambda_id
  );

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
          "PoolId: {}, LambdaId: {}, Last Active: {} ms ago, Reqs in Flight: {}, Elapsed: {} ms, RPS: {} - Requesting close: Last Active",
          pool_id,
          lambda_id,
          last_active_ago_ms,
          requests_in_flight,
          elapsed,
          rps
        );
        ping_result.get_or_insert(PingResult::LastActive);
      } else if time::current_time_millis() + close_before_deadline_ms > deadline_ms {
        log::info!(
          "PoolId: {}, LambdaId: {}, Deadline: {} ms Away, Reqs in Flight: {}, Elapsed: {} ms, RPS: {} - Requesting close: Deadline",
          pool_id,
          lambda_id,
          deadline_ms - time::current_time_millis(),
          requests_in_flight,
          elapsed,
          rps
        );
        ping_result.get_or_insert(PingResult::Deadline);
      }

      // Send Close request to router
      let (mut close_tx, close_recv) = mpsc::channel::<Result<Frame<Bytes>>>(1);
      let boxed_close_body = BodyExt::boxed(StreamBody::new(close_recv));
      let close_req = Request::builder()
        .uri(&close_url)
        .method("GET")
        .header(hyper::header::DATE, fmt_http_date(SystemTime::now()))
        .header("X-Pool-Id", pool_id.as_ref())
        .header("X-Lambda-Id", lambda_id.as_ref());
      let close_req = match &host_header {
        Cow::Borrowed(v) => close_req.header(hyper::header::HOST, *v),
        Cow::Owned(v) => close_req.header(hyper::header::HOST, v),
      };
      let close_req = close_req.body(boxed_close_body).unwrap();

      let router_result = router_client.request(close_req).await;
      close_tx.close().await.unwrap();
      match router_result {
        Ok(mut router_res) => {
          // Rip through and discard so the response stream is closed
          while router_res.frame().await.is_some() {}
        }
        Err(err) => {
          log::error!(
            "PoolId: {}, LambdaId: {} - PingLoop - Close request failed: {:?}",
            pool_id,
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

      let (mut ping_tx, ping_recv) = mpsc::channel::<Result<Frame<Bytes>>>(1);
      let boxed_ping_body = BodyExt::boxed(StreamBody::new(ping_recv));
      let ping_req = Request::builder()
        .uri(&ping_url)
        .method("GET")
        .header(hyper::header::DATE, fmt_http_date(SystemTime::now()))
        .header("X-Pool-Id", pool_id.as_ref())
        .header("X-Lambda-Id", lambda_id.as_ref());
      let ping_req = match &host_header {
        Cow::Borrowed(v) => ping_req.header(hyper::header::HOST, *v),
        Cow::Owned(v) => ping_req.header(hyper::header::HOST, v),
      };
      let ping_req = ping_req.body(boxed_ping_body).unwrap();

      let router_result = router_client.request(ping_req).await;
      ping_tx.close().await.unwrap();
      match router_result {
        Ok(router_res) => {
          let (parts, mut res_stream) = router_res.into_parts();

          // Rip through and discard so the response stream is closed
          while res_stream.frame().await.is_some() {}

          if parts.status == StatusCode::CONFLICT {
            log::info!(
              "PoolId: {}, LambdaId: {} - Ping Loop - 409 received on ping, exiting",
              pool_id,
              lambda_id
            );
            ping_result.get_or_insert(PingResult::GoAway);
            goaway_received.store(true, Ordering::Release);
            break;
          }

          if parts.status != StatusCode::OK {
            if parts.status.is_server_error() {
              ping_result.get_or_insert(PingResult::StatusCode5xx);
            } else if parts.status.is_client_error() {
              ping_result.get_or_insert(PingResult::StatusCode4xx);
            } else {
              ping_result.get_or_insert(PingResult::StatusCodeOther);
            }
            log::info!(
              "PoolId: {}, LambdaId: {} - Ping Loop - non-200 received on ping, exiting: {:?}",
              pool_id,
              lambda_id,
              parts.status
            );
            goaway_received.store(true, Ordering::Release);
            break;
          }
        }
        Err(err) => {
          ping_result.get_or_insert(PingResult::ConnectionError);
          log::error!(
            "PoolId: {}, LambdaId: {} - Ping Loop - Ping request failed: {:?}",
            pool_id,
            lambda_id,
            err
          );
          goaway_received.store(true, Ordering::Release);
          break;
        }
      }

      log::info!(
        "PoolId: {}, LambdaId: {}, Requests: {}, GoAway: {}, Reqs in Flight: {}, Elapsed: {} ms, RPS: {} - Ping Loop - Looping",
        pool_id,
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
          log::info!("PoolId: {}, LambdaId: {}, Requests: {}, GoAway: {}, Reqs in Flight: {}, Elapsed: {} ms, RPS: {} - Ping Loop - Cancelled",
            pool_id,
            lambda_id,
            count,
            goaway_received.load(Ordering::Acquire),
            requests_in_flight,
            elapsed,
            rps
          );

          ping_result.get_or_insert(PingResult::CancelToken);
        }
        _ = tokio::time::sleep(Duration::from_millis(100)) => {
        }
    };
  }

  let count = count.load(Ordering::Acquire);
  let elapsed = time::current_time_millis() - start_time;
  log::info!(
    "PoolId: {}, LambdaId: {}, Requests: {}, GoAway: {}, Reqs in Flight: {}, Elapsed: {} ms, RPS: {:.1} - Ping Loop - Exiting",
    pool_id,
    lambda_id,
    count,
    goaway_received.load(Ordering::Acquire),
    requests_in_flight.load(Ordering::Acquire),
    elapsed,
    count as f64 / (elapsed as f64 / 1000.0)
  );

  ping_result
}

#[cfg(test)]
mod tests {
  use super::*;

  use std::sync::Arc;

  use crate::router_client::create_router_client;
  use crate::{endpoint::Endpoint, test_http2_server::run_http2_app};

  use axum::{extract::Path, routing::get, Router};
  use hyper::StatusCode;

  #[tokio::test]
  async fn test_ping_immediate_exit_deadline() {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();

    // Start router server
    // Use an arc int to count how many times the request endpoint was called
    let ping_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let ping_count_clone = Arc::clone(&ping_count);
    let close_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let close_count_clone = Arc::clone(&close_count);
    let app = Router::new()
      .route(
        "/api/chunked/ping/:lambda_id",
        get(|Path(lambda_id): Path<String>| async move {
          let ping_count = Arc::clone(&ping_count_clone);
          // Increment
          ping_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
          format!("Ping! LambdaID: {}", lambda_id)
        }),
      )
      .route(
        "/api/chunked/close/:lambda_id",
        get(|Path(lambda_id): Path<String>| async move {
          let close_count = Arc::clone(&close_count_clone);
          // Increment
          close_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
          format!("Close! LambdaID: {}", lambda_id)
        }),
      );
    let mock_router_server = run_http2_app(app);

    let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
      .parse()
      .unwrap();

    let pool_id_arc: PoolId = pool_id.clone().into();
    let lambda_id_arc: LambdaId = lambda_id.clone().into();

    // Declare the counts
    let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let last_active = Arc::new(std::sync::atomic::AtomicU64::new(0));
    let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let cancel_token = tokio_util::sync::CancellationToken::new();
    let deadline_ms = 2000;

    // Act
    let result = send_ping_requests(
      Arc::clone(&last_active),
      Arc::clone(&goaway_received),
      create_router_client(),
      Arc::clone(&pool_id_arc),
      Arc::clone(&lambda_id_arc),
      Arc::new(std::sync::atomic::AtomicUsize::new(0)), // count is only used in log messages
      router_endpoint,
      deadline_ms,
      cancel_token,
      Arc::clone(&requests_in_flight),
    )
    .await;

    // Assert
    assert_eq!(
      result,
      Some(PingResult::Deadline),
      "result should be Deadline"
    );
    assert_eq!(
      ping_count.load(std::sync::atomic::Ordering::SeqCst),
      0,
      "ping count should be 0"
    );
    assert_eq!(
      close_count.load(std::sync::atomic::Ordering::SeqCst),
      1,
      "close count should be 1"
    );
    assert_eq!(
      requests_in_flight.load(std::sync::atomic::Ordering::SeqCst),
      0,
      "requests in flight should be 0"
    );
    assert_eq!(
      last_active.load(std::sync::atomic::Ordering::SeqCst),
      0,
      "last active should be 0"
    );
    assert_eq!(
      goaway_received.load(std::sync::atomic::Ordering::SeqCst),
      true,
      "goaway received should be true"
    );
  }

  #[tokio::test]
  async fn test_ping_channel_last_active() {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();

    // Start router server
    // Use an arc int to count how many times the request endpoint was called
    let ping_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let ping_count_clone = Arc::clone(&ping_count);
    let close_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let close_count_clone = Arc::clone(&close_count);
    let app = Router::new()
      .route(
        "/api/chunked/ping/:lambda_id",
        get(|Path(lambda_id): Path<String>| async move {
          let ping_count = Arc::clone(&ping_count_clone);
          // Increment
          ping_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
          format!("Ping! LambdaID: {}", lambda_id)
        }),
      )
      .route(
        "/api/chunked/close/:lambda_id",
        get(|Path(lambda_id): Path<String>| async move {
          let close_count = Arc::clone(&close_count_clone);
          // Increment
          close_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
          format!("Close! LambdaID: {}", lambda_id)
        }),
      );
    let mock_router_server = run_http2_app(app);

    let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
      .parse()
      .unwrap();

    let pool_id_arc: PoolId = pool_id.clone().into();
    let lambda_id_arc: LambdaId = lambda_id.clone().into();

    // Declare the counts
    let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let last_active_intial = time::current_time_millis();
    let last_active = Arc::new(std::sync::atomic::AtomicU64::new(last_active_intial));
    let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let cancel_token = tokio_util::sync::CancellationToken::new();
    let deadline_ms = time::current_time_millis() + 30000;

    // Act
    let result = send_ping_requests(
      Arc::clone(&last_active),
      Arc::clone(&goaway_received),
      create_router_client(),
      Arc::clone(&pool_id_arc),
      Arc::clone(&lambda_id_arc),
      Arc::new(std::sync::atomic::AtomicUsize::new(0)), // count is only used in log messages
      router_endpoint,
      deadline_ms,
      cancel_token,
      Arc::clone(&requests_in_flight),
    )
    .await;

    // Assert
    assert_eq!(
      result,
      Some(PingResult::LastActive),
      "result should be LastActive"
    );
    assert_eq!(
      ping_count.load(std::sync::atomic::Ordering::SeqCst),
      0,
      "ping count should be 0"
    );
    assert_eq!(
      close_count.load(std::sync::atomic::Ordering::SeqCst),
      1,
      "close count should be 1"
    );
    assert_eq!(
      requests_in_flight.load(std::sync::atomic::Ordering::SeqCst),
      0,
      "requests in flight should be 0"
    );
    assert_eq!(
      last_active.load(std::sync::atomic::Ordering::SeqCst),
      last_active_intial,
      "last active should not be 0"
    );
    assert_eq!(
      goaway_received.load(std::sync::atomic::Ordering::SeqCst),
      true,
      "goaway received should be true"
    );
  }

  #[tokio::test]
  async fn test_ping_channel_cancel_token() {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();

    // Start router server
    // Use an arc int to count how many times the request endpoint was called
    let ping_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let ping_count_clone = Arc::clone(&ping_count);
    let close_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let close_count_clone = Arc::clone(&close_count);
    let app = Router::new()
      .route(
        "/api/chunked/ping/:lambda_id",
        get(|Path(lambda_id): Path<String>| async move {
          let ping_count = Arc::clone(&ping_count_clone);
          // Increment
          ping_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
          format!("Ping! LambdaID: {}", lambda_id)
        }),
      )
      .route(
        "/api/chunked/close/:lambda_id",
        get(|Path(lambda_id): Path<String>| async move {
          let close_count = Arc::clone(&close_count_clone);
          // Increment
          close_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
          format!("Close! LambdaID: {}", lambda_id)
        }),
      );
    let mock_router_server = run_http2_app(app);

    let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
      .parse()
      .unwrap();

    let pool_id_arc: PoolId = pool_id.clone().into();
    let lambda_id_arc: LambdaId = lambda_id.clone().into();

    // Declare the counts
    let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let last_active_intial = time::current_time_millis();
    let last_active = Arc::new(std::sync::atomic::AtomicU64::new(last_active_intial));
    let requests_in_flight_initial = 3;
    let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(
      requests_in_flight_initial,
    ));
    let cancel_token = tokio_util::sync::CancellationToken::new();
    let deadline_ms = time::current_time_millis() + 30000;

    // Spawn a task to flip the cancel token in 1 second
    let cancel_token_clone = cancel_token.clone();
    tokio::spawn(async move {
      tokio::time::sleep(Duration::from_millis(1000)).await;
      cancel_token_clone.cancel();
    });

    // Act
    let result = send_ping_requests(
      Arc::clone(&last_active),
      Arc::clone(&goaway_received),
      create_router_client(),
      Arc::clone(&pool_id_arc),
      Arc::clone(&lambda_id_arc),
      Arc::new(std::sync::atomic::AtomicUsize::new(0)), // count is only used in log messages
      router_endpoint,
      deadline_ms,
      cancel_token,
      Arc::clone(&requests_in_flight),
    )
    .await;

    // Assert
    assert_eq!(
      result,
      Some(PingResult::CancelToken),
      "result should be CancelToken"
    );
    assert_eq!(
      ping_count.load(std::sync::atomic::Ordering::SeqCst),
      0,
      "ping count should be 0"
    );
    assert_eq!(
      close_count.load(std::sync::atomic::Ordering::SeqCst),
      0,
      "close count should be 0"
    );
    assert_eq!(
      requests_in_flight.load(std::sync::atomic::Ordering::SeqCst),
      requests_in_flight_initial,
      "requests in flight"
    );
    assert_eq!(
      last_active.load(std::sync::atomic::Ordering::SeqCst),
      last_active_intial,
      "last active should not be 0"
    );
    assert_eq!(
      goaway_received.load(std::sync::atomic::Ordering::SeqCst),
      false,
      "goaway received should be false"
    );
  }

  #[tokio::test]
  async fn test_ping_channel_status_305() {
    test_ping_status_code(StatusCode::USE_PROXY, Some(PingResult::StatusCodeOther)).await;
  }

  #[tokio::test]
  async fn test_ping_channel_status_400() {
    test_ping_status_code(StatusCode::BAD_REQUEST, Some(PingResult::StatusCode4xx)).await;
  }

  #[tokio::test]
  async fn test_ping_channel_status_409() {
    test_ping_status_code(StatusCode::CONFLICT, Some(PingResult::GoAway)).await;
  }

  #[tokio::test]
  async fn test_ping_channel_status_500() {
    test_ping_status_code(
      StatusCode::INTERNAL_SERVER_ERROR,
      Some(PingResult::StatusCode5xx),
    )
    .await;
  }

  async fn test_ping_status_code(status_code: StatusCode, expected_result: Option<PingResult>) {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();

    // Start router server
    // Use an arc int to count how many times the request endpoint was called
    let ping_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let ping_count_clone = Arc::clone(&ping_count);
    let close_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let close_count_clone = Arc::clone(&close_count);
    let app = Router::new()
      .route(
        "/api/chunked/ping/:lambda_id",
        get(move |Path(lambda_id): Path<String>| async move {
          let ping_count = Arc::clone(&ping_count_clone);
          // Increment
          ping_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
          (status_code, format!("Ping! LambdaID: {}", lambda_id))
        }),
      )
      .route(
        "/api/chunked/close/:lambda_id",
        get(|Path(lambda_id): Path<String>| async move {
          let close_count = Arc::clone(&close_count_clone);
          // Increment
          close_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
          format!("Close! LambdaID: {}", lambda_id)
        }),
      );
    let mock_router_server = run_http2_app(app);

    let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
      .parse()
      .unwrap();

    let pool_id_arc: PoolId = pool_id.clone().into();
    let lambda_id_arc: LambdaId = lambda_id.clone().into();

    // Declare the counts
    let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let last_active_intial = time::current_time_millis();
    let last_active = Arc::new(std::sync::atomic::AtomicU64::new(last_active_intial));
    let requests_in_flight_initial = 3;
    let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(
      requests_in_flight_initial,
    ));
    let cancel_token = tokio_util::sync::CancellationToken::new();
    let deadline_ms = time::current_time_millis() + 30000;

    // Spawn a task to flip the cancel token in 1 second
    let cancel_token_clone = cancel_token.clone();
    tokio::spawn(async move {
      tokio::time::sleep(Duration::from_millis(5500)).await;
      cancel_token_clone.cancel();
    });

    // Act
    let result = send_ping_requests(
      Arc::clone(&last_active),
      Arc::clone(&goaway_received),
      create_router_client(),
      Arc::clone(&pool_id_arc),
      Arc::clone(&lambda_id_arc),
      Arc::new(std::sync::atomic::AtomicUsize::new(0)), // count is only used in log messages
      router_endpoint,
      deadline_ms,
      cancel_token,
      Arc::clone(&requests_in_flight),
    )
    .await;

    // Assert
    assert_eq!(result, expected_result, "result expected");
    assert_eq!(
      ping_count.load(std::sync::atomic::Ordering::SeqCst),
      1,
      "ping count should be 1"
    );
    assert_eq!(
      close_count.load(std::sync::atomic::Ordering::SeqCst),
      0,
      "close count should be 0"
    );
    assert_eq!(
      requests_in_flight.load(std::sync::atomic::Ordering::SeqCst),
      requests_in_flight_initial,
      "requests in flight"
    );
    assert_eq!(
      last_active.load(std::sync::atomic::Ordering::SeqCst),
      last_active_intial,
      "last active should not be 0"
    );
    assert_eq!(
      goaway_received.load(std::sync::atomic::Ordering::SeqCst),
      true,
      "goaway received should be true"
    );
  }

  #[tokio::test]
  async fn test_ping_channel_connection_closed() {
    let lambda_id = "lambda_id".to_string();
    let pool_id = "pool_id".to_string();

    // Start router server
    // Use an arc int to count how many times the request endpoint was called
    let ping_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let ping_count_clone = Arc::clone(&ping_count);
    let close_count = Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let close_count_clone = Arc::clone(&close_count);
    let app = Router::new()
      .route(
        "/api/chunked/ping/:lambda_id",
        get(|Path(_lambda_id): Path<String>| async move {
          let ping_count = Arc::clone(&ping_count_clone);
          // Increment
          ping_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
          panic!("Connection closed")
        }),
      )
      .route(
        "/api/chunked/close/:lambda_id",
        get(|Path(lambda_id): Path<String>| async move {
          let close_count = Arc::clone(&close_count_clone);
          // Increment
          close_count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
          format!("Close! LambdaID: {}", lambda_id)
        }),
      );
    let mock_router_server = run_http2_app(app);

    let router_endpoint: Endpoint = format!("http://localhost:{}", mock_router_server.addr.port())
      .parse()
      .unwrap();

    let pool_id_arc: PoolId = pool_id.clone().into();
    let lambda_id_arc: LambdaId = lambda_id.clone().into();

    // Declare the counts
    let goaway_received = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let last_active_intial = time::current_time_millis();
    let last_active = Arc::new(std::sync::atomic::AtomicU64::new(last_active_intial));
    let requests_in_flight_initial = 3;
    let requests_in_flight = Arc::new(std::sync::atomic::AtomicUsize::new(
      requests_in_flight_initial,
    ));
    let cancel_token = tokio_util::sync::CancellationToken::new();
    let deadline_ms = time::current_time_millis() + 30000;

    let router_client = create_router_client();

    // Act
    let result = send_ping_requests(
      Arc::clone(&last_active),
      Arc::clone(&goaway_received),
      router_client.clone(),
      Arc::clone(&pool_id_arc),
      Arc::clone(&lambda_id_arc),
      Arc::new(std::sync::atomic::AtomicUsize::new(0)), // count is only used in log messages
      router_endpoint,
      deadline_ms,
      cancel_token,
      Arc::clone(&requests_in_flight),
    )
    .await;

    // Assert
    assert_eq!(
      result,
      Some(PingResult::ConnectionError),
      "result should be ConnectionError"
    );
    assert_eq!(
      ping_count.load(std::sync::atomic::Ordering::SeqCst),
      1,
      "ping count should be 1"
    );
    assert_eq!(
      close_count.load(std::sync::atomic::Ordering::SeqCst),
      0,
      "close count should be 0"
    );
    assert_eq!(
      requests_in_flight.load(std::sync::atomic::Ordering::SeqCst),
      requests_in_flight_initial,
      "requests in flight"
    );
    assert_eq!(
      last_active.load(std::sync::atomic::Ordering::SeqCst),
      last_active_intial,
      "last active should not be 0"
    );
    assert_eq!(
      goaway_received.load(std::sync::atomic::Ordering::SeqCst),
      true,
      "goaway received should be true"
    );
  }
}
