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
  private readonly ConcurrentDictionary<string, IPool> _poolsByLambdaArn = [];
  private readonly ConcurrentDictionary<string, IPool> _poolsByPoolId = [];

  public PoolManager(IServiceProvider serviceProvider, IConfig config)
  {
    _serviceProvider = serviceProvider;
    _config = config;
  }

  public IPool GetOrCreatePoolByLambdaName(string lambdaArn)
  {
    return _poolsByLambdaArn.GetOrAdd(lambdaArn, _ => CreatePool(lambdaArn));
  }

  public bool GetPoolByPoolId(string poolId, [NotNullWhen(true)] out IPool? pool)
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
    if (_poolsByLambdaArn.TryRemove(lambdaArn, out var pool))
    {
      _poolsByPoolId.TryRemove(pool.PoolId, out _);
    }
  }
}