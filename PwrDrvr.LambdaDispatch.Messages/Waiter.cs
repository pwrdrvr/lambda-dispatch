namespace PwrDrvr.LambdaDispatch.Messages;
using PwrDrvr.LambdaDispatch.Messages;

public class WaiterRequest
{
  public readonly string Id;

  public readonly string DispatcherUrl;

  public WaiterRequest(string id, string dispatcherUrl)
  {
    Id = id;
    DispatcherUrl = dispatcherUrl;
  }
}


public class WaiterResponse
{
  public readonly string Id;

  public WaiterResponse(string id)
  {
    Id = id;
  }
}