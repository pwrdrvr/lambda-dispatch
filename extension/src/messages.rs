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
  #[serde(rename = "RouterGoaway")]
  RouterGoaway,
  #[serde(rename = "RouterStatus4xx")]
  RouterStatus4xx,
  #[serde(rename = "RouterStatus5xx")]
  RouterStatus5xx,
  #[serde(rename = "RouterStatusOther")]
  RouterStatusOther,
  #[serde(rename = "ChannelErrorOther")]
  ChannelErrorOther,
}

impl ExitReason {
  pub fn worse(self, other: Self) -> Self {
    use ExitReason::*;

    let ordered_reasons = [
      RouterLambdaInvokeInvalid,
      RouterUnreachable,
      RouterConnectionError,
      ChannelErrorOther,
      AppConnectionError,
      AppConnectionClosed,
      RouterStatus5xx,
      RouterStatus4xx,
      RouterStatusOther,
      RouterGoaway,
      SelfLastActive,
      SelfDeadline,
      SelfStaleRequest,
      SelfInitOnly,
    ];

    for reason in ordered_reasons {
      if self == reason || other == reason {
        return reason;
      }
    }

    self // If no match is found, return the current value
  }

  // This function will cause a compile-time error if a new variant is added to the enum
  // but not added to the match expression.
  #[allow(dead_code)]
  fn ensure_all_variants_handled(variant: Self) {
    use ExitReason::*;

    match variant {
      // HEY - If you add here you need to add to the `worse` function array above
      RouterLambdaInvokeInvalid => {}
      RouterUnreachable => {}
      RouterConnectionError => {}
      ChannelErrorOther => {}
      AppConnectionError => {}
      AppConnectionClosed => {}
      RouterStatus5xx => {}
      RouterStatus4xx => {}
      RouterStatusOther => {}
      RouterGoaway => {}
      SelfLastActive => {}
      SelfDeadline => {}
      SelfStaleRequest => {}
      SelfInitOnly => {}
    }
    // HEY - If you add here you need to add to the `worse` function array above
  }
}

impl WaiterResponse {
  pub fn new(pool_id: String, id: String) -> Self {
    Self {
      pool_id,
      id,
      request_count: 0,
      invoke_duration: 0,
      exit_reason: ExitReason::RouterGoaway,
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

#[cfg(test)]
mod tests {
  use super::*;
  use serde_json::json;

  #[test]
  fn test_worse() {
    assert_eq!(
      ExitReason::SelfDeadline.worse(ExitReason::RouterUnreachable),
      ExitReason::RouterUnreachable
    );
    assert_eq!(
      ExitReason::RouterUnreachable.worse(ExitReason::SelfDeadline),
      ExitReason::RouterUnreachable
    );
    assert_eq!(
      ExitReason::SelfDeadline.worse(ExitReason::SelfStaleRequest),
      ExitReason::SelfDeadline
    );
    assert_eq!(
      ExitReason::SelfStaleRequest.worse(ExitReason::SelfDeadline),
      ExitReason::SelfDeadline
    );
  }

  #[tokio::test]
  async fn test_waiter_request_deserialization() {
    let data = json!({
        "PoolId": "pool1",
        "Id": "id1",
        "DispatcherUrl": "http://localhost:8000",
        "NumberOfChannels": 5,
        "SentTime": "2022-01-01T00:00:00Z",
        "InitOnly": true
    });

    let request: WaiterRequest = serde_json::from_value(data).unwrap();

    assert_eq!(request.pool_id, Some("pool1".to_string()));
    assert_eq!(request.id, "id1");
    assert_eq!(request.router_url, "http://localhost:8000");
    assert_eq!(request.number_of_channels, 5);
    assert_eq!(request.sent_time, "2022-01-01T00:00:00Z");
    assert_eq!(request.init_only, true);
  }

  #[tokio::test]
  async fn test_waiter_response_serialization() {
    let response = WaiterResponse {
      pool_id: "pool1".to_string(),
      id: "id1".to_string(),
      request_count: 10,
      invoke_duration: 20,
      exit_reason: ExitReason::SelfDeadline,
    };

    let data = serde_json::to_value(&response).unwrap();

    assert_eq!(
      data,
      json!({
          "PoolId": "pool1",
          "Id": "id1",
          "RequestCount": 10,
          "InvokeDuration": 20,
          "ExitReason": "SelfDeadline"
      })
    );
  }

  #[test]
  fn test_waiter_request_debug() {
    let request = WaiterRequest {
      pool_id: Some("pool1".to_string()),
      id: "id1".to_string(),
      router_url: "http://localhost:8000".to_string(),
      number_of_channels: 5,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: true,
    };

    assert_eq!(
            format!("{:?}", request),
            "WaiterRequest { pool_id: Some(\"pool1\"), id: \"id1\", router_url: \"http://localhost:8000\", number_of_channels: 5, sent_time: \"2022-01-01T00:00:00Z\", init_only: true }"
        );
  }

  #[test]
  fn test_waiter_response_debug() {
    let response = WaiterResponse {
      pool_id: "pool1".to_string(),
      id: "id1".to_string(),
      request_count: 10,
      invoke_duration: 20,
      exit_reason: ExitReason::SelfDeadline,
    };

    assert_eq!(
      format!("{:?}", response),
      "WaiterResponse { pool_id: \"pool1\", id: \"id1\", request_count: 10, invoke_duration: 20, exit_reason: SelfDeadline }"
    );
  }

  #[test]
  fn test_validate_success() {
    let request = WaiterRequest {
      pool_id: Some("pool1".to_string()),
      id: "id1".to_string(),
      router_url: "http://example.com".to_string(),
      number_of_channels: 5,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: false,
    };

    let validated_request = request.validate().unwrap();

    assert_eq!(
      validated_request.router_url,
      "http://example.com".parse::<Uri>().unwrap()
    );
    assert_eq!(
      validated_request.sent_time,
      DateTime::parse_from_rfc3339("2022-01-01T00:00:00Z")
        .unwrap()
        .with_timezone(&Utc)
    );
  }

  #[test]
  fn test_validate_invalid_router_url() {
    let request = WaiterRequest {
      pool_id: Some("pool1".to_string()),
      id: "id1".to_string(),
      router_url: "invalid_url".to_string(),
      number_of_channels: 5,
      sent_time: "2022-01-01T00:00:00Z".to_string(),
      init_only: false,
    };

    let result = request.validate();

    assert!(matches!(result, Err(ValidationError::InvalidRouterUrl)));
  }

  #[test]
  fn test_validate_invalid_sent_time() {
    let request = WaiterRequest {
      pool_id: Some("pool1".to_string()),
      id: "id1".to_string(),
      router_url: "http://example.com".to_string(),
      number_of_channels: 5,
      sent_time: "invalid_time".to_string(),
      init_only: false,
    };

    let result = request.validate();

    assert!(matches!(result, Err(ValidationError::InvalidSentTime)));
  }
}
