use std::time::{SystemTime, UNIX_EPOCH};

pub fn current_time_millis() -> u64 {
  let duration_since_epoch = SystemTime::now()
    .duration_since(UNIX_EPOCH)
    .expect("Time went backwards");
  duration_since_epoch.as_millis() as u64
}
