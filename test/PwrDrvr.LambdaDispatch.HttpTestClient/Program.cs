// See https://aka.ms/new-console-template for more information

using System.Security.Authentication;

var _handler = new HttpClientHandler();

// _handler.SslProtocols = SslProtocols.None;
// _handler.Proxy = new WebProxy("http://localhost:8080");
// _handler.UseProxy = true;
// // Allow all certificates
// _handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
_handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
{
  // If the certificate is a valid, signed certificate, return true.
  if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
  {
    return true;
  }

  // If it's a self-signed certificate for the specific host, return true.
  // TODO: Get the CN name to allow
  if (cert != null && cert.Subject.Contains("CN=lambdadispatch.local"))
  {
    return true;
  }

  // In all other cases, return false.
  return false;
};

var _client = new HttpClient(_handler, true)
{
  DefaultRequestVersion = new Version(2, 0),
  DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
  Timeout = TimeSpan.FromMinutes(15),
};

int i = 0;

// Print i every 5 seconds
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
Task.Run(async () =>
{
  while (true)
  {
    await Task.Delay(TimeSpan.FromSeconds(5));
    Console.WriteLine(i);
  }
});
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

// Wait for a key press
Console.WriteLine("Press any key to start");
Console.ReadKey();

for (i = 0; i < 1000000000; i++)
{
  // var url = "https://127.0.0.1:5004/api/chunked/ping/cats";
  // var url = "http://127.0.0.1:3000/ping";
  var url = "https://127.0.0.1:3001/ping";
  // var response = await _client.GetAsync(url);
  // await response.Content.ReadAsStringAsync();
  using var request = new HttpRequestMessage(HttpMethod.Get, url);
  request.Version = new Version(2, 0);
  request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
  using var response = await _client.SendAsync(request);
  await response.Content.CopyToAsync(Stream.Null);
  // using var stream = await response.Content.ReadAsStreamAsync();
  // await stream.CopyToAsync(Stream.Null);
}