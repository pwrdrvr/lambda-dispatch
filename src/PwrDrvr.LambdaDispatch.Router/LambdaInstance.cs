using Amazon.Lambda;
using Amazon.Lambda.Model;
using PwrDrvr.LambdaDispatch.Messages;
using System.Text.Json;

namespace PwrDrvr.LambdaDispatch.Router;

public enum LambdaInstanceStatus
{
  NotConnected,
  Idle,
  Running,
  Completed,
  Failed
}

public class LambdaInstance
{
  public static readonly AmazonLambdaClient LambdaClient = new AmazonLambdaClient();

  public HttpRequest Request { get; private set; }

  public HttpResponse Response { get; private set; }

  public string Id { get; private set; }

  public LambdaInstanceStatus Status { get; private set; }

  public TaskCompletionSource TCS { get; private set; } = new TaskCompletionSource();

  public LambdaInstance()
  {
    Id = Guid.NewGuid().ToString();
    Status = LambdaInstanceStatus.NotConnected;
  }

  public LambdaInstance(HttpRequest request, HttpResponse response, string? id = null)
  {
    Request = request;
    Response = response;
    Id = !String.IsNullOrEmpty(id) ? id : Guid.NewGuid().ToString();
    Status = LambdaInstanceStatus.Idle;
  }

  public async Task Connect()
  {
    // Setup the Lambda payload
    var payload = new WaiterRequest
    {
      Id = Id,
      DispatcherUrl = await GetCallbackIP.Get()
    };

    // Invoke the Lambda
    var request = new InvokeRequest
    {
      // TODO: Get this from the configuration
      FunctionName = "PwrDrvr.LambdaDispatch.Lambda",
      InvocationType = InvocationType.RequestResponse,
      Payload = JsonSerializer.Serialize(payload)
    };

    var response = await LambdaClient.InvokeAsync(request);

    // TODO: Should not actually wait here as we will not get a response until the Lambda is done
  }
}