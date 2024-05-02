use crate::{messages::ExitReason, prelude::*};

#[derive(Debug, PartialEq, Copy, Clone)]
pub enum LambdaRequestError {
  RouterLambdaInvokeInvalid,
  RouterUnreachable,
  RouterConnectionError,
  AppConnectionUnreachable,
  AppConnectionError,
  ChannelErrorOther,
}

impl std::fmt::Display for LambdaRequestError {
  fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
    match self {
      LambdaRequestError::RouterLambdaInvokeInvalid => {
        write!(f, "Router Lambda Invoke had invalid payload")
      }
      LambdaRequestError::RouterUnreachable => {
        write!(f, "Failed to establish connection to router")
      }
      LambdaRequestError::RouterConnectionError => {
        write!(
          f,
          "Established router connection failed while reading or writing"
        )
      }
      LambdaRequestError::AppConnectionUnreachable => {
        write!(f, "Failed to establish connection to app")
      }
      LambdaRequestError::AppConnectionError => {
        write!(
          f,
          "Established app connection failed while reading or writing"
        )
      }
      LambdaRequestError::ChannelErrorOther => {
        write!(f, "Channel error other than connection error")
      }
    }
  }
}

impl std::error::Error for LambdaRequestError {}

impl From<LambdaRequestError> for ExitReason {
  fn from(error: LambdaRequestError) -> Self {
    ExitReason::from(&error)
  }
}

impl From<&LambdaRequestError> for ExitReason {
  fn from(error: &LambdaRequestError) -> Self {
    match *error {
      LambdaRequestError::RouterLambdaInvokeInvalid => ExitReason::RouterLambdaInvokeInvalid,
      LambdaRequestError::RouterUnreachable => ExitReason::RouterUnreachable,
      LambdaRequestError::RouterConnectionError => ExitReason::RouterConnectionError,
      LambdaRequestError::AppConnectionUnreachable => ExitReason::AppConnectionError,
      LambdaRequestError::AppConnectionError => ExitReason::AppConnectionError,
      LambdaRequestError::ChannelErrorOther => ExitReason::ChannelErrorOther,
    }
  }
}

impl LambdaRequestError {
  pub fn is_fatal(&self) -> bool {
    // Add other fatal errors to the match pattern. For example:
    // matches!(self, Self::Variant1 | Self::Variant2)
    matches!(self, Self::AppConnectionUnreachable)
  }
}

impl Severity for LambdaRequestError {
  fn severity(&self) -> usize {
    match self {
      Self::RouterLambdaInvokeInvalid => 0,
      Self::RouterUnreachable => 1,
      Self::RouterConnectionError => 2,
      Self::AppConnectionUnreachable => 3,
      Self::AppConnectionError => 4,
      Self::ChannelErrorOther => 5,
    }
  }
}

#[cfg(test)]
mod tests {
  use super::*;

  #[test]
  fn test_from_lambda_request_error() {
    let error = LambdaRequestError::RouterUnreachable;
    let exit_reason: ExitReason = error.into();
    assert_eq!(exit_reason, ExitReason::RouterUnreachable);

    let error = LambdaRequestError::RouterConnectionError;
    let exit_reason: ExitReason = error.into();
    assert_eq!(exit_reason, ExitReason::RouterConnectionError);

    let error = LambdaRequestError::AppConnectionUnreachable;
    let exit_reason: ExitReason = error.into();
    assert_eq!(exit_reason, ExitReason::AppConnectionError);

    let error = LambdaRequestError::AppConnectionError;
    let exit_reason: ExitReason = error.into();
    assert_eq!(exit_reason, ExitReason::AppConnectionError);

    let error = LambdaRequestError::ChannelErrorOther;
    let exit_reason: ExitReason = error.into();
    assert_eq!(exit_reason, ExitReason::ChannelErrorOther);
  }

  #[test]
  fn test_from_lambda_request_error_ref() {
    let error = &LambdaRequestError::RouterUnreachable;
    let exit_reason: ExitReason = error.into();
    assert_eq!(exit_reason, ExitReason::RouterUnreachable);

    let error = &LambdaRequestError::RouterConnectionError;
    let exit_reason: ExitReason = error.into();
    assert_eq!(exit_reason, ExitReason::RouterConnectionError);

    let error = &LambdaRequestError::AppConnectionUnreachable;
    let exit_reason: ExitReason = error.into();
    assert_eq!(exit_reason, ExitReason::AppConnectionError);

    let error = &LambdaRequestError::AppConnectionError;
    let exit_reason: ExitReason = error.into();
    assert_eq!(exit_reason, ExitReason::AppConnectionError);

    let error = &LambdaRequestError::ChannelErrorOther;
    let exit_reason: ExitReason = error.into();
    assert_eq!(exit_reason, ExitReason::ChannelErrorOther);
  }

  #[test]
  fn test_worse() {
    use LambdaRequestError as E;

    // Test that RouterUnreachable is considered worse than AppConnectionError
    let error1 = E::RouterUnreachable;
    let error2 = E::AppConnectionError;
    assert_eq!(error1.worse(error2), E::RouterUnreachable);

    // Test that AppConnectionError is considered worse than ChannelErrorOther
    let error1 = E::AppConnectionError;
    let error2 = E::ChannelErrorOther;
    assert_eq!(error1.worse(error2), E::AppConnectionError);

    // Test that when both errors are the same, that error is returned
    let error1 = E::ChannelErrorOther;
    let error2 = E::ChannelErrorOther;
    assert_eq!(error1.worse(error2), E::ChannelErrorOther);
  }

  #[test]
  fn test_is_fatal() {
    use LambdaRequestError as E;

    // Test that AppConnectionUnreachable is considered fatal
    let error = E::AppConnectionUnreachable;
    assert!(error.is_fatal());

    // Test that RouterUnreachable is not considered fatal
    let error = E::RouterUnreachable;
    assert!(!error.is_fatal());

    // Test that RouterConnectionError is not considered fatal
    let error = E::RouterConnectionError;
    assert!(!error.is_fatal());

    // Test that AppConnectionError is not considered fatal
    let error = E::AppConnectionError;
    assert!(!error.is_fatal());

    // Test that ChannelErrorOther is not considered fatal
    let error = E::ChannelErrorOther;
    assert!(!error.is_fatal());
  }
}
