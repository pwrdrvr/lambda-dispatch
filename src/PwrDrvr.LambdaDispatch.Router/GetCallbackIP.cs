namespace PwrDrvr.LambdaDispatch.Router;

using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

public class GetCallbackIP
{
  static private string? callbackUrl = null;

  static public async Task<string> Get()
  {
    if (callbackUrl != null)
    {
      return callbackUrl;
    }

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var client = new HttpClient();
    try
    {
      var response = await client.GetStringAsync("http://169.254.170.2/v2/metadata", cts.Token);
      var metadata = JsonDocument.Parse(response).RootElement;
      var networks = metadata.GetProperty("Networks");
      foreach (var network in networks.EnumerateArray())
      {
        if (network.GetProperty("NetworkMode").GetString() == "awsvpc")
        {
          callbackUrl = $"http://{network.GetProperty("IPv4Addresses").EnumerateArray().First().GetString()}:5001/api/chunked";
          return callbackUrl;
        }
      }
    }
    catch (HttpRequestException)
    {
      // Ignore
    }
    catch (OperationCanceledException)
    {
      // Ignore
    }

    callbackUrl = "http://localhost:5001/api/chunked";
    return callbackUrl;
  }
}