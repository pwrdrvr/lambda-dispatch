use serde::Deserialize;
use serde::Serialize;

#[derive(Deserialize, Debug)]
pub struct WaiterRequest {
  #[serde(rename = "Id")]
  pub id: String,
  #[serde(rename = "DispatcherUrl")]
  pub dispatcher_url: String,
  #[serde(rename = "NumberOfChannels")]
  pub number_of_channels: i32,
  #[serde(rename = "SentTime")]
  pub sent_time: String,
  #[serde(rename = "InitOnly")]
  pub init_only: bool,
}

#[derive(Serialize, Debug)]
pub struct WaiterResponse {
  #[serde(rename = "Id")]
  pub id: String,
}
