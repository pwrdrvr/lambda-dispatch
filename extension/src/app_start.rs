use crate::{app_client::AppClient, lambda_request_error::LambdaRequestError, prelude::*};

use std::sync::atomic::AtomicBool;
use std::sync::Arc;
use std::time::Duration;

use futures::channel::mpsc;
use http_body_util::{BodyExt, StreamBody};
use hyper::{
  body::{Bytes, Frame},
  Request, StatusCode, Uri,
};

async fn send_healthcheck(
  app_client: &AppClient,
  healthcheck_url: &Uri,
) -> Result<(), LambdaRequestError> {
  let (mut app_req_tx, app_req_recv) = mpsc::channel::<Result<Frame<Bytes>>>(32 * 1024);
  let app_req = Request::builder()
    .method("GET")
    .uri(healthcheck_url)
    .header(hyper::header::HOST, "localhost")
    .body(StreamBody::new(app_req_recv))
    .map_err(|_| LambdaRequestError::ChannelErrorOther)?;

  // Close the body - we are not sending one
  app_req_tx.close_channel();

  match app_client.request(app_req).await {
    Ok(app_res) => {
      let (parts, mut res_stream) = app_res.into_parts();

      // Rip through and discard so the response stream is closed
      while res_stream.frame().await.is_some() {}

      // Check status code and discard body
      if parts.status != StatusCode::OK {
        log::debug!("Health check - Failed: {:?}\nHeaders:", parts.status);
        for header in parts.headers.iter() {
          log::debug!("  {}: {}", header.0, header.1.to_str().unwrap());
        }

        return Err(LambdaRequestError::AppConnectionError);
      }

      log::debug!("Health check - Send request to contained app success");

      Ok(())
    }
    Err(err) => {
      if err.is_connect() {
        log::debug!("Health check - Failed to connect to contained app: {}", err);
        Err(LambdaRequestError::AppConnectionError)
      } else {
        log::debug!(
          "Health check - Failed to send request to contained app: {}",
          err
        );
        Err(LambdaRequestError::ChannelErrorOther)
      }
    }
  }
}

pub async fn health_check_contained_app(
  goaway_received: Arc<AtomicBool>,
  healthcheck_url: &Uri,
  app_client: &AppClient,
) -> bool {
  log::info!(
    "Health check - Starting for contained app at: {}",
    healthcheck_url.to_string()
  );

  while !goaway_received.load(std::sync::atomic::Ordering::Acquire) {
    tokio::time::sleep(Duration::from_millis(10)).await;

    match send_healthcheck(app_client, healthcheck_url).await {
      Ok(_) => {
        log::debug!("Health check - Complete - Success");
        return true;
      }
      Err(_) => {
        log::debug!("Health check - Failed");
      }
    }
  }

  log::info!("Health check - Complete - Goaway received");
  false
}
