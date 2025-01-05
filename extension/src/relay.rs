use crate::lambda_request_error::LambdaRequestError;
use crate::prelude::*;

use std::{
  io::Write,
  pin::Pin,
  sync::{atomic::AtomicUsize, Arc},
};

use bytes::{buf::Writer, BytesMut};
use futures::{channel::mpsc::Sender, SinkExt};
use hyper::body::{Body, Bytes, Frame, Incoming};

use flate2::write::GzEncoder;

macro_rules! log_debug {
  ($debug:expr, $($arg:tt)*) => {
      if $debug {
          log::info!($($arg)*);
      } else {
          log::debug!($($arg)*);
      }
  };
}

pub async fn relay_request_to_app(
  left_over_buf: Vec<u8>,
  pool_id: PoolId,
  lambda_id: LambdaId,
  channel_id: ChannelId,
  requests_in_flight: Arc<AtomicUsize>,
  mut app_req_tx: Sender<Result<Frame<Bytes>, Error>>,
  mut router_response_stream: Incoming,
  debug_mode: bool,
  app_url: String,
) -> Result<usize, LambdaRequestError> {
  let mut bytes_sent = 0;

  // Send any overflow body bytes to the contained app
  if !left_over_buf.is_empty() {
    bytes_sent += left_over_buf.len();
    log_debug!(
      debug_mode,
      "{} PoolId: {}, LambdaId: {}, ChannelId: {} - Sending left over bytes to contained app: {:?}",
      app_url,
      pool_id,
      lambda_id,
      channel_id,
      left_over_buf.len(),
    );
    app_req_tx
      .send(Ok(Frame::data(left_over_buf.into())))
      .await
      .map_err(|_| LambdaRequestError::AppConnectionError)?;
  }

  //
  // Handle incoming POST request by relaying the body
  //
  // Reads from: Router response body stream (containing request from client)
  // Writes to: App request body stream
  // Source: router_res_stream
  // Sink: app_req_tx
  while let Some(chunk) =
    futures::future::poll_fn(|cx| Incoming::poll_frame(Pin::new(&mut router_response_stream), cx))
      .await
  {
    let chunk = match chunk {
      Ok(value) => value,
      Err(_) => {
        log::error!("{} LambdaId: {}, ChannelId: {}, Reqs in Flight: {}, BytesSent: {} - Error reading from router_res_stream: {:?}",
        app_url,
        lambda_id,
        channel_id,
        requests_in_flight.load(std::sync::atomic::Ordering::Acquire),
        bytes_sent,
        chunk.err());
        return Err(LambdaRequestError::RouterConnectionError);
      }
    };

    let chunk_data = match chunk.data_ref() {
      Some(data) => data,
      None => continue,
    };

    let chunk_len = chunk_data.len();
    // If chunk_len is zero the channel has closed
    if chunk_len == 0 {
      log_debug!(
        debug_mode,
        "{} PoolId: {}, LambdaId: {}, ChannelId: {}, BytesSent: {}, ChunkLen: {} - Request from router - Channel closed",
        app_url,
        pool_id,
        lambda_id,
        channel_id,
        bytes_sent,
        chunk_len
      );
      break;
    }
    match app_req_tx.send(Ok(chunk)).await {
      Ok(_) => {}
      Err(err) => {
        log::error!("PoolId: {}, LambdaId: {}, ChannelId: {}, Reqs in Flight: {}, BytesSent: {}, ChunkLen: {} - Error sending to app_req_tx: {}",
                            pool_id,
                            lambda_id,
                            channel_id,
                            requests_in_flight.load(std::sync::atomic::Ordering::Acquire),
                            bytes_sent,
                            chunk_len,
                            err);
        return Err(LambdaRequestError::AppConnectionError);
      }
    }
    bytes_sent += chunk_len;

    log_debug!(
      debug_mode,
      "{} PoolId: {}, LambdaId: {}, ChannelId: {}, BytesSent: {}, ChunkLen: {} - Sent bytes to contained app",
      app_url,
      pool_id,
      lambda_id,
      channel_id,
      bytes_sent,
      chunk_len
    );
  }

  // This may error if the router closed the connection
  app_req_tx
    .flush()
    .await
    .map_err(|_| LambdaRequestError::RouterConnectionError)?;
  app_req_tx
    .close()
    .await
    .map_err(|_| LambdaRequestError::RouterConnectionError)?;

  log_debug!(
    debug_mode,
    "{} PoolId: {}, LambdaId: {}, ChannelId: {}, BytesSent: {} - Request from router - Completed",
    app_url,
    pool_id,
    lambda_id,
    channel_id,
    bytes_sent
  );

  Ok(bytes_sent)
}

/// Reads from: App response body stream
/// Writes to: Router request body stream
pub async fn relay_response_to_router(
  pool_id: PoolId,
  lambda_id: LambdaId,
  channel_id: ChannelId,
  requests_in_flight: Arc<AtomicUsize>,
  mut app_res_stream: Incoming,
  mut encoder: Option<GzEncoder<Writer<BytesMut>>>,
  mut tx: Sender<Result<Frame<Bytes>, Error>>,
  debug_mode: bool,
  app_url: String,
) -> Result<usize, LambdaRequestError> {
  let mut bytes_read = 0;
  while let Some(chunk) =
    futures::future::poll_fn(|cx| Incoming::poll_frame(Pin::new(&mut app_res_stream), cx)).await
  {
    let chunk = match chunk {
      Ok(value) => value,
      Err(_) => {
        log::error!("PoolId: {}, LambdaId: {}, ChannelId: {}, Reqs in Flight: {}, BytesRead: {} - Error reading from app_res_stream: {:?}",
              pool_id,
              lambda_id,
              channel_id,
              requests_in_flight.load(std::sync::atomic::Ordering::Acquire),
              bytes_read,
              chunk.err());
        return Err(LambdaRequestError::AppConnectionError);
      }
    };

    let chunk_data = match chunk.data_ref() {
      Some(data) => data,
      None => continue,
    };

    let chunk_len = chunk_data.len();
    // If chunk_len is zero the response
    if chunk_len == 0 {
      log_debug!(
        debug_mode,
        "{} PoolId: {}, LambdaId: {}, ChannelId: {}, BytesRead: {}, ChunkLen: {} - Response from app - Channel closed",
        app_url,
        pool_id,
        lambda_id,
        channel_id,
        bytes_read,
        chunk_len
      );
      break;
    }

    bytes_read += chunk_len;

    if let Some(ref mut encoder) = encoder {
      encoder
        .write_all(chunk_data)
        .map_err(|_| LambdaRequestError::RouterConnectionError)?;
      encoder
        .flush()
        .map_err(|_| LambdaRequestError::RouterConnectionError)?;

      let writer = encoder.get_mut();
      let bytes = writer.get_mut().split().into();

      match tx.send(Ok(Frame::data(bytes))).await {
        Ok(_) => {
          log_debug!(
            debug_mode,
            "{} PoolId: {}, LambdaId: {}, ChannelId: {}, Reqs in Flight: {}, BytesSent: {} - Sent bytes to router using encoder",
            app_url,
            pool_id,
            lambda_id,
            channel_id,
            requests_in_flight.load(std::sync::atomic::Ordering::Acquire),
            bytes_read
          );
        }
        Err(err) => {
          log::error!("PoolId: {}, LambdaId: {}, ChannelId: {}, Reqs in Flight: {}, BytesRead: {} - Error sending to tx: {}",
                            pool_id,
                            lambda_id,
                            channel_id,
                            requests_in_flight.load(std::sync::atomic::Ordering::Acquire),
                            bytes_read,
                            err);
          return Err(LambdaRequestError::RouterConnectionError);
        }
      }
    } else {
      match tx.send(Ok(chunk)).await {
        Ok(_) => {
          log_debug!(
            debug_mode,
            "{} PoolId: {}, LambdaId: {}, ChannelId: {}, Reqs in Flight: {}, BytesSent: {} - Sent bytes to router without encoder",
            app_url,
            pool_id,
            lambda_id,
            channel_id,
            requests_in_flight.load(std::sync::atomic::Ordering::Acquire),
            bytes_read
          );
        }
        Err(err) => {
          log::error!("PoolId: {}, LambdaId: {}, ChannelId: {}, Reqs in Flight: {}, BytesRead: {} - Error sending to tx: {}",
                            pool_id,
                            lambda_id,
                            channel_id,
                            requests_in_flight.load(std::sync::atomic::Ordering::Acquire),
                            bytes_read,
                            err);
          return Err(LambdaRequestError::RouterConnectionError);
        }
      }
    }
  }

  if let Some(encoder) = encoder.take() {
    let writer = encoder
      .finish()
      .map_err(|_| LambdaRequestError::RouterConnectionError)?;
    let bytes = writer.into_inner().into();

    tx.send(Ok(Frame::data(bytes)))
      .await
      .map_err(|_| LambdaRequestError::RouterConnectionError)?;
  }
  tx.flush()
    .await
    .map_err(|_| LambdaRequestError::RouterConnectionError)?;
  tx.close()
    .await
    .map_err(|_| LambdaRequestError::RouterConnectionError)?;

  log_debug!(
    debug_mode,
    "{} PoolId: {}, LambdaId: {}, ChannelId: {}, BytesSent: {} - Response from app - Completed",
    app_url,
    pool_id,
    lambda_id,
    channel_id,
    bytes_read
  );

  Ok(bytes_read)
}
