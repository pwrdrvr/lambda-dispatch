namespace PwrDrvr.LambdaDispatch.Router;

public static class GetCallbackIP
{
  static private string? callbackUrl = null;

  static private string scheme = "https";

  static private int port = 5004;

  static private string? networkIp;

  static public string Init(int port, string scheme, string networkIp)
  {
    GetCallbackIP.port = port;
    GetCallbackIP.scheme = scheme;
    GetCallbackIP.networkIp = networkIp;

    return Get();
  }

  static public string Get()
  {
    // Once it is set, it is set, we don't have to lock
    if (callbackUrl != null)
    {
      return callbackUrl;
    }

    if (networkIp == null)
    {
      throw new Exception("Network IP is not set");
    }

    callbackUrl = $"{scheme}://{Environment.GetEnvironmentVariable("ROUTER_CALLBACK_HOST") ?? networkIp}:{port}/api/chunked";
    return callbackUrl;
  }
}