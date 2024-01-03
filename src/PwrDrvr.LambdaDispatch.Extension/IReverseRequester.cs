namespace PwrDrvr.LambdaDispatch.Extension;

public interface IReverseRequester : IAsyncDisposable
{
  public Task<(int, HttpRequestMessage)> GetRequest();

  public Task SendResponse();
}
