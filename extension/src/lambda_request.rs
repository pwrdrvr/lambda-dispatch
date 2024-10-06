use rand::Rng;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::Arc;
use tokio::task::JoinHandle;

use rand::SeedableRng;
use tokio::{
  io::{AsyncRead, AsyncWrite},
  net::TcpStream,
};
use tokio_rustls::client::TlsStream;

use crate::app_client::AppClient;
use crate::endpoint::Endpoint;
use crate::lambda_request_error::LambdaRequestError;
use crate::messages::{self, ExitReason};
use crate::ping::{self, send_close_request, PingResult};
use crate::prelude::*;
use crate::router_channel::RouterChannel;
use crate::router_client::RouterClient;
use crate::time::current_time_millis;

// Define a Stream trait that both TlsStream and TcpStream implement
pub trait Stream: AsyncRead + AsyncWrite + Send {}
impl Stream for TlsStream<TcpStream> {}
impl Stream for TcpStream {}

/// A `LambdaRequest` handles connecting back to the router, picking up requests, sending ping
/// requests to the router, and sending the requests to the contained app When an invoke completes
/// this is torn down completely
#[derive(Debug, Clone)]
pub struct LambdaRequest {
  app_endpoint: Endpoint,
  compression: bool,
  pool_id: PoolId,
  lambda_id: LambdaId,
  channel_count: u8,
  router_endpoint: Endpoint,
  cancel_token: tokio_util::sync::CancellationToken,
  deadline_ms: u64,
  goaway_received: Arc<AtomicBool>,
  last_active: Arc<AtomicU64>,
  rng: rand::rngs::StdRng,
  requests_in_flight: Arc<AtomicUsize>,
  pub count: Arc<AtomicUsize>,
  start_time: u64,
  last_active_grace_period_ms: u64,
}

impl LambdaRequest {
  /// Create a new `LambdaRequest` task with a specified deadline.
  ///
  /// # Parameters
  ///
  /// * `deadline_ms`: A timestamp in milliseconds since the Unix epoch representing when the
  /// Lambda function needs to finish execution.
  pub fn new(
    app_endpoint: Endpoint,
    compression: bool,
    pool_id: PoolId,
    lambda_id: LambdaId,
    channel_count: u8,
    router_endpoint: Endpoint,
    deadline_ms: u64,
    last_active_grace_period_ms: u64,
  ) -> Self {
    LambdaRequest {
      count: Arc::new(AtomicUsize::new(0)),
      app_endpoint,
      compression,
      pool_id,
      lambda_id,
      channel_count,
      router_endpoint,
      cancel_token: tokio_util::sync::CancellationToken::new(),
      deadline_ms,
      goaway_received: Arc::new(AtomicBool::new(false)),
      last_active: Arc::new(AtomicU64::new(0)),
      rng: rand::rngs::StdRng::from_entropy(),
      requests_in_flight: Arc::new(AtomicUsize::new(0)),
      start_time: current_time_millis(),
      last_active_grace_period_ms,
    }
  }

  /// Executes a task with a specified deadline.
  pub async fn start(
    &mut self,
    app_client: AppClient,
    router_client: RouterClient,
  ) -> Result<messages::ExitReason, LambdaRequestError> {
    // Send the ping requests in background
    let mut ping_task = Some(tokio::task::spawn(ping::send_ping_requests(
      Arc::clone(&self.last_active),
      Arc::clone(&self.goaway_received),
      router_client.clone(),
      Arc::clone(&self.pool_id),
      Arc::clone(&self.lambda_id),
      Arc::clone(&self.count),
      self.router_endpoint.clone(),
      self.deadline_ms,
      self.cancel_token.clone(),
      Arc::clone(&self.requests_in_flight),
      self.last_active_grace_period_ms,
    )));

    // Startup the request channels
    let channel_futures = (0..self.channel_count)
      .map(|channel_number| {
        let router_endpoint_url = self.router_endpoint.url().clone();
        let lambda_id = Arc::clone(&self.lambda_id);
        let last_active = Arc::clone(&self.last_active);
        // Create a JoinHandle and implicitly return it to be collected in the vector
        let mut router_channel = RouterChannel::new(
          Arc::clone(&self.count),
          self.compression,
          Arc::clone(&self.goaway_received),
          Arc::clone(&last_active),
          Arc::clone(&self.requests_in_flight),
          self.router_endpoint.clone(),
          self.app_endpoint.clone(),
          channel_number,
          Arc::clone(&self.pool_id),
          Arc::clone(&self.lambda_id),
          uuid::Builder::from_random_bytes(self.rng.gen())
            .into_uuid()
            .to_string(),
        );
        let goaway_received = Arc::clone(&self.goaway_received);

        let app_client = app_client.clone();
        let router_client = router_client.clone();
        tokio::spawn(async move {
          let result = router_channel
            .start(app_client.clone(), router_client.clone())
            .await;

          // If we failed to connect to the router we should log our IP, router IP, port and protocol
          if let Err(LambdaRequestError::RouterUnreachable) = result {
            log::error!(
              "LambdaId: {} - start - Failed to connect to router: {}",
              lambda_id,
              router_endpoint_url
            );
          }

          // Tell the other channels to stop
          goaway_received.store(true, Ordering::Release);

          result
        })
      })
      .collect::<Vec<_>>();

    let mut exit_reason = messages::ExitReason::RouterGoAway;

    // `try_join_all` says all futures will be immediately canceled if one of them returns an error
    // However, this "cancelation" is cooperative and has to be checked by the tasks themselves
    // As a result, this just waits for all tasks to complete
    // In addition, `try_join_all` says it will return an error when a future returns an error,
    // but it's returning `Ok` even when there are `Err` in the vector (this may be because
    // try_join_all is looking for an error on the future not the task?)
    match futures::future::try_join_all(channel_futures).await {
      Ok(results) => {
        // All tasks completed successfully
        log::debug!(
          "LambdaId: {} - run - All channel tasks completed successfully",
          self.lambda_id
        );

        for result in results {
          match result {
            Ok(result) => {
              if let Some(result) = result {
                exit_reason = exit_reason.worse(result.into());
              }
            }
            Err(err) => {
              log::error!(
                "LambdaId: {} - run - Error in channel task: {:?}",
                self.lambda_id,
                err
              );

              if err.is_fatal() {
                // Try to clean up the ping task
                self.cancel_token.cancel();
                let _ = tokio::time::timeout(
                  std::time::Duration::from_secs(1),
                  ping_task.take().unwrap(),
                )
                .await;

                return Err(err);
              }

              // Error is not fatal so just use it as an exit reason
              exit_reason = exit_reason.worse(err.into());
            }
          }
        }
      }
      Err(err) => {
        // try_join_all returns JoinError when one of the futures panics
        // If a future has panicked we are probably in a bad state and should exit
        log::error!(
          "LambdaId: {} - run - Error in futures::future::try_join_all: {:?}",
          self.lambda_id,
          err
        );

        // If we got here we had a panic so we need to ask the router to
        // shut down this Lambda invoke
        let _ = send_close_request(
          Arc::clone(&self.goaway_received),
          router_client,
          Arc::clone(&self.pool_id),
          Arc::clone(&self.lambda_id),
          self.router_endpoint.clone(),
        )
        .await;

        // Set the goaway signal so other tasks stop
        self.goaway_received.store(true, Ordering::Release);

        // Try to clean up the ping task
        self.cancel_token.cancel();
        let _ =
          tokio::time::timeout(std::time::Duration::from_secs(1), ping_task.take().unwrap()).await;

        // Shutdown the ping loop
        // Note: we don't really care what happens here because
        // we're already in a fatal error state
        let _ = self
          .wait_for_ping_loop(ping_task.take().unwrap(), exit_reason)
          .await;

        // This is a bit of a lie: we don't know if the app connection is unreachable
        // but we do know we're in an non-deteministic state and that this
        // Lambda should exit and be recreated
        return Err(LambdaRequestError::AppConnectionUnreachable);
      }
    }

    // Shutdown the ping loop
    exit_reason = self
      .wait_for_ping_loop(ping_task.take().unwrap(), exit_reason)
      .await
      .unwrap_or(exit_reason)
      .worse(exit_reason);

    Ok(exit_reason)
  }

  pub async fn wait_for_ping_loop(
    &mut self,
    ping_task: JoinHandle<Option<PingResult>>,
    exit_reason: ExitReason,
  ) -> Result<ExitReason, LambdaRequestError> {
    // Tell the ping loop to stop
    self.cancel_token.cancel();

    // Wait for the ping loop to exit
    match ping_task.await {
      Ok(result) => {
        // Ping task completed successfully

        // If the ping task knows why we exited, use that reason
        if let Some(ping_result) = result {
          if let Some(ping_result) = ping_result.into() {
            return Ok(exit_reason.worse(ping_result));
          }
        }
        Ok(exit_reason)
      }
      Err(e) => {
        log::error!(
          "LambdaId: {} - run - Error in ping task: {:?}",
          self.lambda_id,
          e
        );
        // We'll lump this in as a generic router connection error
        Err(LambdaRequestError::RouterUnreachable)
      }
    }
  }

  pub fn elapsed(&self) -> u64 {
    current_time_millis() - self.start_time
  }

  pub fn rps(&self) -> f64 {
    self.count.load(Ordering::Acquire) as f64 / (self.elapsed() as f64 / 1000.0)
  }
}
