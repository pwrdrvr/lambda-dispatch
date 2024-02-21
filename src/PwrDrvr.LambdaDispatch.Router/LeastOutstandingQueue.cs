namespace PwrDrvr.LambdaDispatch.Router;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;

public interface ILeastOutstandingQueue
{
  int MaxConcurrentCount { get; }

  bool TryGetLeastOustandingConnection([NotNullWhen(true)] out LambdaConnection? connection, bool tentative = false);

  bool TryRemoveLeastOutstandingInstance([NotNullWhen(true)] out ILambdaInstance? instance);

  void AddInstance(ILambdaInstance instance);

  bool ReinstateFullInstance(ILambdaInstance instance);
}

/// <summary>
/// Gives approximate least outstanding requests for items that may
/// have changes in outstanding requests asynchronously in the background
/// </summary>
public class LeastOutstandingQueue : IDisposable, ILeastOutstandingQueue
{
  private readonly ILogger<LeastOutstandingQueue> _logger = LoggerInstance.CreateLogger<LeastOutstandingQueue>();

  private readonly int maxConcurrentCount;

  private readonly ConcurrentQueue<ILambdaInstance>[] availableInstances;

  private readonly ConcurrentDictionary<string, ILambdaInstance> fullInstances = new();

  // Token to cancel the background tasks
  private readonly CancellationTokenSource cancellationTokenSource = new();

  public int MaxConcurrentCount { get => maxConcurrentCount; }

  public void Dispose()
  {
    cancellationTokenSource.Cancel();
    GC.SuppressFinalize(this);
  }

  public LeastOutstandingQueue(IConfig config)
  {
    if (config == null)
    {
      throw new ArgumentNullException(nameof(config));
    }

    maxConcurrentCount = config.MaxConcurrentCount;

    if (maxConcurrentCount <= 0)
    {
      throw new ArgumentOutOfRangeException(nameof(maxConcurrentCount), "Max concurrent count must be greater than 0");
    }

    if (maxConcurrentCount > 100)
    {
      throw new ArgumentOutOfRangeException(nameof(maxConcurrentCount), "Max concurrent count must be less than 100");
    }

    availableInstances = InitQueues(maxConcurrentCount);

    Task.Run(RebalanceQueue);
    Task.Run(LogQueueSizes);
  }

  /// <summary>
  /// Get the index of the queue for the given outstanding request count
  /// </summary>
  /// <param name="outstandingRequestcount"></param>
  /// <returns></returns>
  private int GetFloorQueueIndex(int outstandingRequestcount)
  {
    if (outstandingRequestcount < 0)
    {
      return 0;
    }

    // If max is 10, and there are 10 outstanding requests,
    // we can't just return 10 as that will exceed the array bounds
    // Technically this should be left in the full dictionary
    if (outstandingRequestcount >= maxConcurrentCount)
    {
      return maxConcurrentCount - 1;
    }

    // TODO: If we use primes, just find the highest prime that is less than or equal to count
    return outstandingRequestcount;
  }

  /// <summary>
  /// Create the array of queues
  /// </summary>
  /// <param name="maxConcurrentCount"></param>
  /// <returns></returns>
  /// <exception cref="ArgumentOutOfRangeException"></exception>
  static private ConcurrentQueue<ILambdaInstance>[] InitQueues(int maxConcurrentCount)
  {
    // If an instance has maxConcurrentCount outstanding it goes in the full list
    var queueList = new ConcurrentQueue<ILambdaInstance>[maxConcurrentCount];

    // Initialize the queues
    for (var i = 0; i < queueList.Length; i++)
    {
      queueList[i] = new ConcurrentQueue<ILambdaInstance>();
    }

    return queueList;
  }

  /// <summary>
  /// Remove the least busy instance from the queue
  /// 
  /// Note: this will return a full instance if no other instances are available but
  /// that instance will still be in the full dictionary (or could be reinstated too)
  /// and must be removed.
  /// </summary>
  /// <param name="instance"></param>
  /// <returns></returns>
  public bool TryRemoveLeastOutstandingInstance([NotNullWhen(true)] out ILambdaInstance? instance)
  {
    instance = null;

    // Get the instance with the least outstanding requests
    // Skip the "full" instances
    for (var i = 0; i < availableInstances.Length; i++)
    {
      //
      // We may loop through and move many items if they are not usable
      //
      while (availableInstances[i].TryDequeue(out var dequeuedInstance))
      {
        // We got an instance with, what we think, is the least outstanding requests
        // But, the instance may actually be closed or full due to disconnects
        // So we'll check that here
        if (!dequeuedInstance.IsOpen)
        {
          // The instance is not open for requests, so we'll drop it on the floor and move on
          continue;
        }
        if (dequeuedInstance.AvailableConnectionCount <= 0)
        {
          // The instance is full, so we'll put it in the full instances
          fullInstances.TryAdd(dequeuedInstance.Id, dequeuedInstance);

          // Hold on to this instance so we can return it, in case we see no better instance
          instance = dequeuedInstance;
          continue;
        }

        // Note: this is the only time we own this instance
        // Once we put it back in a queue it's mutable by other threads

        // We got the least busy instance that was not full
        instance = dequeuedInstance;
        return true;
      }
    }

    // If we encounted any instance at all, full or not, we're going to return it
    return instance != null;
  }

  /// <summary>
  /// Get a connection from the instance with the least outstanding requests, approximately
  ///  
  /// Note: this will perform some limited rebalancing of instances if wrong counts are encountered
  /// </summary>
  /// <returns></returns>
  public bool TryGetLeastOustandingConnection([NotNullWhen(true)] out LambdaConnection? connection, bool tentative = false)
  {
    connection = null;

    // Get the instance with the least outstanding requests
    // Skip the "full" instances
    for (var i = 0; i < availableInstances.Length; i++)
    {
      //
      // We may loop through and move many items if they are not usable
      //
      while (availableInstances[i].TryDequeue(out var instance))
      {
        // We got an instance with, what we think, is the least outstanding requests
        // But, the instance may actually be closed or full due to disconnects
        // So we'll check that here
        if (!instance.IsOpen)
        {
          // The instance is not open, so we'll drop it on the floor and move on
          continue;
        }
        if (instance.AvailableConnectionCount <= 0)
        {
          // The instance is full, so we'll put it in the full instances
          fullInstances.TryAdd(instance.Id, instance);
          continue;
        }

        // Note: this is the only time we own this instance
        // Once we put it back in a queue it's mutable by other threads

        // Get a connection from the instance
        if (!instance.TryGetConnection(out var dequeuedConnection, tentative))
        {
          // The instance is full due to disconnects
          // so we'll put it in the full instances
          // We'll have to try to get a different instance
          fullInstances.TryAdd(instance.Id, instance);
          continue;
        }

        if (instance.AvailableConnectionCount <= 0 || instance.OutstandingRequestCount >= maxConcurrentCount)
        {
          // This instance is full now (the outstanding request count was incremented on dequeue)
          fullInstances.TryAdd(instance.Id, instance);
        }
        else
        {
          // The instance is not going to be full after we dispatch to it
          // So we can put it back in the queue
          availableInstances[GetFloorQueueIndex(instance.OutstandingRequestCount)].Enqueue(instance);
        }

        // We got a connection
        connection = dequeuedConnection;
        return true;
      }
    }

    // Slower case: look through the full instances
    // This happens because of a race condition with adding instances and requests
    // but only when there are very few instances relative to the number of requests
    foreach (var lambdaId in fullInstances.Keys)
    {
      // Get the instance
      if (!fullInstances.TryRemove(lambdaId, out var instance))
      {
        // The instance is gone, so move on
        continue;
      }

      if (!instance.IsOpen)
      {
        // The instance is not open so drop it on the floor and move on
        continue;
      }

      if (instance.AvailableConnectionCount <= 0)
      {
        // The instance is full, so we'll put it back in the full instances
        fullInstances.TryAdd(instance.Id, instance);
        continue;
      }

      // Note: this is the only time we own this instance
      // Once we put it back in a queue it's mutable by other threads
      // Get a connection from the instance
      if (!instance.TryGetConnection(out var dequeuedConnection, tentative))
      {
        // The instance is actually full so put it back
        fullInstances.TryAdd(instance.Id, instance);
        continue;
      }

      // We got a connection
      connection = dequeuedConnection;

      if (instance.AvailableConnectionCount <= 0 || instance.OutstandingRequestCount >= maxConcurrentCount)
      {
        // The instance is full, so we'll put it back in the full instances
        fullInstances.TryAdd(instance.Id, instance);
      }
      else
      {
        // The instance has available connections, so put it in the correct queue
        availableInstances[GetFloorQueueIndex(instance.OutstandingRequestCount)].Enqueue(instance);
      }

      return true;
    }

    // If we get here it's the degenerate case where all instances are full
    // We'll return nothing and let this get put in the request queue
    // The dispatcher will start looking through the full instances
    // and send requests to any instances that have capacity
    // If none have capacity, it will block until one does
    return false;
  }

  /// <summary>
  /// Add a new instance to the queue
  /// </summary>
  /// <param name="instance"></param>
  public void AddInstance(ILambdaInstance instance)
  {
    if (instance == null)
    {
      throw new ArgumentNullException(nameof(instance));
    }

    var proposedIndex = GetFloorQueueIndex(instance.OutstandingRequestCount);

    // If the instance is full, put it in the full instances
    if (instance.AvailableConnectionCount <= 0 || proposedIndex >= maxConcurrentCount)
    {
      fullInstances.TryAdd(instance.Id, instance);
      return;
    }

    // Add the instance to the queue
    availableInstances[proposedIndex].Enqueue(instance);
  }

  public bool ReinstateFullInstance(ILambdaInstance instance)
  {
    // Remove the instance from the full instances
    if (fullInstances.TryRemove(instance.Id, out var _))
    {
      var index = GetFloorQueueIndex(instance.OutstandingRequestCount);

      if (instance.AvailableConnectionCount <= 0 || index >= maxConcurrentCount)
      {
        // The instance is full, so we'll put it back in the full instances
        fullInstances.TryAdd(instance.Id, instance);
        return false;
      }

      // Add the instance to a non-full queue
      try { availableInstances[index].Enqueue(instance); }
      catch (IndexOutOfRangeException)
      {
        _logger.LogError("Exception adding instance to queue, proposed index: {index}, array length {arrayLength}", index, availableInstances.Length);
        return false;
      }

      return true;
    }

    return false;
  }

  private async Task RebalanceQueue()
  {
    while (!cancellationTokenSource.IsCancellationRequested)
    {
      _logger.LogDebug("Rebalancing queue");

      // Get the instance with the least outstanding requests
      // Skip the "full" instances
      for (var i = availableInstances.Length - 1; i >= 0; i--)
      {
        // Get approximate size so we know when to stop
        // If we don't do this we'll go into a 100% CPU loop
        var approximateCount = availableInstances[i].Count;

        // Process all the items
        while (approximateCount-- > 0 && availableInstances[i].TryDequeue(out var instance))
        {
          if (instance.State == LambdaInstanceState.Closed || instance.State == LambdaInstanceState.Closing)
          {
            // The instance is not open, so we'll drop it on the floor and move on
            continue;
          }

          if (instance.AvailableConnectionCount <= 0 || instance.OutstandingRequestCount >= maxConcurrentCount)
          {
            // The instance is full, so we'll put it in the full instances
            fullInstances.TryAdd(instance.Id, instance);
            continue;
          }

          // The instance has available connections, so put it in the correct queue
          availableInstances[GetFloorQueueIndex(instance.OutstandingRequestCount)].Enqueue(instance);
        }
      }

      // Process the full instance dictionary
      foreach (var lambdaId in fullInstances.Keys)
      {
        // Get the instance
        if (!fullInstances.TryRemove(lambdaId, out var instance))
        {
          // The instance is gone, so move on
          continue;
        }

        if (!instance.IsOpen)
        {
          // The instance is not open so drop it on the floor and move on
          continue;
        }

        if (instance.AvailableConnectionCount <= 0 || instance.OutstandingRequestCount >= maxConcurrentCount)
        {
          // The instance is full, so we'll put it back in the full instances
          fullInstances.TryAdd(instance.Id, instance);
        }
        else
        {
          // The instance has available connections, so put it in the correct queue
          availableInstances[GetFloorQueueIndex(instance.OutstandingRequestCount)].Enqueue(instance);
        }
      }

      // Wait for a short period before checking again
      try
      {
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationTokenSource.Token).ConfigureAwait(false);
      }
      catch (TaskCanceledException)
      {
        break;
      }
    }
  }

  private async Task LogQueueSizes()
  {
    while (!cancellationTokenSource.IsCancellationRequested)
    {
      using var stream = new MemoryStream();
      using var stringWriter = new StreamWriter(stream);

      // Log the size of each queue in availableInstances
      for (var i = 0; i < availableInstances.Length; i++)
      {
        if (availableInstances[i].IsEmpty)
        {
          continue;
        }

        stringWriter.WriteLine($"Queue {i} size: {availableInstances[i].Count}");

        // Print the OutstandingRequestCount of the items in the queue
        foreach (var instance in availableInstances[i])
        {
          stringWriter.WriteLine($"Instance {instance.Id} state: {instance.State}, OutstandingRequestCount: {instance.OutstandingRequestCount}, AvailableConnectionCount: {instance.AvailableConnectionCount}");
        }
      }

      // Log the size of fullInstances
      stringWriter.WriteLine($"Full instances size: {fullInstances.Count}");

      // Log the AvailableConnectionCount of each instance in fullInstances
      foreach (var instance in fullInstances.Values)
      {
        stringWriter.WriteLine($"Full instance {instance.Id} state: {instance.State}, OutstandingRequestCount: {instance.OutstandingRequestCount}, AvailableConnectionCount: {instance.AvailableConnectionCount}");
      }

      stringWriter.Flush();

      var output = Encoding.UTF8.GetString(stream.ToArray());

      _logger.LogInformation("Queue Sizes:\n{output}", output);

      // Wait a bit
      try
      {
        await Task.Delay(TimeSpan.FromSeconds(10), cancellationTokenSource.Token).ConfigureAwait(false);
      }
      catch (TaskCanceledException)
      {
        break;
      }
    }
  }
}