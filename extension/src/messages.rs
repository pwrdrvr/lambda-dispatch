use crate::prelude::*;

use chrono::DateTime;
use chrono::Utc;
use hyper::Uri;
use serde::Deserialize;
use serde::Serialize;

use crate::endpoint::Endpoint;

#[derive(Deserialize, Debug, Clone)]
pub struct WaiterRequest {
  #[serde(rename = "PoolId")]
  pub pool_id: Option<String>,
  #[serde(rename = "Id")]
  pub id: String,
  #[serde(rename = "DispatcherUrl")]
  pub router_url: String,
  #[serde(rename = "NumberOfChannels")]
  pub number_of_channels: u8,
  #[serde(rename = "SentTime")]
  pub sent_time: String,
  #[serde(rename = "InitOnly", default)]
  pub init_only: bool,
}

#[derive(Serialize, Debug, PartialEq)]
pub struct WaiterResponse {
  #[serde(rename = "PoolId")]
  pub pool_id: String,
  #[serde(rename = "Id")]
  pub id: String,
  #[serde(rename = "RequestCount")]
  pub request_count: u64, // Assuming it's an unsigned integer
  #[serde(rename = "InvokeDuration")]
  pub invoke_duration: u64, // Assuming it's an unsigned integer
  #[serde(rename = "ExitReason")]
  pub exit_reason: ExitReason,
}

#[derive(Serialize, Debug, PartialEq, Copy, Clone)]
pub enum ExitReason {
  #[serde(rename = "SelfDeadline")]
  SelfDeadline,
  #[serde(rename = "SelfLastActive")]
  SelfLastActive,
  #[serde(rename = "SelfInitOnly")]
  SelfInitOnly,
  #[serde(rename = "SelfStaleRequest")]
  SelfStaleRequest,
  #[serde(rename = "AppConnectionError")]
  AppConnectionError,
  #[serde(rename = "AppConnectionClosed")]
  AppConnectionClosed,
  #[serde(rename = "RouterLambdaInvokeInvalid")]
  RouterLambdaInvokeInvalid,
  #[serde(rename = "RouterConnectionError")]
  RouterConnectionError,
  #[serde(rename = "RouterUnreachable")]
  RouterUnreachable,
  #[serde(rename = "RouterGoAway")]
  RouterGoAway,
  #[serde(rename = "RouterStatus4xx")]
  RouterStatus4xx,
  #[serde(rename = "RouterStatus5xx")]
  RouterStatus5xx,
  #[serde(rename = "RouterStatusOther")]
  RouterStatusOther,
  #[serde(rename = "ChannelErrorOther")]
  ChannelErrorOther,
}

impl Severity for ExitReason {
  fn severity(&self) -> usize {
    match self {
      Self::RouterLambdaInvokeInvalid => 0,
      Self::RouterUnreachable => 1,
      Self::RouterConnectionError => 2,
      Self::ChannelErrorOther => 3,
      Self::AppConnectionError => 4,
      Self::AppConnectionClosed => 5,
      Self::RouterStatus5xx => 6,
      Self::RouterStatus4xx => 7,
      Self::RouterStatusOther => 8,
      Self::RouterGoAway => 9,
      Self::SelfLastActive => 10,
      Self::SelfDeadline => 11,
      Self::SelfStaleRequest => 12,
      Self::SelfInitOnly => 13,
    }
  }
}

impl WaiterResponse {
  pub fn new(pool_id: String, id: String) -> Self {
    Self {
      pool_id,
      id,
      request_count: 0,
      invoke_duration: 0,
      exit_reason: ExitReason::RouterGoAway,
    }
  }
}

impl WaiterRequest {
  pub fn validate(self) -> Result<ValidatedWaiterRequest, ValidationError> {
    let router_url = self
      .router_url
      .parse::<Uri>()
      .map_err(|_| ValidationError::InvalidRouterUrl)?;

    let router_endpoint =
      Endpoint::try_from(&router_url).map_err(|_| ValidationError::InvalidRouterUrl)?;

    let sent_time = DateTime::parse_from_rfc3339(&self.sent_time)
      .map_err(|_| ValidationError::InvalidSentTime)?
      .with_timezone(&Utc);
    Ok(ValidatedWaiterRequest {
      pool_id: self.pool_id.unwrap_or_else(|| "default".to_string()).into(),
      lambda_id: self.id.into(),
      router_endpoint,
      router_url,
      number_of_channels: self.number_of_channels,
      sent_time,
      init_only: self.init_only,
    })
  }
}

#[derive(Debug)]
pub enum ValidationError {
  InvalidRouterUrl,
  InvalidSentTime,
}

#[derive(Debug)]
pub struct ValidatedWaiterRequest {
  pub pool_id: PoolId,
  pub lambda_id: LambdaId,
  pub router_url: Uri,
  pub router_endpoint: Endpoint,
  pub number_of_channels: u8,
  pub sent_time: DateTime<Utc>,
  pub init_only: bool,
}
