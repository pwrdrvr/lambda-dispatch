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

  public bool TryGetConnection([NotNullWhen(true)] out ILambdaConnection? connection, bool tentative = false)
  {
    connection = null;

    var approximateCount = _queue.Count;
    while (approximateCount-- >= 0 && _queue.TryDequeue(out ILambdaInstance? instance))
    {
      // We have to skip !Open instances because they will log errors
      if (!instance.IsOpen)
      {
        // The instance is not open for requests, so we'll drop it on the floor and move on
        continue;
      }

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
    while (_queue.TryDequeue(out instance))
    {
      // Remove all the non-open instances
      // Return an instance only if it is open
      if (instance.IsOpen)
      {
        return true;
      }
    }

    return false;
  }

  public bool ReinstateFullInstance(ILambdaInstance instance)
  {
    return false;
  }
}