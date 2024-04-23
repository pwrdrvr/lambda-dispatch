use crate::messages::ExitReason;

#[derive(Debug, PartialEq)]
pub enum LambdaRequestError {
  RouterUnreachable,
  SendRequestError,
}

impl std::fmt::Display for LambdaRequestError {
  fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
    match self {
      LambdaRequestError::RouterUnreachable => {
        write!(f, "Failed to establish connection to router")
      }
      LambdaRequestError::SendRequestError => write!(f, "Failed to send request"),
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
      LambdaRequestError::RouterUnreachable => ExitReason::RouterUnreachable,
      LambdaRequestError::SendRequestError => ExitReason::RouterConnectionError,
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

    let error = LambdaRequestError::SendRequestError;
    let exit_reason: ExitReason = error.into();
    assert_eq!(exit_reason, ExitReason::RouterConnectionError);
  }

  #[test]
  fn test_from_lambda_request_error_ref() {
    let error = &LambdaRequestError::RouterUnreachable;
    let exit_reason: ExitReason = error.into();
    assert_eq!(exit_reason, ExitReason::RouterUnreachable);

    let error = &LambdaRequestError::SendRequestError;
    let exit_reason: ExitReason = error.into();
    assert_eq!(exit_reason, ExitReason::RouterConnectionError);
  }
}
