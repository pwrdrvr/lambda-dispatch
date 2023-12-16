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
      Console.WriteLine(response);
      var metadata = JsonDocument.Parse(response).RootElement;
      // On ECS there is an extra Containers parent that is not in EC2
      var containers = metadata.GetProperty("Containers");
      foreach (var container in containers.EnumerateArray())
      {
        var networks = container.GetProperty("Networks");
        foreach (var network in networks.EnumerateArray())
        {
          if (network.GetProperty("NetworkMode").GetString() == "awsvpc")
          {
            callbackUrl = $"http://{network.GetProperty("IPv4Addresses").EnumerateArray().First().GetString()}:5001/api/chunked";
            return callbackUrl;
          }
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

    callbackUrl = "http://127.0.0.1:5003/api/chunked";
    return callbackUrl;
  }
}