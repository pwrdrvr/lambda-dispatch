namespace PwrDrvr.LambdaDispatch.Router;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

public class LambdaInstanceManager
{
  private readonly ILogger<LambdaInstanceManager> _logger = LoggerInstance.CreateLogger<LambdaInstanceManager>();

  private readonly LeastOutstandingQueue _leastOutstandingQueue;

  private volatile int _runningInstanceCount = 0;

  private volatile int _desiredInstanceCount = 0;

  private volatile int _startingInstanceCount = 0;

  /// <summary>
  /// Count of instances that have been marked for deletion but
  /// that have not terminated yet
  ///
  /// Similar to _startingInstanceCount but in the other direction
  /// </summary>
  private volatile int _tombstonedInstanceCount = 0;

  private readonly int _maxConcurrentCount;

  /// <summary>
  /// Used to lookup instances by ID
  /// This allows associating the connecitons with their owning lambda instance
  /// </summary>
  private readonly ConcurrentDictionary<string, LambdaInstance> _instances = new();

  public LambdaInstanceManager(int maxConcurrentCount)
  {
    _maxConcurrentCount = maxConcurrentCount;
    _leastOutstandingQueue = new(maxConcurrentCount);
  }

  public bool TryGetConnection([NotNullWhen(true)] out LambdaConnection? connection)
  {
    // Return an available instance or start a new one if none are available
    var gotConnection = _leastOutstandingQueue.TryGetLeastOustandingConnection(out var dequeuedConnection);
    connection = dequeuedConnection;

    return gotConnection;
  }

  public bool ValidateLambdaId(string lambdaId)
  {
    return _instances.ContainsKey(lambdaId);
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
      var connection = await instance.AddConnection(request, response, channelId, immediateDispatch);

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
      try { await request.Body.CopyToAsync(Stream.Null); } catch { }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "AddConnectionForLambda - Exception closing down connection to LambdaId: {LambdaId}, ChannelId: {ChannelId}", lambdaId, channelId);
    }

    return null;
  }

  // TODO: This should project excess capacity and not use 100% of max capacity at all times
  // TODO: This should not start new instances for pending requests at a 1/1 ratio but rather something less than that
  public async Task UpdateDesiredCapacity(int pendingRequests, int runningRequests)
  {
    _logger.LogDebug("UpdateDesiredCapacity - BEFORE - pendingRequests {pendingRequests}, runningRequests {runningRequests}, _desiredInstanceCount {_desiredInstanceCount}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount}", pendingRequests, runningRequests, _desiredInstanceCount, _runningInstanceCount, _startingInstanceCount);

    var cleanPendingRequests = Math.Max(pendingRequests, 0);
    var cleanRunningRequests = Math.Max(runningRequests, 0);

    // Calculate the desired count
    var totalDesiredRequestCapacity = cleanPendingRequests + cleanRunningRequests;
    // TODO: Load the 2x factor from the configuration
    var desiredInstanceCount = (int)Math.Ceiling((double)totalDesiredRequestCapacity / _maxConcurrentCount) * 2;

    // Special case for 0 pending or running
    if (cleanPendingRequests == 0 && cleanRunningRequests == 0)
    {
      desiredInstanceCount = 0;
    }

    _logger.LogDebug("UpdateDesiredCapacity - COMPUTED - pendingRequests {pendingRequests}, runningRequests {runningRequests}, desiredCount {desiredCount}, _desiredInstanceCount {_desiredInstanceCount}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount}", pendingRequests, runningRequests, desiredInstanceCount, _desiredInstanceCount, _runningInstanceCount, _startingInstanceCount);

    // Start instances if needed
    while (_runningInstanceCount + _startingInstanceCount < desiredInstanceCount)
    {
      _logger.LogDebug("UpdateDesiredCapacity - STARTING - pendingRequests {pendingRequests}, runningRequests {runningRequests}, _desiredInstanceCount {_desiredInstanceCount}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount}", pendingRequests, runningRequests, _desiredInstanceCount, _runningInstanceCount, _startingInstanceCount);
      // Start a new instance
      await this.StartNewInstance(false);
    }

    // Try to set the new desired count
    while (_desiredInstanceCount < desiredInstanceCount)
    {
      // Increment the desired count
      Interlocked.Increment(ref _desiredInstanceCount);
    }
    while (_desiredInstanceCount > desiredInstanceCount)
    {
      // Decrement the desired count
      Interlocked.Decrement(ref _desiredInstanceCount);
    }

    // Stop only running instances if we have too many
    // We do not count starting instances because they are not yet running
    // TODO: This is dangerous - the runningInstanceCount is not decremented
    // until the Lamda invoke returns, which could be seconds later.
    // This means we could close all of the instances when this is hit from multiple threads.
    while (_runningInstanceCount - _tombstonedInstanceCount > _desiredInstanceCount)
    {
      // Get the least outstanding instance
      if (_leastOutstandingQueue.TryRemoveLeastOutstandingInstance(out var leastBusyInstance))
      {
        // Increment the tombstoned count
        // This ensures that we and other threads do not try to close this instance again
        Interlocked.Increment(ref _tombstonedInstanceCount);
        MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceTombstonedCount, _tombstonedInstanceCount);

        // Remove it from the collection
        _instances.TryRemove(leastBusyInstance.Id, out var _);

        // Note: the background rebalancer will remove this from the least outstanding queue eventually

        // Close the instance
        if (!CloseInstance(leastBusyInstance))
        {
          // If we failed to start closing the instance, decrement the tombstoned count
          Interlocked.Decrement(ref _tombstonedInstanceCount);
          MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceTombstonedCount, _tombstonedInstanceCount);
        }
      }
      else
      {
        // We have no instances to close
        // This can happen if all instances are busy and we're starting a lot of new instances to replace them
        _logger.LogError("UpdateDesiredCapacity - No instances to close - pendingRequests {pendingRequests}, runningRequests {runningRequests}, _desiredInstanceCount {_desiredInstanceCount}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount}", pendingRequests, runningRequests, _desiredInstanceCount, _runningInstanceCount, _startingInstanceCount);
        break;
      }
    }

    MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceDesiredCount, _desiredInstanceCount);

    _logger.LogDebug("UpdateDesiredCapacity - AFTER - pendingRequests {pendingRequests}, runningRequests {runningRequests}, _desiredInstanceCount {_desiredInstanceCount}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount}", pendingRequests, runningRequests, _desiredInstanceCount, _runningInstanceCount, _startingInstanceCount);
  }

  /// <summary>
  /// Start a new LambdaInstance and increment the desired count
  /// </summary>
  /// <returns></returns>
  private async Task StartNewInstance(bool incrementDesiredCount = false)
  {
    // If we get called we're starting a new instance
    if (incrementDesiredCount)
    {
      Interlocked.Increment(ref _desiredInstanceCount);
      MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceDesiredCount, _desiredInstanceCount);
    }

    Interlocked.Increment(ref _startingInstanceCount);
    MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceStartingCount, _startingInstanceCount);

    // Start a new LambdaInstance and add it to the list
    var instance = new LambdaInstance(_maxConcurrentCount);

    // Add the instance to the collection
    if (!_instances.TryAdd(instance.Id, instance))
    {
      throw new Exception("Failed to add instance to collection - key already exists");
    }

    instance.OnOpen += (instance) =>
    {
      // We always decrement the starting count
      Interlocked.Decrement(ref _startingInstanceCount);
      MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceStartingCount, _startingInstanceCount);

      _logger.LogInformation("LambdaInstance {instanceId} opened", instance.Id);

      // We need to keep track of how many Lambdas are running
      // We will replace this one if it's still desired
      Interlocked.Increment(ref _runningInstanceCount);
      MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceRunningCount, _runningInstanceCount);

      // Add the instance to the least outstanding queue
      _leastOutstandingQueue.AddInstance(instance);
    };

    instance.OnInvocationComplete += async (instance) =>
    {
      // Only decrement the count if this instance was ever opened
      if (instance.WasOpened)
      {
        Interlocked.Decrement(ref _runningInstanceCount);
        MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceRunningCount, _runningInstanceCount);
      }
      else
      {
        _logger.LogInformation("LambdaInstance {instanceId} was never opened, replacing", instance.Id);
        Interlocked.Decrement(ref _startingInstanceCount);
        MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceStartingCount, _startingInstanceCount);
      }

      _logger.LogInformation("LambdaInstance {instanceId} invocation complete, _desiredInstanceCount {_desiredInstanceCount}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount} (after decrement)", instance.Id, _desiredInstanceCount, _runningInstanceCount, _startingInstanceCount);

      // Remove this instance from the collection
      _instances.TryRemove(instance.Id, out var instanceFromList);

      // The instance will already be marked as closing
      if (instanceFromList != null)
      {
        // We don't want to wait for this, let it happen in the background
        instanceFromList.Close();
      }

      if (instance.Tombstoned)
      {
        Interlocked.Decrement(ref _tombstonedInstanceCount);
        MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceTombstonedCount, _tombstonedInstanceCount);
        _logger.LogInformation("LambdaInstance {instanceId} marked as Tombstoned, not replacing", instance.Id);
        return;
      }

      // We need to keep track of how many Lambdas are running
      // We will replace this one if it's still desired

      if (_runningInstanceCount + _startingInstanceCount < _desiredInstanceCount)
      {
        // We need to start a new instance
        await this.StartNewInstance().ConfigureAwait(false);
      }
    };

    // This is only async because of the ENI IP lookup
    await instance.Start().ConfigureAwait(false);
  }

  /// <summary>
  /// Gracefully close an instance
  /// </summary>
  /// <param name="instance"></param>
  public bool CloseInstance(LambdaInstance instance, bool tombstone = false)
  {
    _logger.LogInformation("Closing instance {instanceId}", instance.Id);

    // The instance is going to get cleaned up by the OnInvocationComplete handler
    // Counts will be decremented, the instance will be replaced, etc.
    // We just need to get the Lambda to return from the invoke
    return instance.Close(tombstone);
  }

  /// <summary>
  /// Gracefully close an instance
  /// </summary>
  /// <param name="instanceId"></param>
  /// <returns></returns>
  public bool CloseInstance(string instanceId, bool tombstone = false)
  {
    _logger.LogInformation("Closing instance {instanceId}", instanceId);

    if (_instances.TryGetValue(instanceId, out var instance))
    {
      return CloseInstance(instance, tombstone);
    }
    else
    {
      _logger.LogInformation("Instance {instanceId} not found during close", instanceId);
      return false;
    }
  }
}
