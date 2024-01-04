namespace PwrDrvr.LambdaDispatch.Router;

using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

public class GetCallbackIP
{
  static private string? callbackUrl = null;

  static private int performedInit = 0;

  static private ReaderWriterLockSlim rwLock = new();

  static private string scheme = "https";

  static private int port = 5004;

  static public ValueTask<string> Init(int port, string scheme)
  {
    GetCallbackIP.port = port;
    GetCallbackIP.scheme = scheme;

    return Get();
  }

  static public async ValueTask<string> Get()
  {
    // Once it is set, it is set, we don't have to lock
    if (callbackUrl != null)
    {
      return callbackUrl;
    }

    try
    {
      // Wait for the one init to finish
      rwLock.EnterUpgradeableReadLock();

      // Only one thread should do the init
      if (Interlocked.CompareExchange(ref performedInit, 1, 0) == 1)
      {
        if (callbackUrl == null)
        {
          throw new InvalidOperationException("Callback URL is null");
        }
        return callbackUrl;
      }

      // Upgrade the read lock to a write lock
      rwLock.EnterWriteLock();

      // We are the only thread that will get here, ever
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
              callbackUrl = $"{scheme}://{network.GetProperty("IPv4Addresses").EnumerateArray().First().GetString()}:{port}/api/chunked";
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

      callbackUrl = $"{scheme}://{Environment.GetEnvironmentVariable("ROUTER_CALLBACK_HOST") ?? "127.0.0.1"}:{port}/api/chunked";
      return callbackUrl;
    }
    finally
    {
      if (rwLock.IsWriteLockHeld)
      {
        rwLock.ExitWriteLock();
      }
      if (rwLock.IsUpgradeableReadLockHeld)
      {
        rwLock.ExitUpgradeableReadLock();
      }
    }
  }
}