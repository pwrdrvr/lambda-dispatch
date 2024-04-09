use crate::prelude::*;
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

pub struct Options {
  pub port: u16,
  pub async_init: bool,
  pub compression: bool,
  pub runtime: Runtime,
  pub local_env: bool,
}

impl Options {
  fn from_env<P: EnvVarProvider>(provider: P) -> Self {
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
    }
  }
}

impl Default for Options {
  fn default() -> Self {
    Self::from_env(RealEnvVarProvider)
  }
}

#[cfg(test)]
use std::collections::HashMap;

#[cfg(test)]
pub struct MockEnvVarProvider {
  vars: HashMap<String, String>,
}

#[cfg(test)]
impl EnvVarProvider for MockEnvVarProvider {
  fn get_var(&self, var: &str) -> Result<String, std::env::VarError> {
    self
      .vars
      .get(var)
      .cloned()
      .ok_or(std::env::VarError::NotPresent)
  }
}

#[cfg(test)]
mod tests {
  use super::*;

  #[test]
  fn test_constructor() {
    let options = Options {
      port: 9000,
      async_init: true,
      compression: false,
      runtime: Runtime::MultiThread,
      local_env: true,
      ..Default::default()
    };

    assert_eq!(options.port, 9000);
    assert_eq!(options.async_init, true);
    assert_eq!(options.compression, false);
    assert_eq!(options.runtime, Runtime::MultiThread);
    assert_eq!(options.local_env, true);
  }

  #[test]
  fn test_options_default() {
    let mock_provider = MockEnvVarProvider {
      vars: [
        ("LAMBDA_DISPATCH_PORT".to_string(), "4000".to_string()),
        ("LAMBDA_DISPATCH_ASYNC_INIT".to_string(), "true".to_string()),
        (
          "LAMBDA_DISPATCH_ENABLE_COMPRESSION".to_string(),
          "false".to_string(),
        ),
        (
          "LAMBDA_DISPATCH_RUNTIME".to_string(),
          "test_runtime".to_string(),
        ),
        (
          "LAMBDA_DISPATCH_FORCE_DEADLINE".to_string(),
          "60".to_string(),
        ),
      ]
      .iter()
      .cloned()
      .collect(),
    };

    let options = Options::from_env(mock_provider);

    assert_eq!(options.port, 4000);
    assert_eq!(options.async_init, true);
    assert_eq!(options.compression, false);
    assert_eq!(options.runtime, Runtime::CurrentThread);
    assert_eq!(options.local_env, true);
  }

  #[test]
  fn test_options_invalid_values() {
    let mock_provider = MockEnvVarProvider {
      vars: [
        ("LAMBDA_DISPATCH_PORT".to_string(), "invalid".to_string()),
        (
          "LAMBDA_DISPATCH_ASYNC_INIT".to_string(),
          "invalid".to_string(),
        ),
        (
          "LAMBDA_DISPATCH_ENABLE_COMPRESSION".to_string(),
          "invalid".to_string(),
        ),
        ("LAMBDA_DISPATCH_RUNTIME".to_string(), "invalid".to_string()),
        (
          "LAMBDA_DISPATCH_FORCE_DEADLINE".to_string(),
          "invalid".to_string(),
        ),
      ]
      .iter()
      .cloned()
      .collect(),
    };

    let options = Options::from_env(mock_provider);

    assert_eq!(options.port, 3001); // Default value
    assert_eq!(options.async_init, false); // Default value
    assert_eq!(options.compression, true); // Default value
    assert_eq!(options.runtime, Runtime::CurrentThread); // Default value
    assert_eq!(options.local_env, true); // Default value
  }
}
