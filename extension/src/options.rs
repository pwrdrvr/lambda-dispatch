use crate::prelude::*;
use std::time::Duration;

#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum Runtime {
  #[default]
  DefaultMultiThread,
  MultiThread,
  CurrentThread,
}

impl<T> From<T> for Runtime
where
  T: AsRef<str>,
{
  fn from(value: T) -> Self {
    match value.as_ref().to_lowercase().as_ref() {
      "default_multi_thread" => Runtime::DefaultMultiThread,
      "multi_thread" => Runtime::MultiThread,
      "current_thread" => Runtime::CurrentThread,
      _ => Runtime::CurrentThread,
    }
  }
}

pub trait EnvVarProvider {
  fn get_var(&self, var: &str) -> Result<String, std::env::VarError>;
}

pub struct RealEnvVarProvider;

impl EnvVarProvider for RealEnvVarProvider {
  fn get_var(&self, var: &str) -> Result<String, std::env::VarError> {
    std::env::var(var)
  }
}

#[derive(Clone, Copy)]
pub struct Options {
  pub port: u16,
  pub async_init: bool,
  pub compression: bool,
  pub runtime: Runtime,
  pub local_env: bool,
  pub force_deadline_secs: Option<Duration>,
  pub async_init_timeout: Duration,
}

impl Options {
  pub fn from_env<P: EnvVarProvider>(provider: P) -> Self {
    let force_deadline_secs = provider
      .get_var("LAMBDA_DISPATCH_FORCE_DEADLINE")
      .ok()
      .and_then(|d| d.parse::<u64>().map(Duration::from_secs).ok());
    let async_init_timeout = provider
      .get_var("LAMBDA_DISPATCH_ASYNC_INIT_TIMEOUT")
      .ok()
      .and_then(|d| d.parse::<u64>().map(Duration::from_secs).ok())
      .unwrap_or(Duration::from_secs(10));

    Options {
      port: provider
        .get_var("LAMBDA_DISPATCH_PORT")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(3001),
      async_init: provider
        .get_var("LAMBDA_DISPATCH_ASYNC_INIT")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(false),
      compression: provider
        .get_var("LAMBDA_DISPATCH_ENABLE_COMPRESSION")
        .ok()
        .and_then(|v| v.parse().ok())
        .unwrap_or(true),
      runtime: provider
        .get_var("LAMBDA_DISPATCH_RUNTIME")
        .ok()
        .map_or(Runtime::CurrentThread, |v| v.into()),
      local_env: provider.get_var("LAMBDA_DISPATCH_FORCE_DEADLINE").is_ok(),
      force_deadline_secs,
      async_init_timeout,
    }
  }
}

impl Default for Options {
  fn default() -> Self {
    Self::from_env(RealEnvVarProvider)
  }
}
