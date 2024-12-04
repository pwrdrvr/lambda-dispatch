using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace PwrDrvr.LambdaDispatch.Router;

public enum PendingRequestState
{
  Pending,
  Dispatched,
  Completed,
  Aborted
}

public class PendingRequest : IDisposable
{
  private PendingRequestState _state = PendingRequestState.Pending;
  private readonly object _stateLock = new();

  // These are private because we only expose them if the
  // request is not aborted when attempting to dispatch
  private readonly HttpRequest _request;
  private readonly HttpResponse _response;

  public TaskCompletionSource ResponseFinishedTCS { get; private set; } = new TaskCompletionSource();
  public CancellationTokenSource GatewayTimeoutCTS { get; private set; } = new CancellationTokenSource();
  public DateTime? DispatchTime { get; private set; }
  public bool Dispatched { get; private set; } = false;

  private readonly Stopwatch _swDispatch = Stopwatch.StartNew();
  private readonly Stopwatch _swResponse = Stopwatch.StartNew();

  public PendingRequest(HttpRequest request, HttpResponse response)
  {
    _request = request;
    _response = response;
  }

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

  /// <summary>
  /// Marks the cancellation token source as cancelled
  /// Does not do any other sort of cleanup
  /// </summary>
  public bool Abort()
  {
    lock (_stateLock)
    {
      if (_state == PendingRequestState.Aborted)
      {
        return false;
      }

      _state = PendingRequestState.Aborted;
      GatewayTimeoutCTS.Cancel();
      return true;
    }
  }

  /// <summary>
  /// Dispatches the request
  /// Returns the request and response
  /// Can only be called once
  /// </summary>
  /// <param name="incomingRequest"></param>
  /// <param name="incomingResponse"></param>
  /// <returns></returns>
  public bool Dispatch(
    [NotNullWhen(true)] out HttpRequest? incomingRequest,
    [NotNullWhen(true)] out HttpResponse? incomingResponse
    )
  {
    incomingRequest = null;
    incomingResponse = null;

    lock (_stateLock)
    {
      if (_state != PendingRequestState.Pending)
      {
        return false;
      }

      incomingRequest = _request;
      incomingResponse = _response;
      _swDispatch.Stop();
      Dispatched = true;
      _state = PendingRequestState.Dispatched;
      return true;
    }
  }

  public void Dispose()
  {
    GatewayTimeoutCTS.Dispose();
  }
}