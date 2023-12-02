namespace PwrDrvr.LambdaDispatch.Router;

using System.Collections.Concurrent;

public class Request
{
  // TODO: Handle to incoming request accepted by ASP.Net
}

public class Dispatcher
{
  // Requests that are dispatched to a Lambda - Keyed by request ID
  private readonly ConcurrentDictionary<string, Request> _runningRequests = new();

  // Requests that are waiting to be dispatched to a Lambda
  private readonly ConcurrentQueue<Request> _pendingRequests = new();

  // Busy Lambda Connections - Keyed by request ID
  private readonly ConcurrentDictionary<string, Request> _busyLambdas = new();

  // Idle Lambda Connections
  private readonly ConcurrentQueue<Request> _idleLambdas = new();

  // Add a new request, dispatch immediately if able
  public void AddRequest(Request request)
  {
  }


  // Add a new lambda, dispatch to it immediately if a request is waiting
  public void AddLambda(Request lambda)
  {

  }
}