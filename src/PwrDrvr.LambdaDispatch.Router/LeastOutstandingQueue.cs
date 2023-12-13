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

    Task.Run(RebalanceQueue);

    // Create a timer that calls LogQueueSizes every 15 seconds
    var timer = new System.Timers.Timer(15000);
    timer.Elapsed += (sender, e) => LogQueueSizes();
    timer.Start();
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

    if (outstandingRequestcount > maxConcurrentCount)
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
  static private ConcurrentQueue<LambdaInstance>[] InitQueues(int maxConcurrentCount)
  {
    // TODO: Get up to 10 primes from 1 to maxCurrentCount that are at least 2x the previous prime
    // But... for now, just reject anything over 10
    if (maxConcurrentCount > 10)
    {
      throw new ArgumentOutOfRangeException(nameof(maxConcurrentCount), "Max concurrent count must be less than 10");
    }

    // If an instance has maxConcurrentCount outstanding it goes in the full list
    var queueList = new ConcurrentQueue<LambdaInstance>[maxConcurrentCount];

    // Initialize the queues
    for (var i = 0; i < queueList.Length; i++)
    {
      queueList[i] = new ConcurrentQueue<LambdaInstance>();
    }

    return queueList;
  }

  // public async Task CloseMostIdleInstance()
  // {
  //   for (var i = 0; i < availableInstances.Length - 1; i++)
  //   {
  //     if (availableInstances[i].TryDequeue(out var instance))
  //     {
  //       // We got an instance with, what we think, is the least outstanding requests
  //       // But, the instance may actually be closed or full due to disconnects
  //       // So we'll check that here
  //       if (instance.State != LambdaInstanceState.Open)
  //       {
  //         // The instance is not open, so we'll drop it on the floor and move on
  //         continue;
  //       }
  //       if (instance.AvailableConnectionCount == 0)
  //       {
  //         // The instance is full, so we'll put it in the full instances
  //         fullInstances.TryAdd(instance.Id, instance);
  //         continue;
  //       }

  //       // Note: this is the only time we own this instance

  //       // Close this instance
  //       await instance.Close();
  //       break;
  //     }
  //   }
  // }

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

        if (instance.OutstandingRequestCount >= maxConcurrentCount)
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

    var proposedIndex = GetFloorQueueIndex(instance.OutstandingRequestCount);

    // If the instance is full, put it in the full instances
    if (proposedIndex == maxConcurrentCount)
    {
      fullInstances.TryAdd(instance.Id, instance);
      return;
    }

    // Add the instance to the queue
    // This may add it the idle (0) or full (maxConcurrentCount) queue
    availableInstances[proposedIndex].Enqueue(instance);
  }

  public void ReinstateFullInstance(LambdaInstance instance)
  {
    // Remove the instance from the full instances
    if (fullInstances.TryRemove(instance.Id, out var _))
    {
      // Add the instance to the queue
      // This may add it the idle (0) or full (maxConcurrentCount) queue
      availableInstances[GetFloorQueueIndex(instance.OutstandingRequestCount)].Enqueue(instance);
    }
  }

  private async Task RebalanceQueue()
  {
    while (true)
    {
      // Get the instance with the least outstanding requests
      // Skip the "full" instances
      for (var i = availableInstances.Length - 1; i >= 0; i--)
      {
        // Process all the items
        while (availableInstances[i].TryDequeue(out var instance))
        {
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

          // The instance is not going to be full after we dispatch to it
          // So we can put it back in the queue
          availableInstances[GetFloorQueueIndex(instance.OutstandingRequestCount)].Enqueue(instance);
        }
      }

      // Wait for a short period before checking again
      await Task.Delay(TimeSpan.FromMilliseconds(100));
    }
  }

  private void LogQueueSizes()
  {
    // Print the number of availalble and in use threadpool threads
    ThreadPool.GetAvailableThreads(out var availableWorkerThreads, out var availableCompletionPortThreads);
    ThreadPool.GetMaxThreads(out var maxWorkerThreads, out var maxCompletionPortThreads);
    ThreadPool.GetMinThreads(out var minWorkerThreads, out var minCompletionPortThreads);
    Console.WriteLine($"Available worker threads: {availableWorkerThreads} of {maxWorkerThreads} (min: {minWorkerThreads})");
    Console.WriteLine($"Available completion port threads: {availableCompletionPortThreads} of {maxCompletionPortThreads} (min: {minCompletionPortThreads})");

    // Log the size of each queue in availableInstances
    for (var i = 0; i < availableInstances.Length; i++)
    {
      Console.WriteLine($"Queue {i} size: {availableInstances[i].Count}");

      // Print the OutstandingRequestCount of the items in the queue
      foreach (var instance in availableInstances[i])
      {
        Console.WriteLine($"Instance {instance.Id} state: {instance.State}, outstanding requests: {instance.OutstandingRequestCount}, available connections: {instance.AvailableConnectionCount}");
      }
    }

    // Log the size of fullInstances
    Console.WriteLine($"Full instances size: {fullInstances.Count}");

    // Log the AvailableConnectionCount of each instance in fullInstances
    foreach (var instance in fullInstances.Values)
    {
      Console.WriteLine($"Full instance {instance.Id} available connections: {instance.AvailableConnectionCount}");
    }
  }
}