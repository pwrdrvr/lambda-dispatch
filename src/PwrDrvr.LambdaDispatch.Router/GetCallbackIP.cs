namespace PwrDrvr.LambdaDispatch.Router;

public interface IGetCallbackIP
{
  string CallbackUrl { get; }
}

public class GetCallbackIP : IGetCallbackIP
{
  private readonly string callbackUrl = null;

  public string CallbackUrl { get => callbackUrl; }

  public GetCallbackIP(int port, string scheme, string networkIp)
  {
    if (string.IsNullOrEmpty(networkIp))
    {
      throw new ArgumentException("Network IP is not set", nameof(networkIp));
    }

    callbackUrl = $"{scheme}://{networkIp}:{port}/api/chunked";
  }
}