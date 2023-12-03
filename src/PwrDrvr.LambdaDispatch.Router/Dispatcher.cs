using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Text;

namespace PwrDrvr.LambdaDispatch.Router;

public class Dispatcher
{
  // Requests that are dispatched to a Lambda - Keyed by request ID
  private readonly ConcurrentDictionary<string, (HttpRequest, HttpResponse, TaskCompletionSource)> _runningRequests = new();

  // Requests that are waiting to be dispatched to a Lambda
  private readonly ConcurrentQueue<(HttpRequest, HttpResponse, TaskCompletionSource)> _pendingRequests = new();

  // Busy Lambda Connections - Keyed by request ID
  private readonly ConcurrentDictionary<string, LambdaInstance> _busyLambdas = new();

  // Idle Lambda Connections
  private readonly ConcurrentQueue<LambdaInstance> _idleLambdas = new();

  // Starting Lambdas - Invoked but not called back yet
  private readonly ConcurrentDictionary<string, LambdaInstance> _startingLambdas = new();

  public Dispatcher()
  {
    Console.WriteLine("Dispatcher created");
  }

  // Add a new request, dispatch immediately if able
  public async Task AddRequest(HttpRequest incomingRequest, HttpResponse incomingResponse)
  {
    Console.WriteLine("Adding request to the Dispatcher");

    // Try to get an idle lambda and dispatch immediately
    if (_idleLambdas.TryDequeue(out var lambdaInstance))
    {
      Console.WriteLine("Dispatching added request to Lambda, immediately");

      // Dispatch the request to the lambda
      await this.RunRequest(incomingRequest, incomingResponse, lambdaInstance);
      return;
    }

    Console.WriteLine("No idle lambdas, adding request to the pending queue");

    // If there are no idle lambdas, add the request to the pending queue
    var tcs = new TaskCompletionSource();

    // Add the request to the pending queue
    _pendingRequests.Enqueue((incomingRequest, incomingResponse, tcs));

    // Everytime we add a request to the queue, we start another Lambda

    // Wait for the request to be dispatched or to timeout
    await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(30000));
  }

  public async Task RunRequest(HttpRequest request, HttpResponse response, LambdaInstance lambdaInstance)
  {
    // Send the incoming Request on the lambda's Response
    Console.WriteLine("Sending incoming request to Lambda");

    // Send the headers to the Lambda
    foreach (var header in request.Headers)
    {
      await lambdaInstance.Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes($"{header.Key}: {header.Value}\r\n"));
    }

    // Send the body to the Lambda
    await request.BodyReader.CopyToAsync(lambdaInstance.Response.BodyWriter.AsStream());
    await request.BodyReader.CompleteAsync();

    // Get the response from the lambda request and relay it back to the caller

    // Send the headers to the caller
    foreach (var header in lambdaInstance.Response.Headers)
    {
      response.Headers.Add(header.Key, header.Value);
    }

    // Send the body to the caller
    await lambdaInstance.Request.BodyReader.CopyToAsync(response.BodyWriter.AsStream());

    // Mark that the Response has been sent on the LambdaInstance
    lambdaInstance.TCS.SetResult();
  }

  // Add a new lambda, dispatch to it immediately if a request is waiting
  public async Task AddLambda(LambdaInstance lambdaInstance)
  {
    Console.WriteLine("Adding Lambda to the Dispatcher");

    // Try to get a pending request and dispatch immediately
    if (_pendingRequests.TryDequeue(out var requestItem))
    {
      Console.WriteLine("Dispatching pending request to Lambda, immediately");

      (var request, var response, var tcs) = requestItem;

      // Dispatch the request to the lambda
      await this.RunRequest(request, response, lambdaInstance);

      // Signal the request has been dispatched
      tcs.SetResult();
      return;
    }

    Console.WriteLine("No pending requests, adding Lambda to the idle queue");

    // If there are no pending requests, add the lambda to the idle queue
    _idleLambdas.Enqueue(lambdaInstance);
  }
}