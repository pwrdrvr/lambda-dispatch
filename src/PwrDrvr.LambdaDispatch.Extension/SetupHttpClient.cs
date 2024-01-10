#if USE_SOCKETS_HTTP_HANDLER
using System.Net.Security;
#endif

using System.Security.Authentication;


/// <summary>
/// Use an insecure cipher to allow Wireshark to decrypt the traffic, if desired
/// </summary>
public static class SetupHttpClient
{
#if USE_SOCKETS_HTTP_HANDLER
  private static SslClientAuthenticationOptions sslOptions = new SslClientAuthenticationOptions
  {
    RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
    {
      // If the certificate is a valid, signed certificate, return true.
      if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
      {
        return true;
      }

      // If it's a self-signed certificate for the specific host, return true.
      if (cert != null && cert.Subject.Contains("CN=lambdadispatch.local"))
      {
        return true;
      }

      // In all other cases, return false.
      return false;
    },
    // CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
    ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http2 },
#if USE_INSECURE_CIPHER_FOR_WIRESHARK
    // WireShark needs this to decrypt the traffic
    CipherSuitesPolicy = new CipherSuitesPolicy(new List<TlsCipherSuite> { TlsCipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA256 })
#endif
  };
  private static SocketsHttpHandler CreateHandler() => new SocketsHttpHandler
  {
    SslOptions = sslOptions,
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(15),
  };
#else
  private static HttpClientHandler CreateHandler() => new HttpClientHandler()
  {
    ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
    {
      // If the certificate is a valid, signed certificate, return true.
      if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
      {
        return true;
      }

      // If it's a self-signed certificate for the specific host, return true.
      if (cert != null && cert.Subject.Contains("CN=lambdadispatch.local"))
      {
        return true;
      }

      // In all other cases, return false.
      return false;
    },
  };
#endif

  public static HttpClient CreateClient(string dispatcherUrl)
  {
    var handler = CreateHandler();

    if (new Uri(dispatcherUrl).Scheme == "http")
    {
      handler.SslProtocols = SslProtocols.None;
    }

    return new HttpClient(handler, true)
    {
      DefaultRequestVersion = new Version(2, 0),
      DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
      Timeout = TimeSpan.FromMinutes(15),
    };
  }
}