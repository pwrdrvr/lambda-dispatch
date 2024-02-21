using System.Collections.Concurrent;
using System.Diagnostics;
using PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;

namespace PwrDrvr.LambdaDispatch.Router;

public class DispatcherAddConnectionResult
{
  public LambdaConnection? Connection { get; set; }
  public bool ImmediatelyDispatched { get; set; } = false;

  public bool LambdaIDNotFound { get; set; } = false;
}

/// <summary>
/// Exposes only the background dispatch function needed by
/// instances when a request completes
/// </summary>
public interface IBackgroundDispatcher
{
  void WakeupBackgroundDispatcher(LambdaConnection? connection);
}

public class Dispatcher : IBackgroundDispatcher
{
  private readonly ILogger<Dispatcher> _logger;

  private readonly ILambdaInstanceManager _lambdaInstanceManager;

  private readonly IMetricsLogger _metricsLogger;

  private readonly WeightedAverage _incomingRequestsWeightedAverage = new(15);

  // NOTE: Microseconds since this can only store longs
  private readonly WeightedAverage _incomingRequestDurationAverage = new(15, mean: true);

  // Requests that are waiting to be dispatched to a Lambda
  private volatile int _pendingRequestCount = 0;

  /// <summary>
  /// Requests that are waiting for a connection
  /// </summary>
  private readonly BlockingCollection<PendingRequest> _pendingRequests = [];

  /// <summary>
  /// All connections in this queue should be available for use, marked as in use, but not yet confirmed
  /// to be used.
  /// 
  /// The background dispatcher will pick these up and either use them or add them to the LOQ using
  /// ReenqueueUnusedConnection() in that case.
  /// </summary>
  private readonly BlockingCollection<LambdaConnection?> _newConnections = [];

  private readonly IShutdownSignal _shutdownSignal;

  // We need to keep a count of the running requests so we can set the desired count
  private volatile int _runningRequestCount = 0;

  public Dispatcher(ILogger<Dispatcher> logger, IMetricsLogger metricsLogger, ILambdaInstanceManager lambdaInstanceManager, IShutdownSignal shutdownSignal)
  {
    _logger = logger;
    _metricsLogger = metricsLogger;
    _logger.LogDebug("Dispatcher created");
    _lambdaInstanceManager = lambdaInstanceManager;
    _shutdownSignal = shutdownSignal;
    _lambdaInstanceManager.AddBackgroundDispatcherReference(this);

    // Start the background task to process pending requests
    // This needs it's own thread because BlockingCollection will block the thread
    // and we don't want to block a ThreadPool worker thread (which we are limiting)
    new Thread(BackgroundPendingRequestDispatcher).Start();
  }

  public bool PingInstance(string instanceId)
  {
    var found = _lambdaInstanceManager.ValidateLambdaId(instanceId, out var _);

    _logger.LogDebug("Pinging instance {instanceId}, found: {found}", instanceId, found);

    return found;
  }

  public async Task CloseInstance(string instanceId, bool lambdaInitiated = false)
  {
    if (_lambdaInstanceManager.ValidateLambdaId(instanceId, out var instance))
    {
      await _lambdaInstanceManager.CloseInstance(instance, lambdaInitiated);
    }
  }

  // Add a new request, dispatch immediately if able
  public async Task AddRequest(HttpRequest incomingRequest, HttpResponse incomingResponse)
  {
    _logger.LogDebug("Adding request to the Dispatcher");

    MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.RequestCount);
    MetricsRegistry.Metrics.Measure.Meter.Mark(MetricsRegistry.IncomingRequestsMeter, 1);
    _incomingRequestsWeightedAverage.Add();
    MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.IncomingRequestRPS, _incomingRequestsWeightedAverage.EWMA);

    // If idle lambdas, try to get an idle lambda and dispatch immediately
    // If there is a queue, they are going to see the new connections before us anyway
    // If we get a connection from the queue, it's fair game
    if (_lambdaInstanceManager.TryGetConnection(out var lambdaConnection, tentative: false))
    {
      var sw = Stopwatch.StartNew();
      _logger.LogDebug("Dispatching incoming request immediately to LambdaId: {Id}", lambdaConnection.Instance.Id);

      MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.ImmediateDispatchCount);
      // Recording 0 dispatch delays skews the stats about
      // requests that actually encounted a delay
      // MetricsRegistry.Metrics.Measure.Histogram.Update(MetricsRegistry.DispatchDelay, 0);
      // _metricsLogger.PutMetric("DispatchDelay", 0, Unit.Milliseconds);

      Interlocked.Increment(ref _runningRequestCount);
      MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.RunningRequests);

      // Tell the scaler we're running more requests now
      _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount, _incomingRequestsWeightedAverage.EWMA, _incomingRequestDurationAverage.EWMA / 1000);

      try
      {
        await lambdaConnection.RunRequest(incomingRequest, incomingResponse).ConfigureAwait(false);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Dispatcher.AddRequest - Exception while running request");
        try
        {
          incomingResponse.ContentType = "text/plain";
          incomingResponse.Headers.Append("Server", "PwrDrvr.LambdaDispatch.Router");

          if (ex is TimeoutException)
          {
            incomingResponse.StatusCode = StatusCodes.Status504GatewayTimeout;
            await incomingResponse.WriteAsync("Gateway timeout");
          }
          else
          {
            incomingResponse.StatusCode = StatusCodes.Status502BadGateway;
            await incomingResponse.WriteAsync("Bad gateway");
          }
        }
        catch
        {
          // This can throw if the request/response have already been sent/aborted
          try { incomingResponse.HttpContext.Abort(); } catch { }
        }
      }
      finally
      {
        Interlocked.Decrement(ref _runningRequestCount);
        MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.RunningRequests);

        _incomingRequestDurationAverage.Add((long)sw.Elapsed.TotalMilliseconds * 1000);
        MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.IncomingRequestDurationEWMA, _incomingRequestDurationAverage.EWMA / 1000);
        MetricsRegistry.Metrics.Measure.Histogram.Update(MetricsRegistry.IncomingRequestDuration, sw.ElapsedMilliseconds);
        MetricsRegistry.Metrics.Measure.Histogram.Update(MetricsRegistry.IncomingRequestDurationAfterDispatch, sw.ElapsedMilliseconds);
        _metricsLogger.PutMetric("IncomingRequestDuration", Math.Round(sw.Elapsed.TotalMilliseconds, 1), Unit.Milliseconds);
        _metricsLogger.PutMetric("IncomingRequestDurationAfterDispatch", Math.Round(sw.Elapsed.TotalMilliseconds, 1), Unit.Milliseconds);

        // Tell the scaler about the lowered request count
        _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount, _incomingRequestsWeightedAverage.EWMA, _incomingRequestDurationAverage.EWMA / 1000);
      }
      return;
    }

    _logger.LogDebug("No idle lambdas, adding request to the pending queue");

    // If there are no idle lambdas, add the request to the pending queue
    // Add the request to the pending queue
    var pendingRequest = new PendingRequest(incomingRequest, incomingResponse);
    _pendingRequests.Add(pendingRequest);
    Interlocked.Increment(ref _pendingRequestCount);
    MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.QueuedRequests);

    // Update number of instances that we want
    _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount, _incomingRequestsWeightedAverage.EWMA, _incomingRequestDurationAverage.EWMA / 1000);

    // Wait for the request to be dispatched or to timeout
    // TODO: Get this timeout from the config
    try
    {
      //
      // This waits for the background dispatcher to, maybe, pickup the request
      //
      await pendingRequest.ResponseFinishedTCS.Task.WaitAsync(TimeSpan.FromSeconds(120));
    }
    catch (Exception ex)
    {
      // Mark the request as aborted
      pendingRequest.Abort();

      try
      {
        incomingResponse.ContentType = "text/plain";
        incomingResponse.Headers.Append("Server", "PwrDrvr.LambdaDispatch.Router");

        if (ex is TimeoutException)
        {
          incomingResponse.StatusCode = StatusCodes.Status504GatewayTimeout;
          await incomingResponse.WriteAsync("Gateway timeout");
        }
        else
        {
          incomingResponse.StatusCode = StatusCodes.Status500InternalServerError;
          await incomingResponse.WriteAsync("Internal server error");
        }
      }
      catch
      {
        // This can throw if the request/response have already been sent/aborted
        try { incomingResponse.HttpContext.Abort(); } catch { }
      }
    }
  }

  // Add a new connection for a lambda, dispatch to it immediately if a request is waiting
  public async Task<DispatcherAddConnectionResult> AddConnectionForLambda(HttpRequest request, HttpResponse response, string lambdaId, string channelId)
  {
    _logger.LogDebug("Adding Connection for Lambda {lambdaID} to the Dispatcher", lambdaId);

    // Validate that the Lambda ID is valid
    if (string.IsNullOrWhiteSpace(lambdaId))
    {
      _logger.LogError("Lambda ID is blank");
      return new DispatcherAddConnectionResult { LambdaIDNotFound = true };
    }

#if TEST_RUNNERS
    if (lambdaId.StartsWith("test"))
    {
      _lambdaInstanceManager.DebugAddInstance(lambdaId);
    }
#endif

    if (!_lambdaInstanceManager.ValidateLambdaId(lambdaId, out var instance))
    {
      _logger.LogDebug("Unknown LambdaId: {lambdaId}, ChannelId: {channelId}", lambdaId, channelId);
      return new DispatcherAddConnectionResult { LambdaIDNotFound = true };
    }

    // Register the connection with the lambda
    var addConnectionResult = await _lambdaInstanceManager.AddConnectionForLambda(request, response, lambdaId, channelId, AddConnectionDispatchMode.TentativeDispatch);

    if (addConnectionResult.WasRejected || addConnectionResult.Connection == null)
    {
      _logger.LogDebug("Failed adding connection - Lambda not known or closed, LambdaId {lambdaId} ChannelId {channelId}, putting the request back in the queue", lambdaId, channelId);
      return new DispatcherAddConnectionResult { LambdaIDNotFound = true };
    }

    // Tell the scaler about the number of running instances
    if (addConnectionResult.Connection.FirstConnectionForInstance)
    {
      _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount, _incomingRequestsWeightedAverage.EWMA, _incomingRequestDurationAverage.EWMA / 1000);
    }

    // We have a valid connection
    // But, the instance may be at it's outstanding request limit
    // since we can have more connections than we are allowed to use
    // Check if we are allowed (race condition, sure) to use this connection
    // Let's try to dispatch if there is a pending request in the queue
    if (_pendingRequestCount > 0 && addConnectionResult.CanUseNow)
    {
      if (TryGetPendingRequestAndDispatch(addConnectionResult.Connection))
      {
        MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.PendingDispatchForegroundCount);
        return new DispatcherAddConnectionResult { ImmediatelyDispatched = true, Connection = addConnectionResult.Connection };
      }

      // Have to return here, else connection gets added twice below
      return new DispatcherAddConnectionResult { ImmediatelyDispatched = false, Connection = addConnectionResult.Connection };
    }

    // Pass the connection through the background dispatcher
    if (addConnectionResult.CanUseNow)
    {
      _newConnections.Add(addConnectionResult.Connection);
    }

    return new DispatcherAddConnectionResult { ImmediatelyDispatched = false, Connection = addConnectionResult.Connection };
  }

  /// <summary>
  /// Passes a connection through the background dispatcher when a Lambda Instance
  /// sees a completed request that exposes an existing unused / hidden connection
  /// </summary>
  /// <param name="lambdaConnection"></param>
  public void WakeupBackgroundDispatcher(LambdaConnection? lambdaConnection)
  {
    // Yes, it is a race condition, but it doesn't matter because the
    // background dispatcher will check it shortly after
    if (_pendingRequestCount != 0)
    {
      _newConnections.Add(lambdaConnection);
    }
  }

  /// <summary>
  /// Dispatch pending requests to Lambdas in the background
  /// 
  /// All incoming connections pass through here
  /// </summary>
  private void BackgroundPendingRequestDispatcher()
  {
    var capacityMessageInterval = TimeSpan.FromMilliseconds(250);
    var swLastCapacityMessage = Stopwatch.StartNew();

    while (true)
    {
      try
      {
        // This blocks until a connection is available
        // or the timeout is hit
        if (_newConnections.TryTake(out var connection, 20, _shutdownSignal.Shutdown.Token) && connection != null)
        {
          _logger.LogDebug("BackgroundPendingRequestDispatcher - Got a connection for LambdaId {}, ChannelId {}",
            connection.Instance.Id, connection.ChannelId);
          if (TryGetPendingRequestAndDispatch(connection))
          {
            MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.PendingDispatchBackgroundCount);
            swLastCapacityMessage.Restart();
          }
        }
        else
        {
          // The LOQ may have a connection if ChannelCount > MaxConcurrentCount
          // and a response has been completed
          // We get woken up here when a connection is added to the LOQ, so let's check
          var anyDispatched = false;
          while (_lambdaInstanceManager.TryGetConnection(out connection, tentative: true)
                  && TryGetPendingRequestAndDispatch(connection))
          {
            anyDispatched = true;
            MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.PendingDispatchBackgroundCount);
          }

          if (anyDispatched)
          {
            // We dispatched some requests, so we should check the capacity
            swLastCapacityMessage.Restart();
          }
          else if (swLastCapacityMessage.Elapsed > capacityMessageInterval)
          {
            MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.IncomingRequestDurationEWMA, _incomingRequestDurationAverage.EWMA / 1000);
            MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.IncomingRequestRPS, _incomingRequestDurationAverage.EWMA);
            _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount, _incomingRequestsWeightedAverage.EWMA, _incomingRequestDurationAverage.EWMA / 1000);
            swLastCapacityMessage.Restart();
          }
        }
      }
      catch (OperationCanceledException)
      {
        _logger.LogInformation("BackgroundPendingRequestDispatcher - Exiting Loop");
        return;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "BackgroundPendingRequestDispatcher - Exception");
      }
    }
  }

  /// <summary>
  /// Get a pending request and dispatch it to a Lambda
  ///
  /// Assumes that the connection is marked for TentativeDispatch
  /// 
  /// Re-enqueues the tentative connection if it's not used
  /// 
  /// Handles adjusting all counts
  /// </summary>
  private bool TryGetPendingRequestAndDispatch(LambdaConnection connection)
  {
    var dispatchedRequest = false;

    // Try to dispatch a pending request
    while (_pendingRequests.TryTake(out var pendingRequest))
    {
      // Check if the pending request is already canceled
      if (pendingRequest.GatewayTimeoutCTS.IsCancellationRequested)
      {
        // The pending request at front of queue was canceled, we're removing it
        Interlocked.Decrement(ref _pendingRequestCount);
        MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.QueuedRequests);
        _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount, _incomingRequestsWeightedAverage.EWMA, _incomingRequestDurationAverage.EWMA / 1000);

        // Try to find another request
        continue;
      }

      _logger.LogDebug("BackgroundPendingRequestDispatcher - Got a pending request, dispatching to LambdaId {}, ChannelId {}", connection.Instance.Id, connection.ChannelId);

      // We've got a good request and a good connection
      dispatchedRequest = true;
      TryBackgroundDispatchOne(pendingRequest, connection);
      break;
    }

    // Add the connection to the LOQ since we didn't use it
    if (!dispatchedRequest)
    {
      _logger.LogDebug("BackgroundPendingRequestDispatcher - Reenqueuing unused connection for LambdaId {}, ChannelId {}", connection.Instance.Id, connection.ChannelId);
      _lambdaInstanceManager.ReenqueueUnusedConnection(connection, connection.Instance.Id);
    }

    return dispatchedRequest;
  }

  /// <summary>
  /// Dispatch a single pending request to a Lambda
  /// 
  /// Handles adjusting all counts
  /// </summary>
  /// <returns>Whether a request was dispatched</returns>
  private bool TryBackgroundDispatchOne(PendingRequest pendingRequest, LambdaConnection lambdaConnection)
  {
    var startedRequest = false;

    try
    {
      startedRequest = true;
      pendingRequest.RecordDispatchTime();
      _logger.LogDebug("Dispatching pending request");
      MetricsRegistry.Metrics.Measure.Histogram.Update(MetricsRegistry.DispatchDelay, (long)pendingRequest.DispatchDelay.TotalMilliseconds);
      _metricsLogger.PutMetric("DispatchDelay", Math.Round(pendingRequest.DispatchDelay.TotalMilliseconds, 1), Unit.Milliseconds);
      if (pendingRequest.DispatchDelay > TimeSpan.FromSeconds(1))
      {
        _logger.LogWarning("Dispatching (background) pending request that has been waiting for {duration} ms", pendingRequest.DispatchDelay.TotalMilliseconds);
      }

      // Register that we are going to use this connection
      // This will add the decrement of outstanding connections when complete
      lambdaConnection.Instance.TryGetConnectionWillUse(lambdaConnection);

      MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.PendingDispatchCount);

      Interlocked.Decrement(ref _pendingRequestCount);
      MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.QueuedRequests);

      Interlocked.Increment(ref _runningRequestCount);
      MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.RunningRequests);

      // Update number of instances that we want
      _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount, _incomingRequestsWeightedAverage.EWMA, _incomingRequestDurationAverage.EWMA / 1000);

      // Do not await this, let it loop around
      _ = lambdaConnection.RunRequest(pendingRequest.Request, pendingRequest.Response).ContinueWith(async Task (task) =>
      {
        Interlocked.Decrement(ref _runningRequestCount);
        MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.RunningRequests);

        // Signal the pending request that it's been completed
        pendingRequest.ResponseFinishedTCS.SetResult();

        // Record the duration
        _incomingRequestDurationAverage.Add((long)pendingRequest.Duration.TotalMilliseconds * 1000);
        MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.IncomingRequestDurationEWMA, _incomingRequestDurationAverage.EWMA / 1000);
        MetricsRegistry.Metrics.Measure.Histogram.Update(MetricsRegistry.IncomingRequestDuration, (long)pendingRequest.Duration.TotalMilliseconds);
        MetricsRegistry.Metrics.Measure.Histogram.Update(MetricsRegistry.IncomingRequestDurationAfterDispatch, (long)(pendingRequest.Duration.TotalMilliseconds - pendingRequest.DispatchDelay.TotalMilliseconds));
        _metricsLogger.PutMetric("IncomingRequestDuration", Math.Round(pendingRequest.Duration.TotalMilliseconds, 1), Unit.Milliseconds);
        _metricsLogger.PutMetric("IncomingRequestDurationAfterDispatch", Math.Round(pendingRequest.Duration.TotalMilliseconds - pendingRequest.DispatchDelay.TotalMilliseconds, 1), Unit.Milliseconds);

        // Update number of instances that we want
        _lambdaInstanceManager.UpdateDesiredCapacity(_pendingRequestCount, _runningRequestCount, _incomingRequestsWeightedAverage.EWMA, _incomingRequestDurationAverage.EWMA / 1000);

        // Handle the exception
        if (task.IsFaulted)
        {
          try
          {
            pendingRequest.Response.ContentType = "text/plain";
            pendingRequest.Response.Headers.Append("Server", "PwrDrvr.LambdaDispatch.Router");

            if (task.Exception.InnerExceptions.Any(e => e is TimeoutException))
            {
              pendingRequest.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
              await pendingRequest.Response.WriteAsync("Gateway timeout");
            }
            else
            {
              pendingRequest.Response.StatusCode = StatusCodes.Status502BadGateway;
              await pendingRequest.Response.WriteAsync("Bad gateway");
            }
          }
          catch
          {
            // This can throw if the request/response have already been sent/aborted
            try { pendingRequest.Response.HttpContext.Abort(); } catch { }
          }
        }
      }).ConfigureAwait(false);

      return startedRequest;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "TryBackgroundDispatchOne - Exception");
      return startedRequest;
    }
  }
}