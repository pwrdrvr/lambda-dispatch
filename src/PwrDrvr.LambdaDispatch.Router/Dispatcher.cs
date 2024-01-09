using System.Collections.Concurrent;
using System.Threading.Channels;

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

  private readonly ILambdaInstanceManager _lambdaInstanceManager;

  // Requests that are waiting to be dispatched to a Lambda
  private volatile int _pendingRequestCount = 0;
  private readonly ConcurrentQueue<PendingRequest> _pendingRequests = new();

  private readonly Channel<int> _pendingRequestSignal = Channel.CreateUnbounded<int>();

  // We need to keep a count of the running requests so we can set the desired count
  private volatile int _runningRequestCount = 0;

  public Dispatcher(ILogger<Dispatcher> logger, ILambdaInstanceManager lambdaInstanceManager)
  {
    _logger = logger;
    _logger.LogDebug("Dispatcher created");
    _lambdaInstanceManager = lambdaInstanceManager;

    // Start the background task to process pending requests
    Task.Run(BackgroundPendingRequestDispatcher);
  }

  public bool PingInstance(string instanceId)
  {
    var found = _lambdaInstanceManager.ValidateLambdaId(instanceId);

    _logger.LogDebug("Pinging instance {instanceId}, found: {found}", instanceId, found);

    return found;
  }

  public void CloseInstance(string instanceId)
  {
    _logger.LogDebug("Closing instance {instanceId}", instanceId);

    _lambdaInstanceManager.CloseInstance(instanceId);
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
        await lambdaConnection.RunRequest(incomingRequest, incomingResponse).ConfigureAwait(false);
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
    _pendingRequestSignal.Writer.TryWrite(1);
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
  public async Task<DispatcherAddConnectionResult> AddConnectionForLambda(HttpRequest request, HttpResponse response, string lambdaId, string channelId)
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

#if TEST_RUNNERS
    if (lambdaId.StartsWith("test"))
    {
      _lambdaInstanceManager.DebugAddInstance(lambdaId);
    }
#endif

    if (!_lambdaInstanceManager.ValidateLambdaId(lambdaId))
    {
      _logger.LogError("Unknown LambdaId: {lambdaId}, ChannelId: {channelId}", lambdaId, channelId);
      result.LambdaIDNotFound = true;
      return result;
    }

    // We have a valid connection, let's try to dispatch if there is a pending request in the queue
    // Get the pending request
    if (_pendingRequests.TryDequeue(out var pendingRequest))
    {
      _logger.LogDebug("Dispatching pending request to LambdaId: {lambdaId}, ChannelId: {channelId}", lambdaId, channelId);
      Interlocked.Decrement(ref _pendingRequestCount);
      MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.QueuedRequests);

      MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.PendingDispatchCount);
      MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.PendingDispatchForegroundCount);

      // Register the connection with the lambda
      var connection = await _lambdaInstanceManager.AddConnectionForLambda(request, response, lambdaId, channelId, true);

      // If the connection returned is null then the Response has already been disposed
      // This will be null if the Lambda was actually gone when we went to add it to the instance manager
      if (connection == null)
      {
        _logger.LogError("Failed adding connection to LambdaId {lambdaId} ChannelId {channelId}, putting the request back in the queue", lambdaId, channelId);
        _pendingRequests.Enqueue(pendingRequest);
        _pendingRequestSignal.Writer.TryWrite(1);
        Interlocked.Increment(ref _pendingRequestCount);
        MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.QueuedRequests);
        MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.PendingDispatchCount);
        MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.PendingDispatchForegroundCount);
        return result;
      }

      // Start the response
      // This sends the headers
      await response.StartAsync();

      // Only at this point are we sure we're going to dispatch
      pendingRequest.RecordDispatchTime();
      if (pendingRequest.Duration > TimeSpan.FromSeconds(1))
      {
        _logger.LogWarning("Dispatching (foreground) pending request that has been waiting for {duration} ms, LambdaId: {lambdaId}, ChannelId: {channelId}", pendingRequest.Duration.TotalMilliseconds, lambdaId, channelId);
      }

      // Dispatch the request to the lambda
      Interlocked.Increment(ref _runningRequestCount);
      MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.RunningRequests);
      try
      {
        await connection.RunRequest(pendingRequest.Request, pendingRequest.Response).ConfigureAwait(false);
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

    //
    // There was no pending request to immediately dispatch, so just add the connection
    //
    // NOTE: As soon as this is called the connection can be taken and used for a request
    // we do not own this request/response anymore after this call
    //
    result.Connection = await _lambdaInstanceManager.AddConnectionForLambda(request, response, lambdaId, channelId);

    return result;
  }

  private async Task BackgroundPendingRequestDispatcher()
  {
    while (true)
    {
      // Wait for the signal or 1 second
      try
      {
        // Create a CancellationToken that will be cancelled after 1 second
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await _pendingRequestSignal.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
        _logger.LogDebug("BackgroundPendingRequestDispatcher - Got signal");

        // Only do this loop if we have pending requests
        if (_pendingRequestCount == 0)
        {
          _logger.LogDebug("BackgroundPendingRequestDispatcher - No pending requests");
          continue;
        }

        // Loop quickly until we dispatch the item that we got a signal for
        // The item can be consumed by an incoming lambda connection, so it might not be there
        var tryCount = 0;
        while (!await TryBackgroundDispatchOne() && tryCount++ < 10)
        {
          _logger.LogDebug("BackgroundPendingRequestDispatcher - Could not dispatch one, trying again");

          await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }
      }
      catch (OperationCanceledException)
      {
        _logger.LogDebug("BackgroundPendingRequestDispatcher - Timed out");

        // We timed out, so try to dispatch one even though we didn't get a signal
        if (await TryBackgroundDispatchOne())
        {
          _logger.LogDebug("BackgroundPendingRequestDispatcher - Dispatched one");
        }
      }
    }
  }

  /// <summary>
  /// 
  /// </summary>
  /// <returns>Whether a request was dispatched</returns>
  private async Task<bool> TryBackgroundDispatchOne()
  {
    // If there should be pending requests, try to get a connection then grab a request
    // If we can't get a pending request, put the connection back
    while (_pendingRequestCount > 0)
    {
      // Try to get a connection
      if (!_lambdaInstanceManager.TryGetConnection(out var lambdaConnection))
      {
        _logger.LogDebug("TryBackgroundDispatchOne - No connections available");

        // Start more instances if needed
        await _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount).ConfigureAwait(false);
        return false;
      }

      // Try to get a pending request
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
          await lambdaConnection.RunRequest(pendingRequest.Request, pendingRequest.Response).ConfigureAwait(false);
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

        return true;
      }
      else
      {
        _logger.LogInformation("TryBackgroundDispatchOne - No pending requests, putting connection back");

        // We didn't get a pending request, so put the connection back
        await _lambdaInstanceManager.ReenqueueUnusedConnection(lambdaConnection, lambdaConnection.Instance.Id);

        return false;
      }
    }

    return false;
  }
}