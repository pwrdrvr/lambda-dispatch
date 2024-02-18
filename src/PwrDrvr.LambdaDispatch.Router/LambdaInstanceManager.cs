namespace PwrDrvr.LambdaDispatch.Router;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;

public struct LambdaInstanceCapacityMessage
{
  public int PendingRequests { get; set; }
  public int RunningRequests { get; set; }

  public double RequestsPerSecondEWMA { get; set; }

  public double RequestDurationEWMA { get; set; }
}

public interface ILambdaInstanceManager
{
  void AddBackgroundDispatcherReference(IBackgroundDispatcher dispatcher);

  bool TryGetConnection([NotNullWhen(true)] out LambdaConnection? connection, bool tentative = false);

  bool ValidateLambdaId(string lambdaId, [NotNullWhen(true)] out ILambdaInstance? instance);

  void ReenqueueUnusedConnection(LambdaConnection connection, string lambdaId);

  Task<LambdaConnection?> AddConnectionForLambda(HttpRequest request, HttpResponse response, string lambdaId, string channelId, AddConnectionDispatchMode dispatchMode = AddConnectionDispatchMode.Enqueue);

#if TEST_RUNNERS
  void DebugAddInstance(string instanceId);
#endif

  void UpdateDesiredCapacity(int pendingRequests, int runningRequests, double requestsPerSecondEWMA, double requestDurationEWMA);

  Task CloseInstance(ILambdaInstance instance, bool lambdaInitiated = false);
}

public class LambdaInstanceManager : ILambdaInstanceManager
{
  private readonly ILogger<LambdaInstanceManager> _logger = LoggerInstance.CreateLogger<LambdaInstanceManager>();

  private readonly CapacityManager _capacityManager;

  private readonly LeastOutstandingQueue _leastOutstandingQueue;

  private IBackgroundDispatcher? _dispatcher;

  /// <summary>
  /// Lock around instance counts
  /// </summary>
  private readonly object _instanceCountLock = new();

  /// <summary>
  /// Number of instances that are desired
  /// </summary>
  private int _desiredInstanceCount = 0;

  /// <summary>
  /// Count of instances that running (connected, not starting, not stopping)
  /// </summary>
  private int _runningInstanceCount = 0;

  /// <summary>
  /// Count of instances that are starting
  /// </summary>
  private int _startingInstanceCount = 0;

  /// <summary>
  /// Count of instances that have called Close() but are still invoked
  /// We subtract this off the running+starting count to determine if we can
  /// allow an overlapping invoke while the still-running lambda is closing
  /// </summary>
  private int _stoppingInstanceCount = 0;

  private readonly int _maxConcurrentCount;

  private readonly int _instanceCountMultiplier;

  private readonly int _channelCount;

  private readonly string _functionName;

  private readonly string? _functionNameQualifier;

  private readonly IMetricsLogger _metricsLogger;

  private readonly Channel<LambdaInstanceCapacityMessage> _capacityChannel
    = Channel.CreateBounded<LambdaInstanceCapacityMessage>(new BoundedChannelOptions(100)
    {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true,
    });

  /// <summary>
  /// Used to lookup instances by ID
  /// This allows associating the connecitons with their owning lambda instance
  /// </summary>
  private readonly ConcurrentDictionary<string, ILambdaInstance> _instances = new();

  public LambdaInstanceManager(IConfig config, IMetricsLogger metricsLogger)
  {
    _instanceCountMultiplier = config.InstanceCountMultiplier;
    _maxConcurrentCount = config.MaxConcurrentCount;
    _channelCount = config.ChannelCount;
    _leastOutstandingQueue = new(_maxConcurrentCount);
    _functionName = config.FunctionNameOnly;
    _functionNameQualifier = config.FunctionNameQualifier;
    _metricsLogger = metricsLogger;
    _capacityManager = new(_maxConcurrentCount, _instanceCountMultiplier);

    // Start the capacity manager
    Task.Factory.StartNew(ManageCapacity, TaskCreationOptions.LongRunning);
  }

  public void AddBackgroundDispatcherReference(IBackgroundDispatcher dispatcher)
  {
    _dispatcher = dispatcher;
  }

  public bool TryGetConnection([NotNullWhen(true)] out LambdaConnection? connection, bool tentative = false)
  {
    // Return an available instance or start a new one if none are available
    var gotConnection = _leastOutstandingQueue.TryGetLeastOustandingConnection(out var dequeuedConnection, tentative);
    connection = dequeuedConnection;

    return gotConnection;
  }

  public bool ValidateLambdaId(string lambdaId, [NotNullWhen(true)] out ILambdaInstance? instance)
  {
    return _instances.TryGetValue(lambdaId, out instance);
  }

  public void ReenqueueUnusedConnection(LambdaConnection connection, string lambdaId)
  {
    // Get the instance for the lambda
    if (_instances.TryGetValue(lambdaId, out var instance))
    {
      // Add the connection to the instance
      // The instance will eventually get rebalanced in the least outstanding queue
      instance.ReenqueueUnusedConnection(connection);

      // Put this instance back into rotation if it was busy
      _leastOutstandingQueue.ReinstateFullInstance(instance);
    }
    else
    {
      _logger.LogWarning("ReenqueueUnusedConnection - Connection added to Lambda that does not exist - closing with 409, LambdaId: {LambdaId}, ChannelId: {ChannelId}", lambdaId, connection.ChannelId);
      _ = connection.Discard();
    }
  }

  public async Task<LambdaConnection?> AddConnectionForLambda(HttpRequest request, HttpResponse response, string lambdaId, string channelId, AddConnectionDispatchMode dispatchMode = AddConnectionDispatchMode.Enqueue)
  {
    // Get the instance for the lambda
    if (_instances.TryGetValue(lambdaId, out var instance))
    {
      // Add the connection to the instance
      // The instance will eventually get rebalanced in the least outstanding queue
      var connection = await instance.AddConnection(request, response, channelId, dispatchMode).ConfigureAwait(false);

      // Check where this instance is in the least outstanding queue
      if (dispatchMode == AddConnectionDispatchMode.Enqueue)
      {
        _leastOutstandingQueue.ReinstateFullInstance(instance);
      }

      return connection;
    }

    _logger.LogWarning("AddConnectionForLambda - Connection added to Lambda that does not exist - closing with 409, LambdaId: {LambdaId}, ChannelId: {ChannelId}", lambdaId, channelId);

    // Close the connection
    try
    {
      response.StatusCode = 409;
      await response.StartAsync();
      await response.WriteAsync("Lambda instance does not exist");
      await response.CompleteAsync();
      try { await request.BodyReader.CopyToAsync(Stream.Null); } catch { }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "AddConnectionForLambda - Exception closing down connection to LambdaId: {LambdaId}, ChannelId: {ChannelId}", lambdaId, channelId);
    }

    return null;
  }

  private async Task ManageCapacity()
  {
    Dictionary<string, ILambdaInstance> stoppingInstances = [];
    var trailingAverage = new TrailingAverage();
    var scaleTokenBucket = new TokenBucket(2, TimeSpan.FromMilliseconds(1000));
    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    int? deferredScaleInNewDesiredInstanceCount = null;
    const int maxScaleOut = 5;
    const double maxScaleOutPercent = .33;
    const double maxScaleInPercent = .33;

    while (true)
    {
      // Setup a timer to run every 5 seconds or when we are asked to increase capacity
      try
      {
        // Cleanup any stopping instances
        foreach (var stoppingInstance in stoppingInstances)
        {
          if (stoppingInstance.Value.InvokeCompletionTask.IsCompleted)
          {
            stoppingInstances.Remove(stoppingInstance.Key);
          }
        }

        //
        // Only a single instance of this will run at a time
        //
        var message = await _capacityChannel.Reader.ReadAsync(cts.Token);
        var pendingRequests = message.PendingRequests;
        var runningRequests = message.RunningRequests;
        var requestsPerSecondEWMA = message.RequestsPerSecondEWMA;
        var requestDurationEWMA = message.RequestDurationEWMA;

        trailingAverage.Add(_capacityManager.SimpleDesiredInstanceCount(pendingRequests, runningRequests));

        // Rip through any other messages, up to a limit of 100
        // But keep reading if we keep ending on a message with no pending or running requests
        var messagesToRead = 100;
        while (_capacityChannel.Reader.TryRead(out var nextMessage)
          && (--messagesToRead > 0 || nextMessage.PendingRequests == 0 && nextMessage.RunningRequests == 0))
        {
          pendingRequests = nextMessage.PendingRequests;
          runningRequests = nextMessage.RunningRequests;
          requestsPerSecondEWMA = nextMessage.RequestsPerSecondEWMA;
          requestDurationEWMA = nextMessage.RequestDurationEWMA;

          trailingAverage.Add(_capacityManager.SimpleDesiredInstanceCount(pendingRequests, runningRequests));
        }

        var averageCount = trailingAverage.Average;
        // If the entire average count is 0 then we want 0 instances
        // OR: If there we read all the messages and there are no pending or running requests
        //     in the last read message then we want 0 instances - this helps avoid
        //     5 second waits to stop instances when load disappears
        var simpleScalerDesiredInstanceCount = _capacityManager.SimpleDesiredInstanceCount(pendingRequests, runningRequests);
        var newDesiredInstanceCount = simpleScalerDesiredInstanceCount;

        // Override the simple scale-from zero logic (which uses running and pending requests only)
        // with the EWMA data, if available
        int? ewmaScalerDesiredInstanceCount = null;
        if (requestsPerSecondEWMA > 0 && requestDurationEWMA > 0)
        {
          newDesiredInstanceCount = _capacityManager.EwmaDesiredInstanceCount(requestsPerSecondEWMA, requestDurationEWMA);
          ewmaScalerDesiredInstanceCount = newDesiredInstanceCount;
        }

        //
        // Locking instance counts - Do not do anything Async / IO in this block
        //
        lock (_instanceCountLock)
        {
          // Calculate the maximum allowed change
          int maxScaleOutChange = Math.Max(maxScaleOut, (int)Math.Ceiling(_desiredInstanceCount * maxScaleOutPercent));
          int maxScaleInChange = Math.Max(maxScaleOut, (int)Math.Ceiling(_desiredInstanceCount * maxScaleInPercent));

          // Calculate the proposed change
          int proposedChange = newDesiredInstanceCount - _desiredInstanceCount;

          // If the proposed change is greater than the maximum allowed change, adjust newDesiredInstanceCount
          if (proposedChange > maxScaleOutChange)
          {
            newDesiredInstanceCount = _desiredInstanceCount + maxScaleOutChange;
          }

          // If the proposed change is less than the negative of the maximum allowed change, adjust newDesiredInstanceCount
          if (proposedChange < -maxScaleInChange)
          {
            newDesiredInstanceCount = _desiredInstanceCount - maxScaleInChange;
          }

          // If we are already at the desired count and we have no starting instances, then we are done
          if (!deferredScaleInNewDesiredInstanceCount.HasValue
              && newDesiredInstanceCount == _desiredInstanceCount
              && _startingInstanceCount + _runningInstanceCount - _stoppingInstanceCount <= _desiredInstanceCount)
          {
            // Clear any deferred scale down
            deferredScaleInNewDesiredInstanceCount = null;

            // Restore any missing instances
            if (_startingInstanceCount + _runningInstanceCount - _stoppingInstanceCount < _desiredInstanceCount)
            {
              _logger.LogInformation("ManageCapacity - Desired count unchanged, restoring {} missing instances: _startingInstanceCount {} + _runningInstanceCount {} - _stoppingInstanceCount {} -> _desiredInstanceCount {}",
                     _desiredInstanceCount - (_startingInstanceCount + _runningInstanceCount - _stoppingInstanceCount), _startingInstanceCount, _runningInstanceCount, _stoppingInstanceCount, _desiredInstanceCount); ;

              while (_startingInstanceCount + _runningInstanceCount - _stoppingInstanceCount < _desiredInstanceCount)
              {
                // Start a new instance
                StartNewInstance();
              }
            }

            // Nothing to do
            continue;
          }

          if (deferredScaleInNewDesiredInstanceCount.HasValue
            && deferredScaleInNewDesiredInstanceCount.Value == newDesiredInstanceCount)
          {
            // We are already scheduled to scale to this count
            continue;
          }

          // The current scale is already correct
          if (newDesiredInstanceCount == _desiredInstanceCount)
          {
            // The new desired count is the same as the current count
            // We have no need to scale down if we were going to
            // We also have no need to scale up
            deferredScaleInNewDesiredInstanceCount = null;
            continue;
          }

          // See if we are allowed to adjust the desired count
          // We are always allowed to scale out from 0 without consuming a token
          // We are always allowed to scale in to 0 without consuming a token
          var allowScaleWithoutToken = _desiredInstanceCount == 0 && newDesiredInstanceCount > 0;
          if (allowScaleWithoutToken
              // Allowing unlimited ins gets way too chatty
              // || (_desiredInstanceCount > 0 && newDesiredInstanceCount == 0)
              || scaleTokenBucket.TryGetToken())
          {
            var prevDesiredInstanceCount = _desiredInstanceCount;

            _metricsLogger.PutMetric("PendingRequestCount", pendingRequests, Unit.Count);
            _metricsLogger.PutMetric("RunningRequestCount", runningRequests, Unit.Count);

            // Start instances if needed
            if (newDesiredInstanceCount > _desiredInstanceCount)
            {
              _logger.LogInformation("ManageCapacity - Performing scale out: _desiredInstanceCount {_desiredInstanceCount} -> {newDesiredInstanceCount}, ewmaScalerDesiredInstanceCount {ewmaScalerDesiredInstanceCount}, simpleScalerDesiredInstanceCount {simpleScalerDesiredInstanceCount}, prevDesiredInstanceCount {prevDesiredInstanceCount}, requestsPerSecondEWMA {requestsPerSecondEWMA}, requestDurationEWMA {requestDurationEWMA}",
                _desiredInstanceCount, newDesiredInstanceCount, ewmaScalerDesiredInstanceCount != null ? ewmaScalerDesiredInstanceCount.Value : null, simpleScalerDesiredInstanceCount, prevDesiredInstanceCount, Math.Round(requestsPerSecondEWMA, 1), Math.Round(requestDurationEWMA, 1));

              // Clear any deferred scale down
              deferredScaleInNewDesiredInstanceCount = null;

              // Try to set the new desired count
              // Because we own setting this value we can do a simple compare and swap
              _desiredInstanceCount = newDesiredInstanceCount;

              // Record the new desired count
              MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceDesiredCount, _desiredInstanceCount);
              _metricsLogger.PutMetric("LambdaDesiredCount", newDesiredInstanceCount, Unit.Count);

              var scaleOutCount = Math.Max(newDesiredInstanceCount - (_runningInstanceCount + _startingInstanceCount - _stoppingInstanceCount), 0);
              scaleOutCount = Math.Min(scaleOutCount, maxScaleOut);
              scaleOutCount = Math.Min(scaleOutCount, (int)Math.Ceiling(_desiredInstanceCount * maxScaleOutPercent));
              if (scaleOutCount > 0)
              {
                _metricsLogger.PutMetric("LambdaScaleOutCount", scaleOutCount, Unit.Count);
              }
              while (scaleOutCount-- > 0)
              {
                // Start a new instance
                StartNewInstance();
              }
            }
            else if (!deferredScaleInNewDesiredInstanceCount.HasValue
                    && newDesiredInstanceCount < _desiredInstanceCount)
            {
              _logger.LogDebug("ManageCapacity - Scheduling scale in: _desiredInstanceCount {_desiredInstanceCount} -> {newDesiredInstanceCount}, ewmaScalerDesiredInstanceCount {ewmaScalerDesiredInstanceCount}, simpleScalerDesiredInstanceCount {simpleScalerDesiredInstanceCount}, prevDesiredInstanceCount {prevDesiredInstanceCount}, requestsPerSecondEWMA {requestsPerSecondEWMA}, requestDurationEWMA {requestDurationEWMA}, _runningInstanceCount {}, _startingInstanceCount {}, _stoppingInstanceCount {}",
                _desiredInstanceCount, newDesiredInstanceCount, ewmaScalerDesiredInstanceCount != null ? ewmaScalerDesiredInstanceCount.Value : null, simpleScalerDesiredInstanceCount, prevDesiredInstanceCount, Math.Round(requestsPerSecondEWMA, 1), Math.Round(requestDurationEWMA, 1), _runningInstanceCount, _startingInstanceCount, _stoppingInstanceCount);

              // Schedule a scale down
              deferredScaleInNewDesiredInstanceCount = newDesiredInstanceCount;
              cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(125));
            }
            else if (deferredScaleInNewDesiredInstanceCount.GetValueOrDefault() > newDesiredInstanceCount)
            {
              _logger.LogInformation("ManageCapacity - Adjusting scheduled scale in: _desiredInstanceCount {_desiredInstanceCount} -> {newDesiredInstanceCount}, ewmaScalerDesiredInstanceCount {ewmaScalerDesiredInstanceCount}, simpleScalerDesiredInstanceCount {simpleScalerDesiredInstanceCount}, prevDesiredInstanceCount {prevDesiredInstanceCount}, requestsPerSecondEWMA {requestsPerSecondEWMA}, requestDurationEWMA {requestDurationEWMA}",
                _desiredInstanceCount, newDesiredInstanceCount, ewmaScalerDesiredInstanceCount != null ? ewmaScalerDesiredInstanceCount.Value : null, simpleScalerDesiredInstanceCount, prevDesiredInstanceCount, Math.Round(requestsPerSecondEWMA, 1), Math.Round(requestDurationEWMA, 1));

              // Leave the schedule but adjust the amount
              deferredScaleInNewDesiredInstanceCount = newDesiredInstanceCount;
            }
            else
            {
              _logger.LogInformation("ManageCapacity - Cancelling scheduled scale in: _desiredInstanceCount {_desiredInstanceCount} -> {newDesiredInstanceCount}, ewmaScalerDesiredInstanceCount {ewmaScalerDesiredInstanceCount}, simpleScalerDesiredInstanceCount {simpleScalerDesiredInstanceCount}, prevDesiredInstanceCount {prevDesiredInstanceCount}, requestsPerSecondEWMA {requestsPerSecondEWMA}, requestDurationEWMA {requestDurationEWMA}",
                                   _desiredInstanceCount, newDesiredInstanceCount, ewmaScalerDesiredInstanceCount != null ? ewmaScalerDesiredInstanceCount.Value : null, simpleScalerDesiredInstanceCount, prevDesiredInstanceCount, Math.Round(requestsPerSecondEWMA, 1), Math.Round(requestDurationEWMA, 1));

              // Cancel the scale down
              deferredScaleInNewDesiredInstanceCount = null;
              cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            }
          }
        }
      }
      catch (OperationCanceledException)
      {
        // This was a timeout - Reset the timeout
        cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _logger.LogDebug("ManageCapacity - Scale down check running");

        //
        // Locking instance counts - Do not do anything Async / IO in this block
        //
        lock (_instanceCountLock)
        {
          // See if we have a deferred scale down to apply
          if (deferredScaleInNewDesiredInstanceCount.HasValue)
          {
            if (deferredScaleInNewDesiredInstanceCount.Value == _desiredInstanceCount)
            {
              // Nothing to do, just go around
              _logger.LogInformation("ManageCapacity - Skipping performing deferred scale down: _desiredInstanceCount {_desiredInstanceCount} -> {deferredScaleInNewDesiredInstanceCount.Value}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount}",
                _desiredInstanceCount, deferredScaleInNewDesiredInstanceCount.Value, _runningInstanceCount, _startingInstanceCount);
              deferredScaleInNewDesiredInstanceCount = null;
              continue;
            }
            _logger.LogInformation("ManageCapacity - Performing deferred scale down: _desiredInstanceCount {_desiredInstanceCount} -> {deferredScaleInNewDesiredInstanceCount.Value}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount}",
              _desiredInstanceCount, deferredScaleInNewDesiredInstanceCount.Value, _runningInstanceCount, _startingInstanceCount);
            _desiredInstanceCount = deferredScaleInNewDesiredInstanceCount.GetValueOrDefault();
            _metricsLogger.PutMetric("LambdaDesiredCount", 0, Unit.Count);
            MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceDesiredCount, _desiredInstanceCount);

            // Clear the deferred value
            deferredScaleInNewDesiredInstanceCount = null;
          }

          // Time to check if we can reduce capacity

          // Stop only running instances if we have too many
          // We do not count starting instances because they are not yet running
          // Example:
          // Running: 100
          // Desired: 50
          // Stopping: 20
          // We need to stop 100 - 50 = 50 instances, but 20 are already stopping so 50 - 20 = 30 more need to be stopped
          // Note: it doesn't matter if the instances in the dictionary are stopped or not.
          // Example a few moments later when all 20 have stopped but we haven't updated our calcuation:
          // Running: 80
          // Desired: 50
          // Stopping: 0
          // We need to stop 80 - 50 = 30 instances, but 0 are already stopping so 30 - 0 = 30 more need to be stopped
          // At worst, we won't stop enough until the next loop.
          var scaleInCount = Math.Max(_runningInstanceCount - _stoppingInstanceCount - _desiredInstanceCount, 0);
          scaleInCount = Math.Min(scaleInCount, maxScaleOut);
          scaleInCount = Math.Min(scaleInCount, (int)Math.Ceiling(_desiredInstanceCount * maxScaleInPercent));
          _metricsLogger.PutMetric("LambdaScaleInCount", scaleInCount, Unit.Count);
          while (scaleInCount-- > 0)
          {
            // Get the least outstanding instance
            if (_leastOutstandingQueue.TryRemoveLeastOutstandingInstance(out var leastBusyInstance))
            {
              // Remove it from the collection
              _instances.TryRemove(leastBusyInstance.Id, out var _);

              // Add to the stopping list
              stoppingInstances.Add(leastBusyInstance.Id, leastBusyInstance);

              // Close the instance
              _logger.LogInformation("ManageCapacity - Closing least busy instance, LambdaId: {lambdaId}", leastBusyInstance.Id);
              // We are not awaiting the close
              _ = CloseInstance(leastBusyInstance);
            }
            else
            {
              // We have no instances to close
              // This can happen if all instances are busy and we're starting a lot of new instances to replace them
              _logger.LogError("ManageCapacity - No instances to close - _desiredInstanceCount {_desiredInstanceCount}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount}, _stoppingInstanceCount {_stoppingInstanceCount}",
                _desiredInstanceCount, _runningInstanceCount, _startingInstanceCount, _stoppingInstanceCount);
              break;
            }
          }
        }
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "ManageCapacity - Exception");
      }
    }
  }

  public void UpdateDesiredCapacity(int pendingRequests, int runningRequests, double requestsPerSecondEWMA, double requestDurationEWMA)
  {
    // Send the message to the channel
    // This will return immediately because we drop any prior message and only keep the latest
    _capacityChannel.Writer.TryWrite(new LambdaInstanceCapacityMessage()
    {
      PendingRequests = pendingRequests,
      RunningRequests = runningRequests,
      RequestsPerSecondEWMA = requestsPerSecondEWMA,
      RequestDurationEWMA = requestDurationEWMA,
    });
  }

#if TEST_RUNNERS
  public void DebugAddInstance(string instanceId)
  {
    // Start a new LambdaInstance and add it to the list
    var instance = new LambdaInstance(_maxConcurrentCount, _functionName, _functionNameQualifier, channelCount: _channelCount, dispatcher: _dispatcher);

    instance.FakeStart(instanceId);

    // Add the instance to the collection
    if (!_instances.TryAdd(instance.Id, instance))
    {
      _logger.LogDebug("LambdaInstance {instanceId} fake already open", instance.Id);
      return;
    }

    _logger.LogInformation("LambdaInstance {instanceId} fake opened", instance.Id);

    // We need to keep track of how many Lambdas are running
    // We will replace this one if it's still desired
    lock (_instanceCountLock)
    {
      _runningInstanceCount++;
      MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceRunningCount, _runningInstanceCount);
      _metricsLogger.PutMetric("LambdaInstanceRunningCount", _runningInstanceCount, Unit.Count);
    }

    // Add the instance to the least outstanding queue
    _leastOutstandingQueue.AddInstance(instance);
  }
#endif

  /// <summary>
  /// Start a new LambdaInstance and increment the desired count
  /// </summary>
  /// <returns></returns>
  private void StartNewInstance()
  {
    lock (_instanceCountLock)
    {
      // We always increment the starting count
      _startingInstanceCount++;
      MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceStartingCount, _startingInstanceCount);
      _metricsLogger.PutMetric("LambdaInstanceStartingCount", _startingInstanceCount, Unit.Count);
    }

    // Start a new LambdaInstance and add it to the list
    var instance = new LambdaInstance(_maxConcurrentCount, _functionName, _functionNameQualifier, channelCount: _channelCount, dispatcher: _dispatcher);

    // Add the instance to the collection
    if (!_instances.TryAdd(instance.Id, instance))
    {
      // Note: this can't really happen since the instance assigned a GUID and we only add it here
      throw new Exception("Failed to add instance to collection - key already exists");
    }

    instance.OnOpen += (instance) =>
    {
      // We always decrement the starting count
      lock (_instanceCountLock)
      {
        _startingInstanceCount--;
        MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceStartingCount, _startingInstanceCount);
        _metricsLogger.PutMetric("LambdaInstanceStartingCount", _startingInstanceCount, Unit.Count);

        _logger.LogInformation("LambdaInstance {instanceId} opened", instance.Id);

        // We need to keep track of how many Lambdas are running
        // We will replace this one if it's still desired
        _runningInstanceCount++;
        MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceRunningCount, _runningInstanceCount);
        _metricsLogger.PutMetric("LambdaInstanceRunningCount", _runningInstanceCount, Unit.Count);
      }

      // Add the instance to the least outstanding queue
      _leastOutstandingQueue.AddInstance(instance);
    };

    //
    // Called from the LambdaInstance.Close method when close is
    // initiated rather than just observing an Invoke has returned
    //
    instance.OnCloseInitiated += (instance) =>
    {
      // We always increment the stopping count when initiating a close
      lock (_instanceCountLock)
      {
        // Stopping instance count allows early replace
        _stoppingInstanceCount++;
        MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceStoppingCount, _stoppingInstanceCount);
        _metricsLogger.PutMetric("LambdaInstanceStoppingCount", _stoppingInstanceCount, Unit.Count);

        if (instance.LamdbdaInitiatedClose)
        {
          _metricsLogger.PutMetric("LambdaInitiatedClose", 1, Unit.Count);
        }

        // Replace this instance if it's still desired
        if (!instance.DoNotReplace)
        {
          if (_runningInstanceCount + _startingInstanceCount - _stoppingInstanceCount < _desiredInstanceCount)
          {
            // We need to start a new instance
            if (instance.LamdbdaInitiatedClose)
            {
              // Mark that we are replacing this instance
              // TODO: Only mark that we're replacing this if it was open for a while
              instance.Replacing = true;
              _metricsLogger.PutMetric("LambdaInitiatedCloseReplacing", 1, Unit.Count);
            }
            StartNewInstance();
          }
        }
      }
    };

    instance.OnInvocationComplete += (instance) =>
      {
        lock (_instanceCountLock)
        {
          var transitionResult = instance.TransitionToDraining();

          // If the instance just returned but was not marked as closing,
          // then we should not decrement the stopping count.
          // But if Closing was initiated then we should decrement the count.
          if (!transitionResult.TransitionedToDraining)
          {
            _stoppingInstanceCount--;
            MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceStoppingCount, _stoppingInstanceCount);
            _metricsLogger.PutMetric("LambdaInstanceStoppingCount", _stoppingInstanceCount, Unit.Count);

            _logger.LogInformation("LambdaInstance {instanceId} was closed already, _stoppingInstanceCount {_stoppingInstanceCount} (after decrement)", instance.Id, _stoppingInstanceCount);
          }

          if (transitionResult.OpenWasRejected)
          {
            _logger.LogInformation("LambdaInstance {instanceId} open was rejected, not replacing, not decrementing counts", instance.Id);
            return;
          }

          if (transitionResult.WasOpened)
          {
            // The instance opened, so decrement the running count not the starting count
            _runningInstanceCount--;
            MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceRunningCount, _runningInstanceCount);
            _metricsLogger.PutMetric("LambdaInstanceRunningCount", _runningInstanceCount, Unit.Count);
          }
          else
          {
            _logger.LogInformation("LambdaInstance {instanceId} was never opened", instance.Id);
            // The instance never opened (e.g. throttled and failed) so decrement the starting count
            _startingInstanceCount--;
            MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceStartingCount, _startingInstanceCount);
            _metricsLogger.PutMetric("LambdaInstanceStartingCount", _startingInstanceCount, Unit.Count);
          }

          _logger.LogInformation("LambdaInstance {instanceId} invocation complete, _desiredInstanceCount {_desiredInstanceCount}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount} (after decrement), _stoppingInstanceCount {_stoppingInstanceCount}",
            instance.Id, _desiredInstanceCount, _runningInstanceCount, _startingInstanceCount, _stoppingInstanceCount);

          // Remove this instance from the collection
          _instances.TryRemove(instance.Id, out var instanceFromList);

          // We don't want to wait for this, let it happen in the background
          if (transitionResult.TransitionedToDraining)
          {
            // We were the first to notice the close - the close was not initiated by the router
            // but by a faulted invoke
            instance.InvokeCompleted();
          }

          if (instance.DoNotReplace)
          {
            _logger.LogInformation("LambdaInstance {instanceId} marked as DoNotReplace, not replacing", instance.Id);
            return;
          }

          // We need to keep track of how many Lambdas are running
          // We will replace this one if it's still desired
          if (_runningInstanceCount + _startingInstanceCount - _stoppingInstanceCount < _desiredInstanceCount)
          {
            // We need to start a new instance
            _metricsLogger.PutMetric("LambdaInvokeCompleteReplacing", 1, Unit.Count);
            StartNewInstance();
          }
        }
      };

    // Start the instance
    instance.Start();
  }

  /// <summary>
  /// Gracefully close an instance
  /// </summary>
  /// <param name="instance"></param>
  public async Task CloseInstance(ILambdaInstance instance, bool lambdaInitiated = false)
  {
    // The instance is going to get cleaned up by the OnInvocationComplete handler
    // Counts will be decremented, the instance will be replaced, etc.
    // We just need to get the Lambda to return from the invoke
    // If the Lambda initiated the close we can still replace it
    // (this can be for deadline exceeded or for lambda detecting idle)
    await instance.Close(doNotReplace: !lambdaInitiated, lambdaInitiated: lambdaInitiated);
  }
}
