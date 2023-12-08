namespace PwrDrvr.LambdaDispatch.Router;


public class LambdaInstanceManager
{
  private readonly LeastOutstandingQueue _leastOutstandingQueue;

  private volatile int _runningCount = 0;

  private volatile int _desiredCount = 0;

  public LambdaInstanceManager(int maxConcurrentCount)
  {
    _leastOutstandingQueue = new(maxConcurrentCount);
  }

  public LambdaConnection? TryGetConnection()
  {
    // Return an available instance or start a new one if none are available
    return this._leastOutstandingQueue.TryGetLeastOustandingConnection();
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

  /// <summary>
  /// Start a new LambdaInstance and increment the desired count
  /// </summary>
  /// <returns></returns>
  public async Task StartNewInstance()
  {
    // If we get called we're starting a new instance
    Interlocked.Increment(ref _desiredCount);

    // Start a new LambdaInstance and add it to the list
    var instance = new LambdaInstance();

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
