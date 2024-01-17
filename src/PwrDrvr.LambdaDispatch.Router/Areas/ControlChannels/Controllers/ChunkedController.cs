using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Timeouts;
using System.Globalization;

namespace PwrDrvr.LambdaDispatch.Router.ControlChannels.Controllers;

[Area("ControlChannels")]
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
  [Route("close/{lambdaId}")]
  public IActionResult CloseInstance(string lambdaId)
  {
    logger.LogInformation("Router.ChunkedController.CloseInstance - Closing LambdaId: {lambdaId}", lambdaId);
    dispatcher.CloseInstance(lambdaId);

    return Ok();
  }

  [DisableRequestTimeout]
  [HttpPost]
  [DisableRequestSizeLimit]
  [Route("request/{lambdaId}/{channelId}")]
  public async Task Post(string lambdaId, string channelId)
  {
    logger.LogDebug("Router.ChunkedController.Post - Start for LambdaId: {lambdaId}, ChannelId: {channelId}", lambdaId, channelId);

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
        if (this.Request.Headers.TryGetValue("Date", out var dateValues)
         && dateValues.Count == 1
         && DateTime.TryParse(dateValues.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var requestDate))
        {
          var delay = DateTimeOffset.UtcNow - requestDate;
          if (delay > TimeSpan.FromSeconds(5))
          {
            logger.LogWarning("Router.ChunkedController.Post - Request received at {time} was delayed in receipt by {delay} ms, LambdaId: {lambdaId}, ChannelId: {channelId}", requestDate.ToLocalTime().ToString("o"), delay.TotalMilliseconds, lambdaId, channelId);
          }
        }

        logger.LogDebug("Router.ChunkedController.Post - Connection from LambdaId: {lambdaId}, ChannelId: {channelId}", lambdaId, channelId);

        // Print when we start the response
        Response.OnStarting(() =>
        {
          logger.LogDebug("Starting response, LambdaId: {lambdaId}, ChannelId: {channelId}", lambdaId, channelId);
          return Task.CompletedTask;
        });

        // Print when we finish the response
        Response.OnCompleted(() =>
        {
          logger.LogDebug("Finished response, LambdaId: {lambdaId}, ChannelId: {channelId}", lambdaId, channelId);
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

        logger.LogDebug("Router.ChunkedController.Post - Finished - Response will be closed, LambdaId: {lambdaId}, ChannelId: {channelId}", lambdaId, channelId);
      }
      catch (Exception ex)
      {
        if (Request.HttpContext.RequestAborted.IsCancellationRequested)
        {
          // If we already aborted the request there is no need to rethrow
          logger.LogDebug("Router.ChunkedController.Post - Request aborted, LambdaId: {lambdaId}, ChannelId: {channelId}", lambdaId, channelId);
        }
        else
        {
          // If we didn't abort the request then this is a case we're not handling
          logger.LogError(ex, "Router.ChunkedController.Post - Exception, LambdaId: {lambdaId}, ChannelId: {channelId}", lambdaId, channelId);
          throw;
        }
      }
    }
  }
}