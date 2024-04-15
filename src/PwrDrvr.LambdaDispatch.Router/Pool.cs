using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace PwrDrvr.LambdaDispatch.Router;

public interface IPool
{
  IDispatcher Dispatcher { get; }
  string PoolId { get; }
}

public class Pool : IPool
{
  public IDispatcher Dispatcher { get; }
  public string PoolId { get; }

  public Pool(IDispatcher dispatcher, string poolId)
  {
    Dispatcher = dispatcher;
    PoolId = poolId;
  }
}
