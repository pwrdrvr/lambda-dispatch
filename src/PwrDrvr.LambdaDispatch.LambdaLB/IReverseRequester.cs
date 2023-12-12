namespace PwrDrvr.LambdaDispatch.LambdaLB;

public interface IReverseRequester : IAsyncDisposable
{
  public Task<int> GetRequest();

  public Task SendResponse();
}
