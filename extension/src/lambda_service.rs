use std::sync::atomic::{AtomicBool, Ordering};
use std::{pin::Pin, sync::Arc};

use futures::Future;
use hyper::Uri;
use lambda_runtime::LambdaEvent;
use tokio::io::{AsyncRead, AsyncWrite};
use tokio::net::TcpStream;
use tower::Service;

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
    log::info!("LambdaId: {} - Received request", event.payload.id);
    let schema = "http";

    let domain: Uri = format!("{}://{}:{}", schema, "127.0.0.1", self.options.port)
      .parse()
      .unwrap();

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

    // extract some useful info from the request
    let lambda_id: LambdaId = event.payload.id.into();
    let channel_count: u8 = event.payload.number_of_channels;
    let dispatcher_url = event.payload.dispatcher_url;

    // prepare the response
    let resp = WaiterResponse {
      id: lambda_id.to_string(),
    };

    if event.payload.init_only {
      log::info!("LambdaId: {} - Returning from init-only request", lambda_id);
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
      log::info!("LambdaId: {} - Returning from stale request", lambda_id);
      return Ok(resp);
    }

    log::info!(
      "LambdaId: {}, Timeout: {}s - Invoked",
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
      domain,
      self.options.compression,
      lambda_id,
      channel_count,
      dispatcher_url.parse().unwrap(),
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
