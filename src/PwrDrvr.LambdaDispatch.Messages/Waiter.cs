namespace PwrDrvr.LambdaDispatch.Messages;

public class WaiterRequest
{
  /// <summary>
  /// Unique ID for this Lambda instance
  /// Sent on all requests and responses
  /// </summary>
  public string Id { get; set; }

  /// <summary>
  /// URL of the router to connect back to
  /// </summary>
  public string DispatcherUrl { get; set; }

  /// <summary>
  /// Set the number of channels to open back to the router
  /// </summary>
  public int NumberOfChannels { get; set; }
}


public class WaiterResponse
{
  public string Id { get; set; }
}