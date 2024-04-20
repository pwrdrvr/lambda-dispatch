use std::sync::atomic::{AtomicBool, Ordering};
use std::{pin::Pin, sync::Arc};

use futures::Future;
use hyper::Uri;
use lambda_runtime::LambdaEvent;
use tokio::io::{AsyncRead, AsyncWrite};
use tokio::net::TcpStream;
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
      self.initialized.store(
        app_start::health_check_contained_app(
          Arc::new(AtomicBool::new(false)),
          &self.healthcheck_url,
        )
        .await,
        Ordering::SeqCst,
      );
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
    Box::pin(async move { adapter.fetch_response(event).await })
  }
}

#[cfg(test)]
mod tests {
  use super::*;
  use tokio::test;

  #[test]
  async fn test_fetch_response() {
    let options = Options::default();
    let initialized = true;
    let healthcheck_url: Uri = "http://localhost:8080/health".parse().unwrap();
    let service = LambdaService::new(
      options,
      Arc::new(AtomicBool::new(initialized)),
      healthcheck_url,
    );
    let request = WaiterRequest {
      pool_id: Some("test_pool".to_string()),
      id: "test_id".to_string(),
      router_url: "http://localhost:8000".to_string(),
      number_of_channels: 5,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: false,
    };
    let mut context = lambda_runtime::Context::default();
    context.deadline = current_time_millis() + 1000;
    let event = LambdaEvent {
      payload: request,
      context,
    };

    let response = service.fetch_response(event).await;

    assert!(
      response.is_ok(),
      "fetch_response failed: {:?}",
      response.err()
    );
    // Add more assertions based on your expected response
  }
}
