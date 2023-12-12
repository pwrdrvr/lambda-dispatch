namespace PwrDrvr.LambdaDispatch.LambdaLB;

public interface IReverseRequester : IAsyncDisposable
{
  public Task<(int, HttpRequestMessage)> GetRequest();

  public Task SendResponse();
}
