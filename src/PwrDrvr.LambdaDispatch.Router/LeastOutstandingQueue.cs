namespace PwrDrvr.LambdaDispatch.Router;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

public interface ILeastOutstandingItem
{
  int OutstandingRequestCount { get; }

  string Id { get; }

  LambdaConnection? TryGetConnection();
}

/// <summary>
/// Gives approximate least outstanding requests for items that may
/// have changes in outstanding requests asynchronously in the background
/// </summary>
public class LeastOutstandingQueue
{
  public int MaxConcurrentCount { get; }

  private readonly int maxConcurrentCount;

  private readonly ConcurrentQueue<LambdaInstance>[] availableInstances;

  private readonly ConcurrentDictionary<string, LambdaInstance> fullInstances = new();

  public LeastOutstandingQueue(int maxConcurrentCount)
  {
    this.maxConcurrentCount = maxConcurrentCount;

    availableInstances = InitQueues(maxConcurrentCount);
  }

  private int GetFloorQueueIndex(int count)
  {
    if (count < 0 || count > maxConcurrentCount)
    {
      throw new ArgumentOutOfRangeException(nameof(count), $"Count must be between 0 and {maxConcurrentCount}");
    }

    // TODO: If we use primes, just find the highest prime that is less than or equal to count
    return count;
  }

  /// <summary>
  /// Create the array of queues
  /// </summary>
  /// <param name="maxConcurrentCount"></param>
  /// <returns></returns>
  /// <exception cref="ArgumentOutOfRangeException"></exception>
  static private ConcurrentQueue<LambdaInstance>[] InitQueues(int maxConcurrentCount)
  {
    // TODO: Get up to 10 primes from 1 to maxCurrentCount that are at least 2x the previous prime
    // But... for now, just reject anything over 10
    if (maxConcurrentCount > 10)
    {
      throw new ArgumentOutOfRangeException(nameof(maxConcurrentCount), "Max concurrent count must be less than 10");
    }

    // We add 2 because we want to use `0` for idle and `maxConcurrentCount` for full
    var queueList = new ConcurrentQueue<LambdaInstance>[maxConcurrentCount + 2];

    // Initialize the queues
    for (var i = 0; i < queueList.Length; i++)
    {
      queueList[i] = new ConcurrentQueue<LambdaInstance>();
    }

    return queueList;
  }

  public async Task CloseMostIdleInstance()
  {
    for (var i = 0; i < availableInstances.Length - 1; i++)
    {
      if (availableInstances[i].TryDequeue(out var instance))
      {
        // We got an instance with, what we think, is the least outstanding requests
        // But, the instance may actually be closed or full due to disconnects
        // So we'll check that here
        if (instance.State != LambdaInstanceState.Open)
        {
          // The instance is not open, so we'll drop it on the floor and move on
          continue;
        }
        if (instance.AvailableConnectionCount == 0)
        {
          // The instance is full, so we'll put it in the full instances
          fullInstances.TryAdd(instance.Id, instance);
          continue;
        }

        // Note: this is the only time we own this instance

        // Close this instance
        await instance.Close();
        break;
      }
    }
  }

  /// <summary>
  /// Get the instance with the least outstanding requests, approximately
  ///  
  /// Note: this will perform some limited rebalancing of instances if wrong counts are encountered
  /// </summary>
  /// <returns></returns>
  public bool TryGetLeastOustandingConnection([NotNullWhen(true)] out LambdaConnection? connection)
  {
    connection = null;

    // Get the instance with the least outstanding requests
    // Skip the "full" instances
    for (var i = 0; i < availableInstances.Length - 1; i++)
    {
      if (availableInstances[i].TryDequeue(out var instance))
      {
        // We got an instance with, what we think, is the least outstanding requests
        // But, the instance may actually be closed or full due to disconnects
        // So we'll check that here
        if (instance.State != LambdaInstanceState.Open)
        {
          // The instance is not open, so we'll drop it on the floor and move on
          continue;
        }
        if (instance.AvailableConnectionCount == 0)
        {
          // The instance is full, so we'll put it in the full instances
          fullInstances.TryAdd(instance.Id, instance);
          continue;
        }

        // Note: this is the only time we own this instance
        // Once we put it back in a queue it's mutable by other threads

        // Get a connection from the instance
        if (!instance.TryGetConnection(out var dequeuedConnection))
        {
          // The instance is full due to disconnects
          // so we'll put it in the full instances
          // We'll have to try to get a different instance
          fullInstances.TryAdd(instance.Id, instance);
          continue;
        }

        if (instance.OutstandingRequestCount == maxConcurrentCount - 1)
        {
          // The instance is going to be full after we dispatch to it
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
  public void AddInstance(LambdaInstance instance)
  {
    int queueIndex = GetFloorQueueIndex(instance.AvailableConnectionCount);

    // Add the instance to the queue
    // This may add it the idle (0) or full (maxConcurrentCount) queue
    availableInstances[queueIndex].Enqueue(instance);
  }

  public void ReinstateFullInstance(LambdaInstance instance)
  {
    // Remove the instance from the full instances
    fullInstances.TryRemove(instance.Id, out var removedInstance);

    if (removedInstance != null)
    {
      // Add the instance to the queue
      // This may add it the idle (0) or full (maxConcurrentCount) queue
      availableInstances[GetFloorQueueIndex(instance.AvailableConnectionCount)].Enqueue(instance);
    }
  }
}