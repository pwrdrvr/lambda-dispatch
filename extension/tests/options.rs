#[cfg(test)]
mod tests {
  use extension::options::{EnvVarProvider, Options, Runtime};
  use std::collections::HashMap;
  use std::time::Duration;

  pub struct MockEnvVarProvider {
    vars: HashMap<String, String>,
  }

  impl EnvVarProvider for MockEnvVarProvider {
    fn get_var(&self, var: &str) -> Result<String, std::env::VarError> {
      self
        .vars
        .get(var)
        .cloned()
        .ok_or(std::env::VarError::NotPresent)
    }
  }

  #[test]
  fn test_constructor() {
    let options = Options {
      port: 9000,
      async_init: true,
      compression: false,
      runtime: Runtime::MultiThread,
      local_env: true,
      force_deadline_secs: Some(Duration::from_secs(6)),
      async_init_timeout: Duration::from_secs(11),
      ..Default::default()
    };

    assert_eq!(options.port, 9000);
    assert!(options.async_init);
    assert!(!options.compression);
    assert_eq!(options.runtime, Runtime::MultiThread);
    assert!(options.local_env);
    assert_eq!(options.force_deadline_secs, Some(Duration::from_secs(6)));
    assert_eq!(options.async_init_timeout, Duration::from_secs(11));
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
        (
          "LAMBDA_DISPATCH_ASYNC_INIT_TIMEOUT".to_string(),
          "13".to_string(),
        ),
      ]
      .into_iter()
      .collect(),
    };

    let options = Options::from_env(mock_provider);

    assert_eq!(options.port, 4000);
    assert!(options.async_init);
    assert!(!options.compression);
    assert_eq!(options.runtime, Runtime::CurrentThread);
    assert!(options.local_env);
    assert_eq!(options.force_deadline_secs, Some(Duration::from_secs(60)));
    assert_eq!(options.async_init_timeout, Duration::from_secs(13));
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
        (
          "LAMBDA_DISPATCH_ASYNC_INIT_TIMEOUT".to_string(),
          "invalid".to_string(),
        ),
      ]
      .into_iter()
      .collect(),
    };

    let options = Options::from_env(mock_provider);

    assert_eq!(options.port, 3001); // Default value
    assert!(!options.async_init); // Default value
    assert!(options.compression); // Default value
    assert_eq!(options.runtime, Runtime::CurrentThread); // Default value
    assert!(options.local_env); // Default value
    assert_eq!(options.force_deadline_secs, None);
    assert_eq!(options.async_init_timeout, Duration::from_secs(10));
  }
}
