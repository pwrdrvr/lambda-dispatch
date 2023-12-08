namespace PwrDrvr.LambdaDispatch.Router;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

public class LambdaInstanceManager
{
  private readonly LeastOutstandingQueue _leastOutstandingQueue;

  private volatile int _runningCount = 0;

  private volatile int _desiredCount = 0;

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

  /// <summary>
  /// Decrement the desired count
  /// 
  /// This will not immediately stop an instance
  /// </summary>
  public async Task DecrementDesiredCount()
  {
    Interlocked.Decrement(ref _desiredCount);

    // Check if we should signal one Lambda to close
    if (_runningCount > _desiredCount)
    {
      await _leastOutstandingQueue.CloseMostIdleInstance();
    }
  }

  public async Task<LambdaConnection?> AddConnectionForLambda(HttpRequest request, HttpResponse response, string lambdaId)
  {
    // Get the instance for the lambda
    if (_instances.TryGetValue(lambdaId, out var instance))
    {
      // Add the connection to the instance
      // The instance will eventually get rebalanced in the least outstanding queue
      return instance.AddConnection(request, response);
    }

    Console.WriteLine($"Connection added to Lambda Instance {lambdaId} that does not exist - closing with 1001");

    // Close the connection
    try
    {
      response.StatusCode = 1001;
      await response.CompleteAsync();
      response.Body.Close();
      request.Body.Close();
    }
    catch (Exception ex)
    {
      Console.WriteLine("Exception closing down connection to Lambda");
      Console.WriteLine(ex.Message);
    }

    return null;
  }

  /// <summary>
  /// Start a new LambdaInstance and increment the desired count
  /// </summary>
  /// <returns></returns>
  public async Task StartNewInstance()
  {
    // If we get called we're starting a new instance
    Interlocked.Increment(ref _desiredCount);

    // Start a new LambdaInstance and add it to the list
    var instance = new LambdaInstance(_maxConcurrentCount);

    // Add the instance to the collection
    _instances.TryAdd(instance.Id, instance);

    instance.OnOpen += (instance) =>
    {
      // We need to keep track of how many Lambdas are running
      // We will replace this one if it's still desired
      Interlocked.Increment(ref _runningCount);

      // Add the instance to the least outstanding queue
      _leastOutstandingQueue.AddInstance(instance);
    };

    instance.OnInvocationComplete += async (instance) =>
    {
      Interlocked.Decrement(ref _runningCount);

      // Remove this instance from the collection
      _instances.TryRemove(instance.Id, out _);

      // The instance will already be marked as closing

      // We need to keep track of how many Lambdas are running
      // We will replace this one if it's still desired

      if (_runningCount < _desiredCount)
      {
        // We need to start a new instance
        await this.StartNewInstance();
      }
    };

    // This is only async because of the ENI IP lookup
    await instance.Start();
  }
}
