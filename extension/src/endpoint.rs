use crate::prelude::*;
use hyper::Uri;
use rustls_pki_types::ServerName;
use std::{borrow::Cow, fmt, str::FromStr, sync::Arc};
use url::Url;

/// An `Endpoint` type to extract and validate the interesting components from a hyper `Uri`, and
/// make them relatively cheap to clone.
#[derive(Debug, Clone)]
pub struct Endpoint {
  scheme: Scheme,
  host: Arc<str>,
  port: u16,
  url: Url,
}

impl fmt::Display for Endpoint {
  fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
    write!(f, "{}://{}:{}", self.scheme.as_str(), self.host, self.port)
  }
}

impl Endpoint {
  pub fn new<T>(scheme: Scheme, host: T, port: u16) -> Self
  where
    T: Into<Arc<str>>,
  {
    let host = host.into();
    let url = Url::parse(&format!("{}://{}:{}", scheme.as_str(), host, port)).unwrap();

    Self {
      scheme,
      host,
      port,
      url,
    }
  }

  /// Construct an [`Endpoint`] from a [`Uri`].
  fn from_uri(uri: &Uri) -> Result<Self, Error> {
    let scheme: Scheme = uri.try_into()?;
    let host = uri
      .host()
      .with_context(|| format!("could not determine host from url '{}'", uri))?;
    let port = uri.port_u16().unwrap_or(scheme.default_port());
    let url = Url::parse(&uri.to_string()).unwrap();

    Ok(Self {
      scheme,
      host: host.into(),
      port,
      url,
    })
  }

  pub fn scheme(&self) -> Scheme {
    self.scheme
  }

  pub fn host(&self) -> &str {
    &self.host
  }

  pub fn port(&self) -> u16 {
    self.port
  }

  pub fn url(&self) -> &Url {
    &self.url
  }

  /// Formats a host request header that specifies the host and port number of the server to which
  /// the request is being sent. If the [`Endpoint`]'s port is the default port for the service, the
  /// host can be used directly without allocating a new string.
  ///
  /// See [mozilla dev docs](https://developer.mozilla.org/en-US/docs/Web/HTTP/Headers/Host):
  /// If no port is included, the default port for the service requested is implied (e.g., 443 for
  /// an HTTPS URL, and 80 for an HTTP URL).
  pub fn host_header(&self) -> Cow<'_, str> {
    if self.port == self.scheme.default_port() {
      Cow::Borrowed(&self.host)
    } else {
      format!("{}:{}", &self.host, self.port).into()
    }
  }

  pub fn socket_addr_coercable(&self) -> (&str, u16) {
    (self.host(), self.port)
  }
}

impl TryFrom<&Uri> for Endpoint {
  type Error = Error;

  fn try_from(uri: &Uri) -> Result<Self, Self::Error> {
    Self::from_uri(uri)
  }
}

impl TryFrom<Uri> for Endpoint {
  type Error = Error;

  fn try_from(uri: Uri) -> Result<Self, Self::Error> {
    Self::from_uri(&uri)
  }
}

impl FromStr for Endpoint {
  type Err = Error;
  fn from_str(s: &str) -> Result<Self, Self::Err> {
    let uri: Uri = s.parse()?;
    uri.try_into()
  }
}

impl TryFrom<Endpoint> for ServerName<'_> {
  type Error = Error;
  fn try_from(endpoint: Endpoint) -> Result<Self, Self::Error> {
    Ok(ServerName::try_from(endpoint.host().to_owned())?)
  }
}

impl<'a> TryFrom<&'a Endpoint> for ServerName<'a> {
  type Error = Error;
  fn try_from(endpoint: &'a Endpoint) -> Result<Self, Self::Error> {
    Ok(ServerName::try_from(endpoint.host())?)
  }
}

#[derive(Copy, Clone, Debug, Default, PartialEq)]
pub enum Scheme {
  #[default]
  Http,
  Https,
}

impl Scheme {
  pub fn as_str(&self) -> &str {
    match self {
      Scheme::Http => "http",
      Scheme::Https => "https",
    }
  }

  pub fn default_port(&self) -> u16 {
    match self {
      Scheme::Http => 80,
      Scheme::Https => 443,
    }
  }
}

impl AsRef<str> for Scheme {
  fn as_ref(&self) -> &str {
    self.as_str()
  }
}

impl TryFrom<&Uri> for Scheme {
  type Error = Error;
  fn try_from(uri: &Uri) -> Result<Self, Self::Error> {
    match uri.scheme().map(|s| s.as_str()) {
      Some("https") => Ok(Scheme::Https),
      Some("http") => Ok(Scheme::Http),
      _ => anyhow::bail!(
        "'{}' has an invalid scheme, only 'http' and 'https' are supported",
        uri
      ),
    }
  }
}

#[cfg(test)]
mod tests {
  use super::*;

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
