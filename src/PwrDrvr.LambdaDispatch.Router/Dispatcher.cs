using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Text;

namespace PwrDrvr.LambdaDispatch.Router;

public class DispatcherAddConnectionResult
{
  public LambdaConnection? Connection { get; set; }
  public bool ImmediatelyDispatched { get; set; }
}

public class PendingRequest
{
  public HttpRequest Request { get; set; }
  public HttpResponse Response { get; set; }
  public TaskCompletionSource ResponseFinishedTCS { get; set; } = new TaskCompletionSource();

  public PendingRequest(HttpRequest request, HttpResponse response)
  {
    Request = request;
    Response = response;
  }
}

public class Dispatcher
{
  private readonly ILogger<Dispatcher> _logger;

  private readonly LambdaInstanceManager _lambdaInstanceManager = new(10);

  // Requests that are waiting to be dispatched to a Lambda
  private volatile int _pendingRequestCount = 0;
  private readonly ConcurrentQueue<PendingRequest> _pendingRequests = new();

  // We need to keep a count of the running requests so we can set the desired count
  private volatile int _runningRequestCount = 0;

  public Dispatcher(ILogger<Dispatcher> logger)
  {
    _logger = logger;
    _logger.LogDebug("Dispatcher created");
  }

  // Add a new request, dispatch immediately if able
  public async Task AddRequest(HttpRequest incomingRequest, HttpResponse incomingResponse)
  {
    _logger.LogDebug("Adding request to the Dispatcher");

    // If no queue and idle lambdas, try to get an idle lambda and dispatch immediately
    if (_pendingRequestCount == 0 && _lambdaInstanceManager.TryGetConnection(out var lambdaConnection))
    {
      _logger.LogDebug("Dispatching incoming request immediately to LambdaId: {Id}", lambdaConnection.Instance.Id);

      Interlocked.Increment(ref _runningRequestCount);

      try
      {
        await lambdaConnection.RunRequest(incomingRequest, incomingResponse);
      }
      finally
      {
        Interlocked.Decrement(ref _runningRequestCount);

        // Update number of instances that we want
        await _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount);
      }
      return;
    }

    _logger.LogDebug("No idle lambdas, adding request to the pending queue");

    // If there are no idle lambdas, add the request to the pending queue
    // Add the request to the pending queue
    var pendingRequest = new PendingRequest(incomingRequest, incomingResponse);
    _pendingRequests.Enqueue(pendingRequest);
    Interlocked.Increment(ref _pendingRequestCount);

    // Update number of instances that we want
    await _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount);

    // Wait for the request to be dispatched or to timeout
    // await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(30000));
    await pendingRequest.ResponseFinishedTCS.Task.WaitAsync(TimeSpan.FromMinutes(5));
  }

  // Add a new lambda, dispatch to it immediately if a request is waiting
  public async Task<DispatcherAddConnectionResult> AddConnectionForLambda(HttpRequest request, HttpResponse response, string lambdaId)
  {
    DispatcherAddConnectionResult result = new();
    result.ImmediatelyDispatched = false;

    _logger.LogInformation("Adding Connection for Lambda {lambdaID} to the Dispatcher", lambdaId);

    // Validate that the Lambda ID  is valid
    if (string.IsNullOrWhiteSpace(lambdaId))
    {
      _logger.LogError("Lambda ID is blank");
      return result;
    }

    if (!_lambdaInstanceManager.ValidateLambdaId(lambdaId))
    {
      _logger.LogError("Lambda ID is not known: {lambdaId}", lambdaId);
      return result;
    }

    // We have a valid connection, let's try to dispatch if there is a pending request in the queue
    // Get the pending request
    if (_pendingRequests.TryDequeue(out var pendingRequest))
    {
      _logger.LogDebug("Dispatching pending request to Lambda {lambdaId}", lambdaId);
      Interlocked.Decrement(ref _pendingRequestCount);

      // Get the connection
      var connection = await _lambdaInstanceManager.AddConnectionForLambda(request, response, lambdaId, true);

      if (connection == null)
      {
        throw new Exception("Connection should not be null");
      }

      // If we got a connection, dispatch the request
      // Dispatch the request to the lambda
      Interlocked.Increment(ref _runningRequestCount);
      try
      {
        await connection.RunRequest(pendingRequest.Request, pendingRequest.Response);
      }
      finally
      {
        Interlocked.Decrement(ref _runningRequestCount);

        result.ImmediatelyDispatched = true;
        result.Connection = connection;

        // Signal the pending request that it's been completed
        pendingRequest.ResponseFinishedTCS.SetResult();

         // Update number of instances that we want
        await _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount);
      }

      return result;
    }

    // Note: we cannot immediatley dispatch here because we do not know if the 
    // lambdaId exists, so there are cases that would cause us to pull a request
    // from the queue but then not be able to dispatch it.
    // We wouldn't be able to put the request back at the head of the queue
    // so it would lose it's spot
    result.Connection = await _lambdaInstanceManager.AddConnectionForLambda(request, response, lambdaId);

    return result;
  }
}