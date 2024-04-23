use serde::Deserialize;
use serde::Serialize;

#[derive(Deserialize, Debug)]
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
}

#[cfg(test)]
mod tests {
  use super::*;
  use serde_json::json;

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
    };

    let data = serde_json::to_value(&response).unwrap();

    assert_eq!(
      data,
      json!({
          "PoolId": "pool1",
          "Id": "id1"
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
    };

    assert_eq!(
      format!("{:?}", response),
      "WaiterResponse { pool_id: \"pool1\", id: \"id1\" }"
    );
  }
}
