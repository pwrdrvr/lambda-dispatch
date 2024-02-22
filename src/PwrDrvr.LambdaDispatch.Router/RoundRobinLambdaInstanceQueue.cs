using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace PwrDrvr.LambdaDispatch.Router;

public class RoundRobinLambdaInstanceQueue() : ILambdaInstanceQueue
{
  private readonly ConcurrentQueue<ILambdaInstance> _queue = new();

  public void AddInstance(ILambdaInstance instance)
  {
    _queue.Enqueue(instance);
  }

  public bool TryGetConnection([NotNullWhen(true)] out LambdaConnection? connection, bool tentative = false)
  {
    connection = null;

    var approximateCount = _queue.Count;
    while (approximateCount-- >= 0 && _queue.TryDequeue(out ILambdaInstance? instance))
    {
      _queue.Enqueue(instance);

      if (instance.TryGetConnection(out connection, tentative))
      {
        return true;
      }
    }

    return false;
  }

  public bool TryRemoveInstance([NotNullWhen(true)] out ILambdaInstance? instance)
  {
    return _queue.TryDequeue(out instance);
  }

  public bool ReinstateFullInstance(ILambdaInstance instance)
  {
    return false;
  }
}