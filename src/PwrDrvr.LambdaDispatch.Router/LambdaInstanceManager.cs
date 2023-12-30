namespace PwrDrvr.LambdaDispatch.Router;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

public class LambdaInstanceManager
{
  private readonly ILogger<LambdaInstanceManager> _logger = LoggerInstance.CreateLogger<LambdaInstanceManager>();

  private readonly LeastOutstandingQueue _leastOutstandingQueue;

  private volatile int _runningInstanceCount = 0;

  private volatile int _desiredInstanceCount = 0;

  private volatile int _startingInstanceCount = 0;

  private readonly int _maxConcurrentCount;

  /// <summary>
  /// Used to lookup instances by ID
  /// This allows associating the connecitons with their owning lambda instance
  /// </summary>
  private readonly ConcurrentDictionary<string, ILambdaInstance> _instances = new();

  public LambdaInstanceManager(int maxConcurrentCount)
  {
    _maxConcurrentCount = maxConcurrentCount;
    _leastOutstandingQueue = new(maxConcurrentCount);

    // Start the capacity manager
    Task.Run(ManageCapacity);
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

  private struct LambdaInstanceCapacityMessage
  {
    public int PendingRequests { get; set; }
    public int RunningRequests { get; set; }
  }

  private Channel<LambdaInstanceCapacityMessage> _capacityChannel = Channel.CreateBounded<LambdaInstanceCapacityMessage>(new BoundedChannelOptions(1)
  {
    FullMode = BoundedChannelFullMode.DropOldest
  });

  private int ComputeDesiredInstanceCount(int pendingRequests, int runningRequests)
  {
    // Calculate the desired count
    var cleanPendingRequests = Math.Max(pendingRequests, 0);
    var cleanRunningRequests = Math.Max(runningRequests, 0);

    // Calculate the desired count
    var totalDesiredRequestCapacity = cleanPendingRequests + cleanRunningRequests;
    // TODO: Load the 2x factor from the configuration
    var desiredInstanceCount = (int)Math.Ceiling((double)totalDesiredRequestCapacity / _maxConcurrentCount) * 2;
    return desiredInstanceCount;
  }


  // TODO: This should project excess capacity and not use 100% of max capacity at all times
  // TODO: This should not start new instances for pending requests at a 1/1 ratio but rather something less than that
  private async Task ManageCapacity()
  {
    Dictionary<string, ILambdaInstance> stoppingInstances = [];

    while (true)
    {
      // Setup a timer to run every 5 seconds or when we are asked to increase capacity
      var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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

        // Try to set the new desired count
        // Because we own setting this value we can do a simple compare and swap
        Interlocked.Exchange(ref _desiredInstanceCount, desiredInstanceCount);

        // Start instances if needed
        while (_runningInstanceCount + _startingInstanceCount < desiredInstanceCount)
        {
          _logger.LogDebug("UpdateDesiredCapacity - STARTING - pendingRequests {pendingRequests}, runningRequests {runningRequests}, _desiredInstanceCount {_desiredInstanceCount}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount}", pendingRequests, runningRequests, _desiredInstanceCount, _runningInstanceCount, _startingInstanceCount);
          // Start a new instance
          await StartNewInstance();
        }

        MetricsRegistry.Metrics.Measure.Gauge.SetValue(MetricsRegistry.LambdaInstanceDesiredCount, _desiredInstanceCount);

        _logger.LogDebug("UpdateDesiredCapacity - AFTER - pendingRequests {pendingRequests}, runningRequests {runningRequests}, _desiredInstanceCount {_desiredInstanceCount}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount}", pendingRequests, runningRequests, _desiredInstanceCount, _runningInstanceCount, _startingInstanceCount);
      }
      catch (OperationCanceledException)
      {
        // This was a timeout
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
        var excessCapacity = Math.Max(_runningInstanceCount - _desiredInstanceCount - stoppingInstances.Count, 0);
        while (excessCapacity-- > 0)
        {
          // Get the least outstanding instance
          if (_leastOutstandingQueue.TryRemoveLeastOutstandingInstance(out var leastBusyInstance))
          {
            // Remove it from the collection
            _instances.TryRemove(leastBusyInstance.Id, out var _);

            // Add to the stopping list
            stoppingInstances.Add(leastBusyInstance.Id, leastBusyInstance);

            // Close the instance
            CloseInstance(leastBusyInstance);
          }
          else
          {
            // We have no instances to close
            // This can happen if all instances are busy and we're starting a lot of new instances to replace them
            _logger.LogError("UpdateDesiredCapacity - No instances to close - _desiredInstanceCount {_desiredInstanceCount}, _runningInstanceCount {_runningInstanceCount}, _startingInstanceCount {_startingInstanceCount}", _desiredInstanceCount, _runningInstanceCount, _startingInstanceCount);
            break;
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
    if (ComputeDesiredInstanceCount(pendingRequests, runningRequests) == _desiredInstanceCount)
    {
      // Nothing to do
      return;
    }

    // Send the message to the channel
    // This will return immediately because we drop any prior message and only keep the latest
    await _capacityChannel.Writer.WriteAsync(new LambdaInstanceCapacityMessage()
    {
      PendingRequests = pendingRequests,
      RunningRequests = runningRequests
    });
  }

  /// <summary>
  /// Start a new LambdaInstance and increment the desired count
  /// </summary>
  /// <returns></returns>
  private async Task StartNewInstance()
  {
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
        await StartNewInstance().ConfigureAwait(false);
      }
    };

    // This is only async because of the ENI IP lookup
    await instance.Start().ConfigureAwait(false);
  }

  /// <summary>
  /// Gracefully close an instance
  /// </summary>
  /// <param name="instance"></param>
  public void CloseInstance(ILambdaInstance instance)
  {
    _logger.LogInformation("Closing instance {instanceId}", instance.Id);

    // The instance is going to get cleaned up by the OnInvocationComplete handler
    // Counts will be decremented, the instance will be replaced, etc.
    // We just need to get the Lambda to return from the invoke
    instance.Close();
  }

  /// <summary>
  /// Gracefully close an instance
  /// </summary>
  /// <param name="instanceId"></param>
  /// <returns></returns>
  public void CloseInstance(string instanceId)
  {
    _logger.LogInformation("Closing instance {instanceId}", instanceId);

    if (_instances.TryGetValue(instanceId, out var instance))
    {
      this.CloseInstance(instance);
    }
    else
    {
      _logger.LogInformation("Instance {instanceId} not found during close", instanceId);
    }
  }
}
