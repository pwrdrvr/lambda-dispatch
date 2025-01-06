using Microsoft.AspNetCore.Http.Extensions;

namespace PwrDrvr.LambdaDispatch.Router;

public class IncomingRequestMiddleware
{
  private readonly RequestDelegate _next;
  private readonly IPoolManager _poolManager;
  private readonly int[] _allowedPorts;

  public IncomingRequestMiddleware(RequestDelegate next, IPoolManager poolManager, int[] allowedPorts)
  {
    _next = next;
    _poolManager = poolManager;
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

      // Get the X-Lambda-Name header value, if any, or default to "default"
      var lambdaArn = context.Request.Headers["X-Lambda-Name"].FirstOrDefault() ?? "default";
      var debugMode = context.Request.Headers["X-Lambda-Dispatch-Debug"].FirstOrDefault() == "true";

      AccessLogProps accessLogProps = new()
      {
        Method = context.Request.Method,
        Uri = context.Request.GetDisplayUrl(),
        Protocol = context.Request.Protocol,
        RemoteAddress = context.Request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "-",
        UserAgent = context.Request.Headers.UserAgent.Count > 0 ? context.Request.Headers.UserAgent.ToString() : "-",
      };

      // We're going to handle this
      // We will prevent the endpoint router from ever seeing this request
      await _poolManager.GetOrCreatePoolByLambdaName(lambdaArn).Dispatcher.AddRequest(context.Request, context.Response, accessLogProps, debugMode);
    }
    else
    {
      await _next(context);
    }
  }
}