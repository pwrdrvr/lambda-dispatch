namespace PwrDrvr.LambdaDispatch.Router;

public class IncomingRequestMiddleware
{
  private readonly RequestDelegate _next;
  private readonly Dispatcher _dispatcher;
  private readonly int[] _allowedPorts;

  public IncomingRequestMiddleware(RequestDelegate next, Dispatcher dispatcher, int[] allowedPorts)
  {
    _next = next;
    _dispatcher = dispatcher;
    _allowedPorts = allowedPorts;
  }

  public async Task InvokeAsync(HttpContext context)
  {
    if (_allowedPorts.Contains(context.Connection.LocalPort))
    {
      // Handle /health route
      if (context.Request.Path == "/health")
      {
        context.Response.StatusCode = 200;
        await context.Response.WriteAsync("OK");
        return;
      }

      // We're going to handle this
      // We will prevent the endpoint router from ever seeing this request
      await _dispatcher.AddRequest(context.Request, context.Response);
    }
    else
    {
      await _next(context);
    }
  }
}