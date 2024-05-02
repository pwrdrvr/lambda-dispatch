#[cfg(test)]
mod tests {
  use chrono::{DateTime, Utc};
  use extension::{
    messages::{ExitReason, ValidationError, WaiterRequest, WaiterResponse},
    prelude::*,
  };
  use hyper::Uri;
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
