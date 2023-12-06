using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Text;

namespace PwrDrvr.LambdaDispatch.Router;

public class Dispatcher
{
  private readonly ILogger<Dispatcher> _logger;

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

  public Dispatcher(ILogger<Dispatcher> logger)
  {
    _logger = logger;
    _logger.LogDebug("Dispatcher created");
  }

  // Add a new request, dispatch immediately if able
  public async Task AddRequest(HttpRequest incomingRequest, HttpResponse incomingResponse)
  {
    _logger.LogDebug("Adding request to the Dispatcher");

    // Try to get an idle lambda and dispatch immediately
    if (_idleLambdas.TryDequeue(out var lambdaInstance))
    {
      _logger.LogDebug("Dispatching added request to Lambda, immediately");

      // Dispatch the request to the lambda
      await this.RunRequest(incomingRequest, incomingResponse, lambdaInstance);
      return;
    }

    _logger.LogDebug("No idle lambdas, adding request to the pending queue");

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
    _logger.LogDebug("Sending incoming request headers to Lambda");

    // TODO: Write the request line
    await lambdaInstance.Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes($"{request.Method} {request.Path} {request.Protocol}\r\n"));

    // Send the headers to the Lambda
    foreach (var header in request.Headers)
    {
      await lambdaInstance.Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes($"{header.Key}: {header.Value}\r\n"));
    }

    // Only copy the request body if the request has a body
    if (request.ContentLength > 0 || (request.Headers.ContainsKey("Transfer-Encoding") && request.Headers["Transfer-Encoding"] == "chunked"))
    {
      {
        await lambdaInstance.Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes("\r\n"));
      }

      _logger.LogDebug("Sending incoming request body to Lambda");

      // Send the body to the Lambda
      await request.BodyReader.CopyToAsync(lambdaInstance.Response.BodyWriter.AsStream());
      await request.BodyReader.CompleteAsync();

      _logger.LogDebug("Finished sending incoming request body to Lambda");
    }

    // Mark that the Request has been sent on the LambdaInstances
    await lambdaInstance.Response.BodyWriter.CompleteAsync();

    // Get the response from the lambda request and relay it back to the caller
    _logger.LogDebug("Finished sending entire request to Lambda");

    //
    //
    // Read response from Lambda and relay back to caller
    //
    //

    _logger.LogDebug("Reading response headers from Lambda");

    // Send the headers to the caller
    // This was reading the response headers from the Lambda
    // foreach (var header in lambdaInstance.Response.Headers)
    // {
    //   // Do not set the status code by adding a header
    //   if (header.Key == "Status-Code")
    //   {
    //     // Set the status code on the response
    //     response.StatusCode = int.Parse(header.Value);
    //     _logger.LogDebug($"Set response status code to {header.Value}");
    //     continue;
    //   }

    //   if (header.Key == "Transfer-Encoding")
    //   {
    //     // Don't send the Transfer-Encoding header
    //     continue;
    //   }
    //   response.Headers.Add(header.Key, header.Value);
    //   _logger.LogDebug($"Sent reponse header to caller: {header.Key}: {header.Value}");
    // }

    _logger.LogDebug("Finished reading response headers from Lambda");

    _logger.LogDebug("Copying response body from from Lambda");

    // Send the body to the caller
    using var lambdaResponseReader = new StreamReader(lambdaInstance.Request.BodyReader.AsStream(), leaveOpen: true);
    string? line;
    // First line should be status
    line = await lambdaResponseReader.ReadLineAsync();
    _logger.LogDebug("Got status line from lambda: {line}", line);
    response.StatusCode = int.Parse(line.Split(' ')[1]);
    while (!string.IsNullOrEmpty(line = await lambdaResponseReader.ReadLineAsync()))
    {
      _logger.LogDebug("Got header line from lambda: {line}", line);

      // Parse the header
      var parts = line.Split(new[] { ": " }, 2, StringSplitOptions.None);
      var key = parts[0];
      // Join all the parts after the first one
      var value = string.Join(": ", parts.Skip(1));
      if (key == "Transfer-Encoding")
      {
        // Don't set the Transfer-Encoding header as it breaks the response
        continue;
      }

      // Set the header on the Kestrel response
      response.Headers[parts[0]] = parts[1];
    }

    while ((line = await lambdaResponseReader.ReadLineAsync()) != null)
    {
      _logger.LogDebug("Got body line from lambda: {line}", line);
      await response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes(line));
      await response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes("\r\n"));
    }

    // await lambdaInstance.Request.BodyReader.CopyToAsync(response.BodyWriter.AsStream());

    _logger.LogDebug("Copied response body from from Lambda");

    await response.BodyWriter.CompleteAsync();

    // Mark that the Response has been sent on the LambdaInstance
    lambdaInstance.TCS.SetResult();
  }

  // Add a new lambda, dispatch to it immediately if a request is waiting
  public async Task AddLambda(LambdaInstance lambdaInstance)
  {
    _logger.LogInformation("Adding Lambda to the Dispatcher");

    // Try to get a pending request and dispatch immediately
    if (_pendingRequests.TryDequeue(out var requestItem))
    {
      _logger.LogDebug("Dispatching pending request to Lambda, immediately");

      (var request, var response, var tcs) = requestItem;

      // Dispatch the request to the lambda
      await this.RunRequest(request, response, lambdaInstance);

      // Signal the request has been dispatched
      tcs.SetResult();
      return;
    }

    _logger.LogDebug("No pending requests, adding Lambda to the idle queue");

    // If there are no pending requests, add the lambda to the idle queue
    _idleLambdas.Enqueue(lambdaInstance);
  }
}