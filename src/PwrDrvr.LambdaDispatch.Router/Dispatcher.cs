using System.Collections.Concurrent;
using System.Threading.Channels;
using PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;

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

/// <summary>
/// Exposes only the background dispatch function needed by
/// instances when a request completes
/// </summary>
public interface IBackgroundDispatcher
{
  Task<bool> TryBackgroundDispatchOne(bool countAsForeground = false);
}

public class Dispatcher : IBackgroundDispatcher
{
  private readonly ILogger<Dispatcher> _logger;

  private readonly ILambdaInstanceManager _lambdaInstanceManager;

  private readonly IMetricsLogger _metricsLogger;

  // Requests that are waiting to be dispatched to a Lambda
  private volatile int _pendingRequestCount = 0;
  private readonly ConcurrentQueue<PendingRequest> _pendingRequests = new();

  private readonly Channel<int> _pendingRequestSignal = Channel.CreateBounded<int>(new BoundedChannelOptions(1)
  {
    FullMode = BoundedChannelFullMode.DropOldest,
  });

  // We need to keep a count of the running requests so we can set the desired count
  private volatile int _runningRequestCount = 0;

  public Dispatcher(ILogger<Dispatcher> logger, IMetricsLogger metricsLogger, ILambdaInstanceManager lambdaInstanceManager)
  {
    _logger = logger;
    _metricsLogger = metricsLogger;
    _logger.LogDebug("Dispatcher created");
    _lambdaInstanceManager = lambdaInstanceManager;
    _lambdaInstanceManager.AddBackgroundDispatcherReference(this);

    // Start the background task to process pending requests
    Task.Run(BackgroundPendingRequestDispatcher);
  }

  public bool PingInstance(string instanceId)
  {
    var found = _lambdaInstanceManager.ValidateLambdaId(instanceId, out var _);

    _logger.LogDebug("Pinging instance {instanceId}, found: {found}", instanceId, found);

    return found;
  }

  public void CloseInstance(string instanceId, bool lambdaInitiated = false)
  {
    _lambdaInstanceManager.CloseInstance(instanceId, lambdaInitiated);
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
      _metricsLogger.PutMetric("DispatchDelay", 0, Unit.Milliseconds);

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
    Interlocked.Increment(ref _pendingRequestCount);
    _pendingRequestSignal.Writer.TryWrite(1);
    MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.QueuedRequests);

    // Update number of instances that we want
    await _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount);

    // Wait for the request to be dispatched or to timeout
    // await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(30000));
    // TODO: Get this timeout from the config
    await pendingRequest.ResponseFinishedTCS.Task.WaitAsync(TimeSpan.FromMinutes(5));

    MetricsRegistry.Metrics.Measure.Histogram.Update(MetricsRegistry.DispatchDelay, (long)pendingRequest.Duration.TotalMilliseconds);
    _metricsLogger.PutMetric("DispatchDelay", pendingRequest.Duration.TotalMilliseconds, Unit.Milliseconds);
  }

  // Add a new connection for a lambda, dispatch to it immediately if a request is waiting
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

    if (!_lambdaInstanceManager.ValidateLambdaId(lambdaId, out var instance))
    {
      _logger.LogError("Unknown LambdaId: {lambdaId}, ChannelId: {channelId}", lambdaId, channelId);
      result.LambdaIDNotFound = true;
      return result;
    }

    // We have a valid connection
    // But, the instance may be at it's outstanding request limit
    // since we can have more connections then we are allowed to use
    // Check if we are allowed (race condition, sure) to use this connection
    // Let's try to dispatch if there is a pending request in the queue
    // Get the pending request
    if (instance.OutstandingRequestCount < instance.MaxConcurrentCount
        && _pendingRequests.TryDequeue(out var pendingRequest))
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
        Interlocked.Increment(ref _pendingRequestCount);
        _pendingRequestSignal.Writer.TryWrite(1);
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

        // Loop quickly in case we had a race condition on enqueing a pending request
        // This happens when we have a request and a connection both arrive at the same time
        // and there are no other available connections.  They both check for the other, find
        // none, and get put into lists.
        // If another connection comes in it will pickup this pending request, so we only
        // have to cover the case of the brief race
        var tryCount = 0;
        while (
          // We have this check here (not just in the called function)
          // because we want to skip these 10 loops if there are no requests waiting anymore
          _pendingRequestCount > 0
          && !await TryBackgroundDispatchOne()
          && tryCount++ < 10)
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
  /// Check for a pending request and an available connection
  /// Match them up if we find them
  /// </summary>
  /// <returns>Whether a request was dispatched</returns>
  public async Task<bool> TryBackgroundDispatchOne(bool countAsForeground = false)
  {
    var startedRequest = false;

    try
    {
      // If there should be pending requests, try to get a connection then grab a request
      // If we can't get a pending request, put the connection back
      if (_pendingRequestCount > 0)
      {
        // Try to get a connection
        if (!_lambdaInstanceManager.TryGetConnection(out var lambdaConnection, tentative: true))
        {
          _logger.LogDebug("TryBackgroundDispatchOne - No connections available");

          // Start more instances if needed
          await _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount).ConfigureAwait(false);
          return false;
        }

        // Try to get a pending request
        if (_pendingRequests.TryDequeue(out var pendingRequest))
        {
          startedRequest = true;
          pendingRequest.RecordDispatchTime();
          _logger.LogDebug("Dispatching pending request");

          if (pendingRequest.Duration > TimeSpan.FromSeconds(1))
          {
            _logger.LogWarning("Dispatching (background) pending request that has been waiting for {duration} ms", pendingRequest.Duration.TotalMilliseconds);
          }

          // Register that we are going to use this connection
          // This will add the decrement of outstanding connections when complete
          lambdaConnection.Instance.TryGetConnectionWillUse(lambdaConnection);

          MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.PendingDispatchCount);
          if (countAsForeground)
          {
            MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.PendingDispatchForegroundCount);
          }
          else
          {
            MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.PendingDispatchBackgroundCount);
          }

          Interlocked.Decrement(ref _pendingRequestCount);
          MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.QueuedRequests);

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
          _logger.LogDebug("TryBackgroundDispatchOne - No pending requests, putting connection back");

          // We didn't get a pending request, so put the connection back
          await _lambdaInstanceManager.ReenqueueUnusedConnection(lambdaConnection, lambdaConnection.Instance.Id);

          return false;
        }
      }

      return false;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "TryBackgroundDispatchOne - Exception");
      return startedRequest;
    }
  }
}