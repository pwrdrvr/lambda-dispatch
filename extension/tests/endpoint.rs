#[cfg(test)]
mod tests {
  use std::borrow::Cow;

  use extension::endpoint::{Endpoint, Scheme};
  use hyper::Uri;

  #[test]
  fn test_endpoint_debug() {
    let endpoint = Endpoint::new(Scheme::Http, "localhost", 8000);
    let debug_string = format!("{:?}", endpoint);
    assert_eq!(
      debug_string,
      "Endpoint { scheme: Http, host: \"localhost\", port: 8000, url: Url { scheme: \"http\", cannot_be_a_base: false, username: \"\", password: None, host: Some(Domain(\"localhost\")), port: Some(8000), path: \"/\", query: None, fragment: None } }"
    );
  }

  #[test]
  fn test_endpoint_from_uri_error() {
    let uri = "/hello/world".parse::<Uri>().unwrap();
    let result = Endpoint::from_uri(&uri);
    assert!(result.is_err());
    assert_eq!(
      format!("{}", result.unwrap_err()),
      "'/hello/world' has an invalid scheme, only 'http' and 'https' are supported"
    );
  }

  #[test]
  fn test_host_header_with_default_port() {
    let endpoint: Endpoint = "http://example.com:80".parse().unwrap();

    assert_eq!(endpoint.host_header(), Cow::Borrowed("example.com"))
  }

  #[test]
  fn test_host_header_without_port() {
    let endpoint: Endpoint = "http://example.com".parse().unwrap();

    assert_eq!(endpoint.host_header(), Cow::Borrowed("example.com"))
  }

  #[test]
  fn test_host_header_custom_port() {
    let endpoint: Endpoint = "http://example.com:8080".parse().unwrap();

    assert_eq!(
      endpoint.host_header(),
      Cow::Owned::<String>("example.com:8080".to_string())
    )
  }

  #[test]
  fn test_scheme_debug() {
    let scheme = Scheme::Http;
    assert_eq!(format!("{:?}", scheme), "Http");

    let scheme = Scheme::Https;
    assert_eq!(format!("{:?}", scheme), "Https");
  }

  #[test]
  fn test_scheme_default() {
    let scheme = Scheme::default();
    assert_eq!(scheme, Scheme::Http);
  }

  #[test]
  fn test_scheme_partial_eq() {
    assert_eq!(Scheme::Http, Scheme::Http);
    assert_eq!(Scheme::Https, Scheme::Https);
    assert_ne!(Scheme::Http, Scheme::Https);
  }
}
