using System.Buffers;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;

namespace PwrDrvr.LambdaDispatch.Router;

public class IncomingRequestMiddleware
{
  private readonly ILogger _logger = LoggerInstance.CreateLogger<IncomingRequestMiddleware>();
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
      // Disable request body buffering
      // context.Request.EnableBuffering(bufferThreshold: 0);

      // Disable response body buffering
      context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

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

      if (debugMode && context.Request.Path == "/echo-local")
      {
        try
        {
          _logger.LogInformation("/echo-local - Echoing request body back to client");


          const int bufferSize = 128 * 1024; // 128KB chunks
          byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

          try
          {
            context.Response.StatusCode = 200;
            long totalBytes = 0;

            context.Response.Headers.ContentType = "application/octet-stream";
            // Do NOT set the transfer encoding to chunked... it will cause Kestrel to NOT write the chunk headers
            // context.Response.Headers.TransferEncoding = "chunked";

            // Start the response (sends the headers)
            await context.Response.StartAsync();

            // Echo the request body back to the client
            while (true)
            {
              int bytesRead = await context.Request.Body.ReadAsync(
                  buffer, 0, bufferSize);

              if (bytesRead == 0) break;

              await context.Response.Body.WriteAsync(buffer, 0, bytesRead);
              await context.Response.Body.FlushAsync();

              totalBytes += bytesRead;
              _logger.LogInformation("/echo-local - Copied {bytesRead} bytes, {totalBytes} total bytes", bytesRead, totalBytes);
            }

            _logger.LogInformation("/echo-local - Complete, copied {totalBytes} bytes total", totalBytes);
          }
          finally
          {
            ArrayPool<byte>.Shared.Return(buffer);
          }
        }
        catch (Exception ex)
        {
          _logger.LogError(ex, "/echo-local - Error echoing request body back to client");
        }
        return;
      }

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