using System.Collections.Concurrent;

namespace PwrDrvr.LambdaDispatch.Router;

public class DispatcherAddConnectionResult
{
  public LambdaConnection? Connection { get; set; }
  public bool ImmediatelyDispatched { get; set; } = false;

  public bool LambdaIDNotFound { get; set; } = false;
}

public class PendingRequest
{
  public HttpRequest Request { get; set; }
  public HttpResponse Response { get; set; }
  public TaskCompletionSource ResponseFinishedTCS { get; set; } = new TaskCompletionSource();
  public DateTime CreationTime { get; }

  public DateTime DispatchTime { get; private set; }

  public void RecordDispatchTime()
  {
    DispatchTime = DateTime.Now;
  }

  public TimeSpan Duration => DispatchTime - CreationTime;

  public PendingRequest(HttpRequest request, HttpResponse response)
  {
    Request = request;
    Response = response;
    CreationTime = DateTime.Now;
    DispatchTime = DateTime.Now;
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

    // Start the background task to process pending requests
    Task.Run(ProcessPendingRequests);
  }

  public async Task CloseInstance(string instanceId)
  {
    _logger.LogDebug("Closing instance {instanceId}", instanceId);

    await _lambdaInstanceManager.CloseInstance(instanceId);
  }

  // Add a new request, dispatch immediately if able
  public async Task AddRequest(HttpRequest incomingRequest, HttpResponse incomingResponse)
  {
    _logger.LogDebug("Adding request to the Dispatcher");

    MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.RequestCount);

    // If no queue and idle lambdas, try to get an idle lambda and dispatch immediately
    if (_pendingRequestCount == 0 && _lambdaInstanceManager.TryGetConnection(out var lambdaConnection))
    {
      _logger.LogDebug("Dispatching incoming request immediately to LambdaId: {Id}", lambdaConnection.Instance.Id);

      MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.ImmediateDispatchCount);
      MetricsRegistry.Metrics.Measure.Histogram.Update(MetricsRegistry.DispatchDelay, 0);

      Interlocked.Increment(ref _runningRequestCount);
      MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.RunningRequests);

      try
      {
        await lambdaConnection.RunRequest(incomingRequest, incomingResponse);
      }
      finally
      {
        Interlocked.Decrement(ref _runningRequestCount);
        MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.RunningRequests);

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
    MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.QueuedRequests);

    // Update number of instances that we want
    await _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount);

    // Wait for the request to be dispatched or to timeout
    // await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(30000));
    await pendingRequest.ResponseFinishedTCS.Task.WaitAsync(TimeSpan.FromMinutes(5));

    MetricsRegistry.Metrics.Measure.Histogram.Update(MetricsRegistry.DispatchDelay, (long)pendingRequest.Duration.TotalMilliseconds);
  }

  // Add a new lambda, dispatch to it immediately if a request is waiting
  public async Task<DispatcherAddConnectionResult> AddConnectionForLambda(HttpRequest request, HttpResponse response, string lambdaId)
  {
    DispatcherAddConnectionResult result = new();

    _logger.LogDebug("Adding Connection for Lambda {lambdaID} to the Dispatcher", lambdaId);

    // Validate that the Lambda ID is valid
    if (string.IsNullOrWhiteSpace(lambdaId))
    {
      _logger.LogError("Lambda ID is blank");
      result.LambdaIDNotFound = true;
      return result;
    }

    if (!_lambdaInstanceManager.ValidateLambdaId(lambdaId))
    {
      _logger.LogError("Lambda ID is not known: {lambdaId}", lambdaId);
      result.LambdaIDNotFound = true;
      return result;
    }

    // We have a valid connection, let's try to dispatch if there is a pending request in the queue
    // Get the pending request
    if (_pendingRequests.TryDequeue(out var pendingRequest))
    {
      _logger.LogDebug("Dispatching pending request to Lambda {lambdaId}", lambdaId);
      Interlocked.Decrement(ref _pendingRequestCount);
      MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.QueuedRequests);
      pendingRequest.RecordDispatchTime();

      MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.PendingDispatchCount);
      MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.PendingDispatchForegroundCount);

      if (pendingRequest.Duration > TimeSpan.FromSeconds(1))
      {
        _logger.LogWarning("Dispatching (foreground) pending request that has been waiting for {duration} ms", pendingRequest.Duration.TotalMilliseconds);
      }

      // Register the connection with the lambda
      var connection = await _lambdaInstanceManager.AddConnectionForLambda(request, response, lambdaId, true);

      if (connection == null)
      {
        _logger.LogError("Failed adding the connection to the Lambda {lambdaId}, putting the request back in the queue", lambdaId);
        _pendingRequests.Enqueue(pendingRequest);
        Interlocked.Increment(ref _pendingRequestCount);
        MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.QueuedRequests);
        return result;
      }

      // If we got a connection, dispatch the request
      // Dispatch the request to the lambda
      Interlocked.Increment(ref _runningRequestCount);
      MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.RunningRequests);
      try
      {
        await connection.RunRequest(pendingRequest.Request, pendingRequest.Response);
      }
      finally
      {
        Interlocked.Decrement(ref _runningRequestCount);
        MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.RunningRequests);

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

  private async Task ProcessPendingRequests()
  {
    while (true)
    {
      if (_pendingRequestCount > 0)
      {
        _logger.LogDebug("ProcessPendingRequests - pendingRequestCount: {pendingRequestCount}", _pendingRequestCount);
      }

      // If there should be pending requests, try to get a connection then grab a request
      // If we can't get a pending request, put the connection back
      while (_pendingRequestCount > 0 && _lambdaInstanceManager.TryGetConnection(out var lambdaConnection))
      {
        if (_pendingRequests.TryDequeue(out var pendingRequest))
        {
          pendingRequest.RecordDispatchTime();
          _logger.LogDebug("Dispatching pending request");

          if (pendingRequest.Duration > TimeSpan.FromSeconds(1))
          {
            _logger.LogWarning("Dispatching (background) pending request that has been waiting for {duration} ms", pendingRequest.Duration.TotalMilliseconds);
          }

          MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.PendingDispatchCount);
          MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.PendingDispatchBackgroundCount);

          Interlocked.Decrement(ref _pendingRequestCount);
          MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.QueuedRequests);

          // Try to get a connection and process the request
          Interlocked.Increment(ref _runningRequestCount);
          MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.RunningRequests);

          try
          {
            await lambdaConnection.RunRequest(pendingRequest.Request, pendingRequest.Response);
          }
          finally
          {
            Interlocked.Decrement(ref _runningRequestCount);
            MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.RunningRequests);

            // Signal the pending request that it's been completed
            pendingRequest.ResponseFinishedTCS.SetResult();

            // Update number of instances that we want
            await _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount);
          }
        }
        else
        {
          // We didn't get a pending request, so put the connection back
          await _lambdaInstanceManager.AddConnectionForLambda(lambdaConnection.Request, lambdaConnection.Response, lambdaConnection.Instance.Id);
        }
      }

      // Wait for a short period before checking again
      await Task.Delay(TimeSpan.FromMilliseconds(100));
    }
  }
}