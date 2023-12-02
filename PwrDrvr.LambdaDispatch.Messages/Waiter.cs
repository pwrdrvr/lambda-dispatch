namespace PwrDrvr.LambdaDispatch.Messages;

public class WaiterRequest
{
  public string Id { get; set; }

  public string DispatcherUrl { get; set; }
}


public class WaiterResponse
{
  public string Id { get; set; }
}