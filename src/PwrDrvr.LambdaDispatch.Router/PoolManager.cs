using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace PwrDrvr.LambdaDispatch.Router;

public class Pool
{
  public Dispatcher Dispatcher { get; }
  public string PoolId { get; }

  public Pool(Dispatcher dispatcher, string poolId)
  {
    Dispatcher = dispatcher;
    PoolId = poolId;
  }
}

public class PoolManager
{
  private readonly IServiceProvider _serviceProvider;
  private readonly ConcurrentDictionary<string, Pool> _poolsByLambdaArn = [];
  private readonly ConcurrentDictionary<string, Pool> _poolsByPoolId = [];

  public PoolManager(IServiceProvider serviceProvider)
  {
    _serviceProvider = serviceProvider;
  }

  public Pool GetOrCreatePoolByLambdaName(string lambdaArn)
  {
    return _poolsByLambdaArn.GetOrAdd(lambdaArn, _ => CreatePool(lambdaArn));
  }


  public bool GetPoolByPoolId(string poolId, [NotNullWhen(true)] out Pool? pool)
  {
    return _poolsByPoolId.TryGetValue(poolId, out pool);
  }

  private Pool CreatePool(string lambdaArn)
  {
    using var scope = _serviceProvider.CreateScope();
    var dispatcher = scope.ServiceProvider.GetRequiredService<Dispatcher>();
    var poolId = lambdaArn == "default" ? "default" : Guid.NewGuid().ToString();
    var pool = new Pool(dispatcher, poolId);
    _poolsByPoolId.TryAdd(poolId, pool);
    return pool;
  }

  public void RemovePoolByArn(string lambdaArn)
  {
    if (_poolsByLambdaArn.TryRemove(lambdaArn, out var pool))
    {
      _poolsByPoolId.TryRemove(pool.PoolId, out _);
    }
  }
}