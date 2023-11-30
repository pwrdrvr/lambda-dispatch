using System.Collections.Concurrent;

namespace lambda_dispatch.router
{
  public class Request
  {
    public string? Host { get; set; }
    public string? Path { get; set; }
    public string? Method { get; set; }
    public string? Body { get; set; }
  }

  public class RequestQueue
  {
    private readonly ConcurrentQueue<Request> _queue = new();

    public void Enqueue(Request request)
    {
      _queue.Enqueue(request);
    }

    public Request? Dequeue()
    {
      _queue.TryDequeue(out var request);
      return request;
    }
  }
}