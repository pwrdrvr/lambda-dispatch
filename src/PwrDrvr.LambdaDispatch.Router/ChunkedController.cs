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
  [Route("ping/{instanceId}")]
  public IActionResult PingInstance(string instanceId)
  {
    if (dispatcher.PingInstance(instanceId))
    {
      return Ok();
    }
    else
    {
      return NotFound();
    }
  }

  [HttpGet]
  [Route("close/{instanceId}")]
  public IActionResult CloseInstance(string instanceId)
  {
    dispatcher.CloseInstance(instanceId);

    return Ok();
  }

  [DisableRequestTimeout]
  [HttpPost]
  [DisableRequestSizeLimit]
  [Route("request/{instanceId}")]
  public async Task Post(string instanceId)
  {
    logger.LogDebug("Router.ChunkedController.Post - Start: {instanceId}", instanceId);

    using (MetricsRegistry.Metrics.Measure.Timer.Time(MetricsRegistry.LambdaRequestTimer))
    {
      try
      {
        if (!Request.Headers.TryGetValue("X-Lambda-Id", out Microsoft.Extensions.Primitives.StringValues lambdaIdMulti) || lambdaIdMulti.Count != 1)
        {
          logger.LogDebug("Router.ChunkedController.Post - No X-Lambda-Id header");
          Response.StatusCode = 400;
          Response.ContentType = "text/plain";
          await Response.StartAsync();
          await Response.WriteAsync("No X-Lambda-Id header");
          await Response.CompleteAsync();
          try { await Request.Body.CopyToAsync(Stream.Null); } catch { }
          return;
        }

        // Log an error if the request was delayed in reaching us, using the Date header added by HttpClient
        if (Request.Headers.TryGetValue("Date", out Microsoft.Extensions.Primitives.StringValues dateMulti) && dateMulti.Count == 1)
        {
          if (DateTimeOffset.TryParse(dateMulti.ToString(), out DateTimeOffset date))
          {
            var delay = DateTimeOffset.UtcNow - date;
            if (delay > TimeSpan.FromSeconds(5))
            {
              logger.LogError("Router.ChunkedController.Post - Request was delayed in receipt by {delay} ms", delay.TotalMilliseconds);
            }
          }
        }
        else
        {
          logger.LogError("Router.ChunkedController.Post - No Date header");
        }

        // Get the channel id
        if (!Request.Headers.TryGetValue("X-Channel-Id", out Microsoft.Extensions.Primitives.StringValues channelIdMulti) || channelIdMulti.Count != 1)
        {
          logger.LogDebug("Router.ChunkedController.Post - No X-Channel-Id header");
        }

        var lambdaId = lambdaIdMulti.ToString();
        var channelId = channelIdMulti.ToString();

        logger.LogDebug("Router.ChunkedController.Post - Connection from LambdaId: {lambdaId}, ChannelId: {channelId}", lambdaId, channelId);

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
        var result = await dispatcher.AddConnectionForLambda(Request, Response, lambdaId, channelId);

        if (result.LambdaIDNotFound)
        {
          try
          {
            MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.LambdaConnectionRejectedCount);
            logger.LogWarning("Router.ChunkedController.Post - No LambdaInstance found for X-Lambda-Id: {lambdaId}, X-Channel-Id: {channelId}, closing", lambdaId, channelId);
            // Only in this case can we close the response because it hasn't been started
            // Have to write the response body first since the Lambda
            // is blocking on reading the Response before they will stop sending the Request
            Response.StatusCode = 409;
            Response.ContentType = "text/plain";
            await Response.StartAsync();
            await Response.WriteAsync($"No LambdaInstance found for X-Lambda-Id: {lambdaId}, X-Channel-Id: {channelId}, closing");
            await Response.CompleteAsync();
            try { await Request.Body.CopyToAsync(Stream.Null); } catch { }
            logger.LogWarning("Router.ChunkedController.Post - No LambdaInstance found for X-Lambda-Id: {lambdaId}, X-Channel-Id: {channelId}, closed", lambdaId, channelId);
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

            // LambdaInstanceManager.AddConnectionForLambda has already closed the request/response

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