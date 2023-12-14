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

    _logger.LogWarning("AddConnectionForLambda - Connection added to Lambda Instance {lambdaId} that does not exist - closing with 409", lambdaId);

    // Close the connection
    try
    {
      response.StatusCode = 409;
      await response.StartAsync();
      await response.Body.DisposeAsync();
      await response.CompleteAsync();
      await request.Body.CopyToAsync(Stream.Null);
      await request.Body.DisposeAsync();
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "AddConnectionForLambda - Exception closing down connection to LambdaId: {LambdaId}", lambdaId);
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
        await instanceFromList.Close();
        return;
      }

      // We need to keep track of how many Lambdas are running
      // We will replace this one if it's still desired

      if (_runningInstanceCount + _startingInstanceCount < _desiredInstanceCount)
      {
        // We need to start a new instance
        await this.StartNewInstance();
      }
    };

    // This is only async because of the ENI IP lookup
    await instance.Start();
  }

  /// <summary>
  /// Allow an instance to gracefully deregister itself
  /// </summary>
  /// <param name="instanceId"></param>
  /// <returns></returns>
  public void CloseInstance(string instanceId)
  {
    _logger.LogInformation("Closing instance {instanceId}", instanceId);

    if (_instances.TryGetValue(instanceId, out var instance))
    {
      // The instance is going to get cleaned up by the OnInvocationComplete handler
      // Counts will be decremented, the instance will be replaced, etc.
      // We just need to get the Lambda to return from the invoke
      instance.ReleaseConnections();
    }
    else
    {
      _logger.LogInformation("Instance {instanceId} not found during close", instanceId);
    }
  }
}
