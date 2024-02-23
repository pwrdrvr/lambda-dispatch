using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace PwrDrvr.LambdaDispatch.Router;

public class RoundRobinLambdaInstanceQueue() : ILambdaInstanceQueue
{
  private readonly BlockingCollection<ILambdaInstance> _queue = new();

  public void AddInstance(ILambdaInstance instance)
  {
    _queue.Add(instance);
  }

  public bool TryGetConnection([NotNullWhen(true)] out LambdaConnection? connection, bool tentative = false)
  {
    connection = null;

    var approximateCount = _queue.Count;
    while (approximateCount-- >= 0 && _queue.TryTake(out ILambdaInstance? instance, 10))
    {
      // We have to skip !Open instances because they will log errors
      if (!instance.IsOpen)
      {
        // The instance is not open for requests, so we'll drop it on the floor and move on
        continue;
      }

      _queue.Add(instance);

      if (instance.TryGetConnection(out connection, tentative))
      {
        return true;
      }
    }

    return false;
  }

  public bool TryRemoveInstance([NotNullWhen(true)] out ILambdaInstance? instance)
  {
    return _queue.TryTake(out instance);
  }

  public bool ReinstateFullInstance(ILambdaInstance instance)
  {
    return false;
  }
}