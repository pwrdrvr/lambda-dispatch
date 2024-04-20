using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace PwrDrvr.LambdaDispatch.Router;

public interface IPoolManager
{
  IPool GetOrCreatePoolByLambdaName(string lambdaArn);
  bool GetPoolByPoolId(string poolId, [NotNullWhen(true)] out IPool? pool);
  void RemovePoolByArn(string lambdaArn);
}

public class PoolManager : IPoolManager
{
  private readonly IServiceProvider _serviceProvider;
  private readonly IConfig _config;
  private readonly Dictionary<string, IPool> _poolsByLambdaArn = [];
  private readonly Dictionary<string, IPool> _poolsByPoolId = [];
  private readonly ReaderWriterLockSlim _lock = new();

  public PoolManager(IServiceProvider serviceProvider, IConfig config)
  {
    _serviceProvider = serviceProvider;
    _config = config;
  }

  public IPool GetOrCreatePoolByLambdaName(string lambdaArn)
  {
    _lock.EnterUpgradeableReadLock();
    try
    {
      if (!_poolsByLambdaArn.TryGetValue(lambdaArn, out var pool))
      {
        _lock.EnterWriteLock();
        try
        {
          if (!_poolsByLambdaArn.TryGetValue(lambdaArn, out pool)) // Double-check locking
          {
            pool = CreatePool(lambdaArn);
            _poolsByLambdaArn[lambdaArn] = pool;
          }
        }
        finally
        {
          _lock.ExitWriteLock();
        }
      }

      return pool;
    }
    finally
    {
      _lock.ExitUpgradeableReadLock();
    }
  }

  public bool GetPoolByPoolId(string poolId, [NotNullWhen(true)] out IPool? pool)
  {
    _lock.EnterReadLock();
    try
    {
      return _poolsByPoolId.TryGetValue(poolId, out pool);
    }
    finally
    {
      _lock.ExitReadLock();
    }
  }

  /// <summary>
  /// We only get here once for each lambdaArn value
  /// We can validate the ARN and reject the create if it is invalid
  /// </summary>
  /// <param name="lambdaArn"></param>
  /// <returns></returns>
  /// <exception cref="ArgumentException"></exception>
  private IPool CreatePool(string lambdaArn)
  {
    var poolId = lambdaArn == "default" ? "default" : Guid.NewGuid().ToString();
    var lambaArnToUse = lambdaArn == "default" ? _config.FunctionName : lambdaArn;
    if (!LambdaArnParser.IsValidLambdaArgument(lambaArnToUse))
    {
      throw new ArgumentException("Invalid Lambda ARN", nameof(lambdaArn));
    }
    using var scope = _serviceProvider.CreateScope();
    var poolOptions = scope.ServiceProvider.GetRequiredService<IPoolOptions>();
    poolOptions.Setup(lambaArnToUse, poolId);
    var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
    var pool = new Pool(dispatcher, poolId);
    if (!_poolsByPoolId.TryAdd(poolId, pool))
    {
      throw new InvalidOperationException("PoolId already exists");
    }
    return pool;
  }

  public void RemovePoolByArn(string lambdaArn)
  {
    _lock.EnterWriteLock();
    try
    {
      if (_poolsByLambdaArn.Remove(lambdaArn, out var pool))
      {
        _poolsByPoolId.Remove(pool.PoolId, out _);
      }
    }
    finally
    {
      _lock.ExitWriteLock();
    }
  }
}