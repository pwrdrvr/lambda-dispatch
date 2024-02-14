namespace PwrDrvr.LambdaDispatch.Router;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;

public struct LambdaInstanceCapacityMessage
{
  public int PendingRequests { get; set; }
  public int RunningRequests { get; set; }
}

public interface ILambdaInstanceManager
{
  void AddBackgroundDispatcherReference(IBackgroundDispatcher dispatcher);

  bool TryGetConnection([NotNullWhen(true)] out LambdaConnection? connection, bool tentative = false);

  bool ValidateLambdaId(string lambdaId, [NotNullWhen(true)] out ILambdaInstance? instance);

  Task ReenqueueUnusedConnection(LambdaConnection connection, string lambdaId);

  Task<LambdaConnection?> AddConnectionForLambda(HttpRequest request, HttpResponse response, string lambdaId, string channelId, bool immediateDispatch = false);

#if TEST_RUNNERS
  void DebugAddInstance(string instanceId);
#endif

  Task UpdateDesiredCapacity(int pendingRequests, int runningRequests);

  void CloseInstance(ILambdaInstance instance, bool lambdaInitiated = false);

  void CloseInstance(string instanceId, bool lambdaInitiated = false);
}

public class LambdaInstanceManager : ILambdaInstanceManager
{
  private readonly ILogger<LambdaInstanceManager> _logger = LoggerInstance.CreateLogger<LambdaInstanceManager>();

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
  /// </summary>
  private int _stoppingInstanceCount = 0;

  private readonly int _maxConcurrentCount;

  private readonly int _instanceCountMultiplier;

  private readonly int _channelCount;

  private readonly string _functionName;

  private readonly string? _functionNameQualifier;

  private readonly IMetricsLogger _metricsLogger;

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

    // Start the capacity manager
    Task.Run(ManageCapacity);
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

  public async Task ReenqueueUnusedConnection(LambdaConnection connection, string lambdaId)
  {
    // Get the instance for the lambda
    if (_instances.TryGetValue(lambdaId, out var instance))
    {
      // Add the connection to the instance
      // The instance will eventually get rebalanced in the least outstanding queue
      await instance.ReenqueueUnusedConnection(connection);

      // Put this instance back into rotation if it was busy
      _leastOutstandingQueue.ReinstateFullInstance(instance);
    }
    else
    {
      _logger.LogWarning("ReenqueueUnusedConnection - Connection added to Lambda Instance {lambdaId} that does not exist - closing with 409", lambdaId);
    }
  }

  public async Task<LambdaConnection?> AddConnectionForLambda(HttpRequest request, HttpResponse response, string lambdaId, string channelId, bool immediateDispatch = false)
  {
    // Get the instance for the lambda
    if (_instances.TryGetValue(lambdaId, out var instance))
    {
      // Add the connection to the instance
      // The instance will eventually get rebalanced in the least outstanding queue
      var connection = await instance.AddConnection(request, response, channelId, immediateDispatch).ConfigureAwait(false);

      // Check where this instance is in the least outstanding queue
      if (!immediateDispatch)
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

  private readonly Channel<LambdaInstanceCapacityMessage> _capacityChannel = Channel.CreateBounded<LambdaInstanceCapacityMessage>(new BoundedChannelOptions(100)
  {
    FullMode = BoundedChannelFullMode.DropOldest,
    SingleReader = true,
  });

  private int ComputeDesiredInstanceCount(int pendingRequests, int runningRequests)
  {
    // Calculate the desired count
    var cleanPendingRequests = Math.Max(pendingRequests, 0);
    var cleanRunningRequests = Math.Max(runningRequests, 0);

    // Calculate the desired count
    var totalDesiredRequestCapacity = cleanPendingRequests + cleanRunningRequests;
    var desiredInstanceCount = (int)Math.Ceiling((double)totalDesiredRequestCapacity / _maxConcurrentCount) * _instanceCountMultiplier;

    // Special case for 0 pending or running requests
    if (cleanPendingRequests == 0 && cleanRunningRequests == 0)
    {
      desiredInstanceCount = 0;
    }

    return desiredInstanceCount;
  }

  // TODO: This should project excess capacity and not use 100% of max capacity at all times
  // TODO: This should not start new instances for pending requests at a 1/1 ratio but rather something less than that
  private async Task ManageCapacity()
  {
    Dictionary<string, ILambdaInstance> stoppingInstances = [];
    var trailingAverage = new TrailingAverage();
    var scaleTokenBucket = new TokenBucket(5, TimeSpan.FromSeconds(1));
    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var noMessagesRead = true;

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

        // Mark that we read a message
        noMessagesRead = false;

        trailingAverage.Add(ComputeDesiredInstanceCount(pendingRequests, runningRequests));

        // Rip through any other messages, up to a limit of 100
        var messagesToRead = 100;
        while (_capacityChannel.Reader.TryRead(out var nextMessage) && --messagesToRead > 0)
        {
          pendingRequests = nextMessage.PendingRequests;
          runningRequests = nextMessage.RunningRequests;

          trailingAverage.Add(ComputeDesiredInstanceCount(pendingRequests, runningRequests));
        }

        var averageCount = trailingAverage.Average;
        var newDesiredInstanceCount = averageCount == 0 ? 0 : (int)Math.Ceiling(averageCount);

        //
        // Locking instance counts - Do not do anything Async / IO in this block
        //
        lock (_instanceCountLock)
        {
          // If we are already at the desired count and we have no starting instances, then we are done
          if (newDesiredInstanceCount == _desiredInstanceCount
              && _startingInstanceCount + _runningInstanceCount == _desiredInstanceCount)
          {
            // Nothing to do
            continue;
          }

          // See if we are allowed to adjust the desired count
          if (scaleTokenBucket.TryGetToken())
          {
            var prevDesiredInstanceCount = _desiredInstanceCount;

            // Try to set the new desired count
            // Because we own setting this value we can do a simple compare and swap
            _desiredInstanceCount = newDesiredInstanceCount;

            // Record the new desired count
            MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceDesiredCount, _desiredInstanceCount);
            _metricsLogger.PutMetric("LambdaDesiredCount", newDesiredInstanceCount, Unit.Count);
            _metricsLogger.PutMetric("PendingRequestCount", pendingRequests, Unit.Count);

            // Start instances if needed
            if (newDesiredInstanceCount > _runningInstanceCount + _startingInstanceCount)
            {
              var scaleOutCount = Math.Max(newDesiredInstanceCount - _runningInstanceCount - _startingInstanceCount, 0);
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
            else if (newDesiredInstanceCount < _runningInstanceCount)
            {
              // TODO: Reuse this from the catch below
              // Note: Here we are only counting running instances, not starting instances because
              // staring instances could take long time to become available
              // We do not want to stop good instances and then wait for cold starts to replace them
              var scaleInCount = Math.Max(_runningInstanceCount - _desiredInstanceCount, 0);
              if (scaleInCount > 0)
              {
                _metricsLogger.PutMetric("LambdaScaleInCount", scaleInCount, Unit.Count);
              }
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
                  CloseInstance(leastBusyInstance);
                }
                else
                {
                  // We have no instances to close
                  // This can happen if all instances are busy and we're starting a lot of new instances to replace them
                  _logger.LogError("ManageCapacity - No instances to close - _desiredInstanceCount {_desiredInstanceCount}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount}", _desiredInstanceCount, _runningInstanceCount, _startingInstanceCount);
                  break;
                }
              }
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
          // When no messages were read it means we need zero capacity
          if (noMessagesRead && _desiredInstanceCount > 0)
          {
            _logger.LogDebug("ManageCapacity - No messages read, setting desired count to 0");
            _desiredInstanceCount = 0;
            _metricsLogger.PutMetric("LambdaDesiredCount", 0, Unit.Count);
            MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceDesiredCount, _desiredInstanceCount);
          }
          noMessagesRead = true;

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
          var scaleInCount = Math.Max(_runningInstanceCount - _desiredInstanceCount, 0);
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
              CloseInstance(leastBusyInstance);
            }
            else
            {
              // We have no instances to close
              // This can happen if all instances are busy and we're starting a lot of new instances to replace them
              _logger.LogError("ManageCapacity - No instances to close - _desiredInstanceCount {_desiredInstanceCount}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount}", _desiredInstanceCount, _runningInstanceCount, _startingInstanceCount);
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

  public async Task UpdateDesiredCapacity(int pendingRequests, int runningRequests)
  {
    // In the nominal case of the right amount of capacity, we avoid writing to the channel
    // if (ComputeDesiredInstanceCount(pendingRequests, runningRequests) == _desiredInstanceCount)
    // {
    //   // Nothing to do
    //   return;
    // }

    // Send the message to the channel
    // This will return immediately because we drop any prior message and only keep the latest
    await _capacityChannel.Writer.WriteAsync(new LambdaInstanceCapacityMessage()
    {
      PendingRequests = pendingRequests,
      RunningRequests = runningRequests
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

        // Starting lambdas can't be seen by the scale down
        // If we have too many running instances we tell this one to stop
        // If we don't do this, it will hang out for 5 seconds if all traffic stops
        // This can cause a massive traffic spike
        if (_runningInstanceCount + _startingInstanceCount > _desiredInstanceCount)
        {
          _logger.LogInformation("LambdaInstance {instanceId} opened but we have too many running instances, closing", instance.Id);
          instance.Close(true);
          return;
        }

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
        _stoppingInstanceCount--;
        MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceStoppingCount, _stoppingInstanceCount);

        if (instance.LamdbdaInitiatedClose)
        {
          _metricsLogger.PutMetric("LambdaInitiatedClose", 1, Unit.Count);
        }

        // Replace this instance if it's still desired
        if (!instance.DoNotReplace)
        {
          if (_runningInstanceCount + _startingInstanceCount < _desiredInstanceCount)
          {
            // We need to start a new instance
            if (instance.LamdbdaInitiatedClose)
            {
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
          // If the instance just returned but was not marked as closing,
          // then we should not decrement the stopping count.
          // But if Closing was initiated then we should decrement the count.
          var transitionResult = instance.TransitionToClosing();

          if (transitionResult.TransitionedToClosing)
          {
            _stoppingInstanceCount--;
            MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceStoppingCount, _stoppingInstanceCount);
            _logger.LogInformation("LambdaInstance {instanceId} was closed already, _stoppingInstanceCount {_stoppingInstanceCount} (after decrement)", instance.Id, _stoppingInstanceCount);
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
            _logger.LogInformation("LambdaInstance {instanceId} was never opened, replacing", instance.Id);
            // The instance never opened (e.g. throttled and failed) so decrement the starting count
            _startingInstanceCount--;
            MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceStartingCount, _startingInstanceCount);
          }

          _logger.LogInformation("LambdaInstance {instanceId} invocation complete, _desiredInstanceCount {_desiredInstanceCount}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount} (after decrement), _stoppingInstanceCount {_stoppingInstanceCount}",
            instance.Id, _desiredInstanceCount, _runningInstanceCount, _startingInstanceCount, _stoppingInstanceCount);

          // Remove this instance from the collection
          _instances.TryRemove(instance.Id, out var instanceFromList);

          // The instance will already be marked as closing
          if (instanceFromList != null)
          {
            // We don't want to wait for this, let it happen in the background
            instanceFromList.Close();
          }

          if (instance.DoNotReplace)
          {
            _logger.LogInformation("LambdaInstance {instanceId} marked as DoNotReplace, not replacing", instance.Id);
            return;
          }

          // We need to keep track of how many Lambdas are running
          // We will replace this one if it's still desired
          if (_runningInstanceCount + _startingInstanceCount < _desiredInstanceCount)
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
  public void CloseInstance(ILambdaInstance instance, bool lambdaInitiated = false)
  {
    // The instance is going to get cleaned up by the OnInvocationComplete handler
    // Counts will be decremented, the instance will be replaced, etc.
    // We just need to get the Lambda to return from the invoke
    instance.Close(true, lambdaInitiated);
  }

  /// <summary>
  /// Gracefully close an instance
  /// </summary>
  /// <param name="instanceId"></param>
  /// <returns></returns>
  public void CloseInstance(string instanceId, bool lambdaInitiated)
  {
    if (_instances.TryGetValue(instanceId, out var instance))
    {
      CloseInstance(instance, lambdaInitiated);
    }
    else
    {
      _logger.LogInformation("Instance {instanceId} not found during close", instanceId);
    }
  }
}
