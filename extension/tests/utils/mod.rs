#[cfg(test)]
mod tests {
  use extension::utils::compressable;
  use hyper::HeaderMap;

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
