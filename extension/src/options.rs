use std::env;

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum Runtime {
  #[default]
  DefaultMultiThread,
  MultiThread,
  CurrentThread,
}

impl From<&str> for Runtime {
  fn from(value: &str) -> Self {
    match value.to_lowercase().as_str() {
      "default_multi_thread" => Runtime::DefaultMultiThread,
      "multi_thread" => Runtime::MultiThread,
      "current_thread" => Runtime::CurrentThread,
      _ => Runtime::CurrentThread,
    }
  }
}

pub struct Options {
  pub port: u16,
  pub async_init: bool,
  pub compression: bool,
  pub runtime: Runtime,
}

impl Default for Options {
  fn default() -> Self {
    Options {
      port: env::var("LAMBDA_DISPATCH_PORT")
        .unwrap_or_else(|_| "3001".to_string())
        .parse()
        .unwrap_or(3001),
      async_init: env::var("LAMBDA_DISPATCH_ASYNC_INIT")
        .unwrap_or_else(|_| "false".to_string())
        .parse()
        .unwrap_or(false),
      compression: env::var("LAMBDA_DISPATCH_ENABLE_COMPRESSION")
        .unwrap_or_else(|_| "true".to_string())
        .parse()
        .unwrap_or(true),
      runtime: env::var("LAMBDA_DISPATCH_RUNTIME")
        .unwrap_or_else(|_| "current_thread".to_string())
        .as_str()
        .into(),
      // LAMBDA_DISPATCH_FORCE_DEADLINE,
    }
  }
}
