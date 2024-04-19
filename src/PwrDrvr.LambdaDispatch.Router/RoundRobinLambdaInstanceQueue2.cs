using System.Diagnostics.CodeAnalysis;

namespace PwrDrvr.LambdaDispatch.Router;

/// <summary>
/// A round-robin queue for Lambda instances.
/// Does not remove and re-add instances from a queue while getting a connection.
/// This should reduce CPU and increase the chances that an incoming request can
/// get a connection.
/// </summary>
public class RoundRobinLambdaInstanceQueue2() : ILambdaInstanceQueue
{
  private List<ILambdaInstance> _list = [];

  private readonly ReaderWriterLockSlim _rwlsQueue = new();

  private volatile int _prevSlot = -1;

  public void AddInstance(ILambdaInstance instance)
  {
    try
    {
      _rwlsQueue.EnterWriteLock();
      _list.Add(instance);
    }
    finally
    {
      _rwlsQueue.ExitWriteLock();
    }
  }

  public bool TryGetConnection([NotNullWhen(true)] out ILambdaConnection? connection, bool tentative = false)
  {
    connection = null;

    try
    {
      _rwlsQueue.EnterReadLock();

      var approximateCount = 2 * _list.Count;

      while (approximateCount-- >= 0)
      {
        var slot = Interlocked.Increment(ref _prevSlot);
        if (_list.Count == 0)
        {
          return false;
        }
        if (slot >= _list.Count)
        {
          slot = 0;
          Interlocked.Exchange(ref _prevSlot, 0);
        }
        var instance = _list[slot];

        // We have to skip !Open instances because they will log errors
        if (!instance.IsOpen)
        {
          // Skip this it will get cleaned up later
          continue;
        }

        if (instance.TryGetConnection(out connection, tentative))
        {
          return true;
        }
      }

      return false;
    }
    finally
    {
      _rwlsQueue.ExitReadLock();
    }
  }

  public bool TryRemoveInstance([NotNullWhen(true)] out ILambdaInstance? instance)
  {
    try
    {
      _rwlsQueue.EnterWriteLock();

      // Remove all !IsOpen instances
      _list = _list.Where(i => i.IsOpen).ToList();

      // Check if we have any instances left
      if (_list.Count == 0)
      {
        instance = null;
        return false;
      }

      // Give back the first instance, which should be the oldest
      instance = _list[0];
      _list.RemoveAt(0);
      return true;
    }
    finally
    {
      if (_prevSlot >= _list.Count)
      {
        Interlocked.Exchange(ref _prevSlot, 0);
      }
      _rwlsQueue.ExitWriteLock();
    }
  }

  public bool ReinstateFullInstance(ILambdaInstance instance)
  {
    return false;
  }
}