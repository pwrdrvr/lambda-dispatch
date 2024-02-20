using System.Diagnostics;

namespace PwrDrvr.LambdaDispatch.Router;

public class PendingRequest
{
  public HttpRequest Request { get; private set; }
  public HttpResponse Response { get; private set; }
  public TaskCompletionSource ResponseFinishedTCS { get; private set; } = new TaskCompletionSource();
  public CancellationTokenSource GatewayTimeoutCTS { get; private set; } = new CancellationTokenSource();
  public DateTime? DispatchTime { get; private set; }
  public bool Dispatched { get; private set; } = false;

  private readonly Stopwatch _swDispatch = Stopwatch.StartNew();
  private readonly Stopwatch _swResponse = Stopwatch.StartNew();

  /// <summary>
  /// Time the request spent waiting for dispatch
  /// </summary>
  public TimeSpan DispatchDelay
  {
    get
    {
      return _swDispatch.Elapsed;
    }
  }

  /// <summary>
  /// Total duration of the request, including waiting for dispatch
  /// </summary>
  public TimeSpan Duration
  {
    get
    {
      return _swResponse.Elapsed;
    }
  }

  /// <summary>
  /// Time the request took after it was dispatched
  /// </summary>
  public TimeSpan DurationAfterDispatch
  {
    get
    {
      if (_swDispatch.IsRunning)
      {
        return _swDispatch.Elapsed;
      }

      return _swResponse.Elapsed - _swDispatch.Elapsed;
    }
  }

  public PendingRequest(HttpRequest request, HttpResponse response)
  {
    Request = request;
    Response = response;
  }

  public void Abort()
  {
    GatewayTimeoutCTS.Cancel();
  }

  public void RecordDispatchTime()
  {
    if (Dispatched)
    {
      return;
    }
    Dispatched = true;
    _swDispatch.Stop();
  }
}