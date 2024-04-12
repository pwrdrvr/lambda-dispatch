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
  private readonly IConfig _config;
  private readonly ConcurrentDictionary<string, Pool> _poolsByLambdaArn = [];
  private readonly ConcurrentDictionary<string, Pool> _poolsByPoolId = [];

  public PoolManager(IServiceProvider serviceProvider, IConfig config)
  {
    _serviceProvider = serviceProvider;
    _config = config;
  }

  public Pool GetOrCreatePoolByLambdaName(string lambdaArn)
  {
    return _poolsByLambdaArn.GetOrAdd(lambdaArn, _ => CreatePool(lambdaArn));
  }

  public bool GetPoolByPoolId(string poolId, [NotNullWhen(true)] out Pool? pool)
  {
    return _poolsByPoolId.TryGetValue(poolId, out pool);
  }

  /// <summary>
  /// We only get here once for each lambdaArn value
  /// We can validate the ARN and reject the create if it is invalid
  /// </summary>
  /// <param name="lambdaArn"></param>
  /// <returns></returns>
  /// <exception cref="ArgumentException"></exception>
  private Pool CreatePool(string lambdaArn)
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
    var dispatcher = scope.ServiceProvider.GetRequiredService<Dispatcher>();
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