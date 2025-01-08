#![warn(rust_2018_idioms)]

use std::io::Write;

use router::{prelude::*, startup::startup};

fn main() -> Result<()> {
  env_logger::Builder::new()
    .format(|buf, record| {
      writeln!(
        buf,
        "{} [{}] - {}",
        chrono::Local::now().format("%Y-%m-%dT%H:%M:%S.%3f"),
        record.level(),
        record.args()
      )
    })
    .filter(None, log::LevelFilter::Info) // Change this to Debug or Warn as needed
    .init();

  // required to enable CloudWatch error logging by the runtime
  tracing_subscriber::fmt()
    .with_max_level(tracing::Level::INFO)
    // disable printing the name of the module in every log line.
    .with_target(false)
    // disabling time is handy because CloudWatch will add the ingestion time.
    .without_time()
    .init();

  startup()
}
