use crate::prelude::*;
use hyper::{header::HeaderValue, HeaderMap};

/// Determine if a response should be compressed based on the content encoding, length, and type.
pub fn compressable(headers: &HeaderMap<HeaderValue>) -> Result<bool, Error> {
  // Check if we have a Content-Encoding response header
  // If we do, we should not gzip the response
  let unencoded = headers.get("content-encoding").is_none();

  // If it's a chunked response we'll compress it; but if it's non-chunked we'll only compress it
  // if it's not small and has a reasonable content-type for compression
  Ok(unencoded && compressable_content_length(headers) && compressable_content_type(headers)?)
}

/// Check if we have a Content-Length response header
/// If we do, and if it's small (e.g. < 1 KB), we should not gzip the response
fn compressable_content_length(headers: &HeaderMap<HeaderValue>) -> bool {
  let Some(length) = headers
    .get(hyper::header::CONTENT_LENGTH)
    .and_then(|val| val.to_str().ok())
    .and_then(|s| s.parse::<i32>().ok())
  else {
    return false;
  };

  length > 1024
}

fn compressable_content_type(headers: &HeaderMap<HeaderValue>) -> Result<bool, Error> {
  let Some(content_type) = headers.get("content-type") else {
    return Ok(false);
  };
  let content_type = content_type.to_str()?;

  Ok(
    content_type.starts_with("text/")
      || content_type.starts_with("application/json")
      || content_type.starts_with("application/javascript")
      || content_type.starts_with("image/svg+xml")
      || content_type.starts_with("application/xhtml+xml")
      || content_type.starts_with("application/x-javascript")
      || content_type.starts_with("application/xml"),
  )
}

#[cfg(test)]
mod tests {
  use super::*;

  const LARGE_CONTENT_LENGTH: usize = 2048;

  #[test]
  fn test_compressable_when_not_encoded_and_large_length_and_correct_type() {
    let mut headers = HeaderMap::new();
    headers.insert("Content-Length", LARGE_CONTENT_LENGTH.into());
    headers.insert("Content-Type", "application/json".try_into().unwrap());

    assert!(compressable(&headers).unwrap());
  }

  #[test]
  fn test_not_compressable_when_content_encoding_present() {
    let mut headers = HeaderMap::new();
    headers.insert("Content-Encoding", "anything".try_into().unwrap());
    headers.insert("Content-Length", LARGE_CONTENT_LENGTH.into());
    headers.insert("Content-Type", "application/json".try_into().unwrap());

    assert!(!compressable(&headers).unwrap());
  }

  #[test]
  fn test_not_compressable_when_content_length_is_small() {
    let mut headers = HeaderMap::new();
    headers.insert("Content-Length", 10usize.into());
    headers.insert("Content-Type", "application/json".try_into().unwrap());

    assert!(!compressable(&headers).unwrap());
  }

  #[test]
  fn test_not_compressable_when_content_length_missing() {
    let mut headers = HeaderMap::new();
    headers.insert("Content-Type", "application/json".try_into().unwrap());

    assert!(!compressable(&headers).unwrap());
  }

  #[test]
  fn test_not_compressable_when_content_type_incorrect() {
    let mut headers = HeaderMap::new();
    headers.insert("Content-Length", LARGE_CONTENT_LENGTH.into());
    headers.insert("Content-Type", "application/nope".try_into().unwrap());

    assert!(!compressable(&headers).unwrap());
  }

  #[test]
  fn test_not_compressable_when_content_type_missing() {
    let mut headers = HeaderMap::new();
    headers.insert("Content-Length", LARGE_CONTENT_LENGTH.into());

    assert!(!compressable(&headers).unwrap());
  }
}
