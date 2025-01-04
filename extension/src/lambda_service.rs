use std::{
  pin::Pin,
  sync::{
    atomic::{AtomicBool, Ordering},
    Arc,
  },
  time::Duration,
};

use futures::Future;
use hyper::Uri;
use lambda_runtime::LambdaEvent;
use tokio::time::timeout;
use tower::Service;

use crate::messages::ExitReason;
use crate::options::Options;
use crate::prelude::*;
use crate::time::current_time_millis;
use crate::{
  app_client::AppClient,
  endpoint::{Endpoint, Scheme},
};
use crate::{
  app_start,
  messages::{WaiterRequest, WaiterResponse},
};
use crate::{lambda_request::LambdaRequest, router_client::RouterClient};
use crate::{lambda_request_error::LambdaRequestError, router_client::create_router_client};

#[derive(Clone)]
pub struct LambdaService {
  options: Options,
  initialized: Arc<AtomicBool>,
  healthcheck_url: Uri,
  app_client: AppClient,
  router_client: RouterClient,
}

impl LambdaService {
  pub fn new(
    options: Options,
    initialized: Arc<AtomicBool>,
    healthcheck_url: Uri,
    app_client: AppClient,
  ) -> Self {
    LambdaService {
      options,
      initialized,
      healthcheck_url,
      app_client,
      router_client: create_router_client(),
    }
  }

  //
  // This is called by the Tower.Service trait impl below
  //
  pub async fn fetch_response(
    &self,
    event: LambdaEvent<WaiterRequest>,
  ) -> Result<WaiterResponse, LambdaRequestError> {
    let start_time = current_time_millis();
    let payload = event.payload.clone();
    let request = match event.payload.validate() {
      Ok(request) => request,
      Err(_) => {
        let mut resp = WaiterResponse::new(
          payload.pool_id.unwrap_or("default".to_string()),
          payload.id.to_string(),
        );
        resp.exit_reason = ExitReason::RouterLambdaInvokeInvalid;
        return Ok(resp);
      }
    };
    let channel_count: u8 = request.number_of_channels;
    let mut resp = WaiterResponse::new(request.pool_id.to_string(), request.lambda_id.to_string());

    log::info!(
      "PoolId: {}, LambdaId: {} - Received request",
      request.pool_id,
      request.lambda_id
    );

    let app_endpoint = Endpoint::new(Scheme::Http, "127.0.0.1", self.options.port);

    if !self.initialized.load(Ordering::SeqCst) {
      let fake_goaway_received = Arc::new(AtomicBool::new(false));
      let result = timeout(
        self.options.async_init_timeout,
        app_start::health_check_contained_app(
          Arc::clone(&fake_goaway_received),
          &self.healthcheck_url,
          &self.app_client,
        ),
      )
      .await;

      match result {
        Ok(success) => {
          if !success {
            log::error!(
              "PoolId: {}, LambdaId: {} - Async init Health check returned false before timeout, bailing",
              request.pool_id,
              request.lambda_id
            );

            // goaway_received is a private var above, it can't be set to false
            // so this can never happen
            return Err(LambdaRequestError::AppConnectionUnreachable);
          }
          self.initialized.store(success, Ordering::SeqCst);
        }
        Err(_) => {
          log::error!(
            "PoolId: {}, LambdaId: {} - Async init Health check not ready before timeout, bailing",
            request.pool_id,
            request.lambda_id
          );

          // Set goaway_received to true to the loop will exit
          fake_goaway_received.store(true, Ordering::SeqCst);

          // We'll panic in the tower service if this happens
          return Err(LambdaRequestError::AppConnectionUnreachable);
        }
      }

      self.initialized.store(true, Ordering::SeqCst);
    }

    if request.init_only {
      log::debug!(
        "PoolId: {}, LambdaId: {} - Returning from init-only request",
        request.pool_id,
        request.lambda_id
      );
      resp.invoke_duration = current_time_millis() - start_time;
      resp.exit_reason = ExitReason::SelfInitOnly;
      return Ok(resp);
    }

    // If the sent_time is more than 5 seconds old, just return
    // This is mostly needed locally where requests get stuck in the queue
    // Do not do this in a deployed env because an app that takes > 5 seconds to start
    // will get much longer initial request times
    if self.options.local_env
      && request.sent_time.timestamp_millis() < (current_time_millis() - 5000).try_into().unwrap()
    {
      log::error!(
        "PoolId: {}, LambdaId: {} - Returning from stale Invoke",
        request.pool_id,
        request.lambda_id
      );
      resp.invoke_duration = current_time_millis() - start_time;
      resp.exit_reason = ExitReason::SelfStaleRequest;
      return Ok(resp);
    }

    log::info!(
      "PoolId: {}, LambdaId: {}, Timeout: {}s - Invoked",
      request.pool_id,
      request.lambda_id,
      (event.context.deadline - current_time_millis()) / 1000
    );
    let mut deadline_ms = event.context.deadline;
    if (deadline_ms - current_time_millis()) > 15 * 60 * 1000 {
      log::warn!("Deadline is greater than 15 minutes, trimming to 1 minute");
      deadline_ms = current_time_millis() + 60 * 1000;
    }
    // check if env var is set to force deadline for testing
    if let Some(force_deadline_secs) = self.options.force_deadline_secs {
      if force_deadline_secs > Duration::from_secs(0) {
        log::warn!(
          "Forcing deadline to {} seconds",
          force_deadline_secs.as_secs()
        );
        deadline_ms = current_time_millis() + force_deadline_secs.as_millis() as u64;
      }
    }

    // run until we get a GoAway or deadline is about to be reached
    let mut lambda_request = LambdaRequest::new(
      app_endpoint,
      self.options.compression,
      Arc::clone(&request.pool_id),
      Arc::clone(&request.lambda_id),
      channel_count,
      request.router_endpoint,
      deadline_ms,
      self.options.last_active_grace_period_ms,
    );

    //
    // This is the main loop that runs until the deadline is about to be reached
    //
    let result = lambda_request
      .start(self.app_client.clone(), self.router_client.clone())
      .await;
    match result {
      Ok(exit_reason) => {
        log::info!(
          "PoolId: {}, LambdaId: {} - Lambda request completed, exit reason: {:?}",
          request.pool_id,
          request.lambda_id,
          exit_reason
        );

        resp.exit_reason = resp.exit_reason.worse(exit_reason);
      }
      Err(err) => {
        log::error!(
          "PoolId: {}, LambdaId: {} - Lambda request failed: {}",
          request.pool_id,
          request.lambda_id,
          err
        );

        if err.is_fatal() {
          log::error!(
            "PoolId: {}, LambdaId: {} - FATAL ERROR - Exiting",
            request.pool_id,
            request.lambda_id
          );
          return Err(err);
        } else {
          // Set the exit reason for non-fatal errors
          resp.exit_reason = resp.exit_reason.worse(err.into());
        }
      }
    }

    // Print final stats
    log::info!(
      "LambdaId: {}, Requests: {}, Elapsed: {} ms, RPS: {:.1} - Returning from Invoke",
      request.lambda_id,
      lambda_request.count.load(Ordering::Acquire),
      lambda_request.elapsed(),
      lambda_request.rps()
    );

    resp.invoke_duration = current_time_millis() - start_time;
    resp.request_count = lambda_request.count.load(Ordering::Acquire) as u64;
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
        // We only get here if the error is fatal
        Err(e) => {
          log::error!("Fatal error in LambdaService.call: {}", e);
          Err(e.into())
        }
      }
    })
  }
}
