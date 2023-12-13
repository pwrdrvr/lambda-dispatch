using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Timeouts;

namespace PwrDrvr.LambdaDispatch.Router;

[Route("api/chunked")]
public class ChunkedController : ControllerBase
{
  private readonly ILogger<ChunkedController> logger;
  private readonly Dispatcher dispatcher;

  public ChunkedController(Dispatcher dispatcher, ILogger<ChunkedController> logger)
  {
    this.dispatcher = dispatcher;
    this.logger = logger;
  }

  [HttpGet]
  [Route("close/{instanceId}")]
  public async Task<IActionResult> CloseInstance(string instanceId)
  {
    await dispatcher.CloseInstance(instanceId);

    return Ok();
  }

  [DisableRequestTimeout]
  [HttpPost]
  [DisableRequestSizeLimit]
  public async Task Post()
  {
    using (MetricsRegistry.Metrics.Measure.Timer.Time(MetricsRegistry.LambdaRequestTimer))
    {
      try
      {
        if (!Request.Headers.TryGetValue("X-Lambda-Id", out Microsoft.Extensions.Primitives.StringValues lambdaIdMulti) || lambdaIdMulti.Count != 1)
        {
          logger.LogDebug("Router.ChunkedController.Post - No X-Lambda-Id header");
          Response.StatusCode = 400;
          Response.ContentType = "text/plain";
          await Request.Body.DisposeAsync();
          await Response.WriteAsync("No X-Lambda-Id header");
          await Response.Body.DisposeAsync();
          return;
        }

        var lambdaId = lambdaIdMulti.ToString();

        logger.LogDebug("Router.ChunkedController.Post - A Lambda has connected with Id: {lambdaId}", lambdaId);

        // Print when we start the response
        Response.OnStarting(() =>
        {
          logger.LogDebug("Starting response");
          return Task.CompletedTask;
        });

        // Print when we finish the response
        Response.OnCompleted(() =>
        {
          logger.LogDebug("Finished response");
          return Task.CompletedTask;
        });

        // Response.Headers["Transfer-Encoding"] = "chunked";
        // This is our content type for the body that will contain a request
        // and (optional) request body
        Response.ContentType = "application/octet-stream";
        // This is our status code for the response
        Response.StatusCode = 200;
        // If you set this it hangs... it's implied that the transfer-encoding is chunked
        // and is already handled by the server

        // TODO: Lookup the LambdaInstance for this request
        // Based on the X-Lambda-Id header
        // We should have this LambdaInstance in a dictionary keyed by the X-Lambda-Id header

        // Register this Lambda with the Dispatcher
        var result = await dispatcher.AddConnectionForLambda(Request, Response, lambdaId);

        if (result.LambdaIDNotFound)
        {
          try
          {
            MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.LambdaConnectionRejectedCount);
            logger.LogInformation("Router.ChunkedController.Post - No LambdaInstance found for X-Lambda-Id header: {lambdaId}", lambdaId);
            // Only in this case can we close the response because it hasn't been started
            Response.StatusCode = 409;
            Response.ContentType = "text/plain";
            await Response.WriteAsync("No LambdaInstance found for X-Lambda-Id header");
            await Response.Body.DisposeAsync();
          }
          catch (Exception ex)
          {
            logger.LogError(ex, "Router.ChunkedController.Post - Exception");
          }
          return;
        }
        else if (result.Connection == null)
        {
          try
          {
            MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.LambdaConnectionRejectedCount);
            logger.LogInformation("Router.ChunkedController.Post - LambdaInstance found for X-Lambda-Id header: {lambdaId} but it is already closed", lambdaId);

            // In this case the connection should already have been cleaned up
          }
          catch (Exception ex)
          {
            logger.LogError(ex, "Router.ChunkedController.Post - Exception");
          }
          return;
        }

        // Wait until we have processed a request and sent a response
        await result.Connection.TCS.Task;

        logger.LogDebug("Router.ChunkedController.Post - Finished - Response will be closed");

        // // Write the response body
        // var writer = new StreamWriter(Response.Body);

        // // TODO: Loop through all the headers in the Request and write them to the Response Body
        // foreach (var header in Request.Headers)
        // {
        //   await writer.WriteAsync($"{header.Key}: {header.Value}\r\n");
        // }

        // // Write the Request body to the Response body


        // await writer.WriteAsync("Chunked response");
        // await writer.FlushAsync();
        // // Close the response body
        // await writer.DisposeAsync();

        // // Read the request body
        // using var reader = new StreamReader(Request.Body);
        // string? line;
        // while ((line = await reader.ReadLineAsync()) != null)
        // {
        //   // Dump request to the console
        //   logger.LogDebug(line);
        // }

        // // Close the response body
        // await writer.DisposeAsync();
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "Router.ChunkedController.Post - Exception");
        throw;
      }
    }
  }
}