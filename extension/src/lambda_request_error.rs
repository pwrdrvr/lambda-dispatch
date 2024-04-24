use crate::messages::ExitReason;

#[derive(Debug, PartialEq)]
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
    use LambdaRequestError::*;

    let fatal_errors = [
      // Add other fatal errors here
      AppConnectionUnreachable,
    ];

    fatal_errors.contains(self)
  }

  pub fn worse(self, other: Self) -> Self {
    use LambdaRequestError::*;

    let ordered_reasons = [
      RouterLambdaInvokeInvalid,
      RouterUnreachable,
      RouterConnectionError,
      AppConnectionUnreachable,
      AppConnectionError,
      ChannelErrorOther,
    ];

    for reason in ordered_reasons {
      if self == reason || other == reason {
        return reason;
      }
    }

    self // If no match is found, return the current value
  }

  // This function will cause a compile-time error if a new variant is added to the enum
  // but not added to the match expression.
  #[allow(dead_code)]
  fn ensure_all_variants_handled(variant: Self) {
    use LambdaRequestError::*;

    match variant {
      // HEY - If you add here you need to add to the `worse` function array above
      RouterLambdaInvokeInvalid => {}
      RouterUnreachable => {}
      RouterConnectionError => {}
      AppConnectionUnreachable => {}
      AppConnectionError => {}
      ChannelErrorOther => {}
    }
    // HEY - If you add here you need to add to the `worse` function array above
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
    use LambdaRequestError::*;

    // Test that RouterUnreachable is considered worse than AppConnectionError
    let error1 = RouterUnreachable;
    let error2 = AppConnectionError;
    assert_eq!(error1.worse(error2), RouterUnreachable);

    // Test that AppConnectionError is considered worse than ChannelErrorOther
    let error1 = AppConnectionError;
    let error2 = ChannelErrorOther;
    assert_eq!(error1.worse(error2), AppConnectionError);

    // Test that when both errors are the same, that error is returned
    let error1 = ChannelErrorOther;
    let error2 = ChannelErrorOther;
    assert_eq!(error1.worse(error2), ChannelErrorOther);
  }

  #[test]
  fn test_is_fatal() {
    use LambdaRequestError::*;

    // Test that AppConnectionUnreachable is considered fatal
    let error = AppConnectionUnreachable;
    assert!(error.is_fatal());

    // Test that RouterUnreachable is not considered fatal
    let error = RouterUnreachable;
    assert!(!error.is_fatal());

    // Test that RouterConnectionError is not considered fatal
    let error = RouterConnectionError;
    assert!(!error.is_fatal());

    // Test that AppConnectionError is not considered fatal
    let error = AppConnectionError;
    assert!(!error.is_fatal());

    // Test that ChannelErrorOther is not considered fatal
    let error = ChannelErrorOther;
    assert!(!error.is_fatal());
  }
}
