using Amazon.Lambda;
using Amazon.Lambda.Model;
using PwrDrvr.LambdaDispatch.Messages;
using System.Text.Json;

namespace PwrDrvr.LambdaDispatch.Router;

public enum LambdaConnectionState
{
  /// <summary>
  /// The connection is open and ready to accept requests
  /// </summary>
  Open,

  /// <summary>
  /// Request has been sent and we are waiting for a response
  /// </summary>
  Busy,

  /// <summary>
  /// The connection is closed and should not be used
  /// </summary>
  Closed
}

public class LambdaConnection
{
  /// <summary>
  /// The state of the connection
  /// </summary>
  public LambdaConnectionState State { get; private set; }

  /// <summary>
  /// The Request from the Lambda (which we will send the response on)
  /// </summary>
  public HttpRequest Request { get; private set; }

  /// <summary>
  /// The Response from the Lambda (which we will send the request on)
  /// </summary>
  public HttpResponse Response { get; private set; }

  /// <summary>
  /// Handle back to the Lambda Instance that owns this Connection
  /// </summary>
  public LambdaInstance Instance { get; private set; }

  /// <summary>
  /// Task that completes when the connection is closed
  /// </summary>
  public TaskCompletionSource TCS { get; private set; } = new TaskCompletionSource();

  public LambdaConnection(HttpRequest request, HttpResponse response, LambdaInstance instance)
  {
    Request = request;
    Response = response;
    Instance = instance;

    // Set the state to open
    State = LambdaConnectionState.Open;

    // Handle an abnormal connection termination
    Request.HttpContext.RequestAborted.Register(() =>
    {
      Instance.ConnectionClosed(State == LambdaConnectionState.Busy);

      // Set the state to closed
      State = LambdaConnectionState.Closed;
    });

    // Register a callback for when the connection closes
    Response.OnCompleted(() =>
    {
      Instance.ConnectionClosed(State == LambdaConnectionState.Busy);

      // Set the state to closed
      State = LambdaConnectionState.Closed;

      return Task.CompletedTask;
    });
  }

  public async Task RunRequest(HttpRequest request, HttpResponse response)
  {
    // TODO: Send the incoming Request on the lambda's Response
  }
}