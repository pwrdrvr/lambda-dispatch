using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Timeouts;
using System.Globalization;
using PwrDrvr.LambdaDispatch.Router.EmbeddedMetrics;

namespace PwrDrvr.LambdaDispatch.Router.ControlChannels.Controllers;

[Area("ControlChannels")]
[Route("api/chunked")]
public class ChunkedController : ControllerBase
{
  private readonly ILogger<ChunkedController> logger;
  private readonly IMetricsLogger metricsLogger;
  private readonly IPoolManager poolManager;

  public ChunkedController(IPoolManager poolManager, IMetricsLogger metricsLogger, ILogger<ChunkedController> logger)
  {
    this.poolManager = poolManager;
    this.logger = logger;
    this.metricsLogger = metricsLogger;
  }

  private string GetPoolId()
  {
    var poolId = "default";
    if (Request.Headers.TryGetValue("X-Pool-Id",
        out Microsoft.Extensions.Primitives.StringValues poolIdMulti) && poolIdMulti.Count == 1)
    {
      poolId = !string.IsNullOrWhiteSpace(poolIdMulti[0]) ? poolIdMulti[0] : poolId;
    }

    return poolId;
  }

  [HttpGet]
  [Route("ping/{lambdaId}")]
  public IActionResult PingInstance(string lambdaId)
  {
    var poolId = GetPoolId();
    return PingInstance(poolId, lambdaId);
  }

  [HttpGet]
  [Route("ping/poolId/{poolId}/lambdaId/{lambdaId}")]
  public IActionResult PingInstance(string poolId, string lambdaId)
  {
    if (poolManager.GetPoolByPoolId(poolId, out var pool))
    {
      pool.Dispatcher.PingInstance(lambdaId);
      return Ok();
    }
    else
    {
      return NotFound();
    }
  }

  [HttpGet]
  [Route("close/{lambdaId}")]
  public async Task<IActionResult> CloseInstance(string lambdaId)
  {
    var poolId = GetPoolId();
    return await CloseInstance(poolId, lambdaId);
  }

  [HttpGet]
  [Route("close/poolId/{poolId}/lambdaId/{lambdaId}")]
  public async Task<IActionResult> CloseInstance(string poolId, string lambdaId)
  {
    logger.LogInformation("Router.ChunkedController.CloseInstance - Closing LambdaId: {lambdaId}", lambdaId);

    if (poolManager.GetPoolByPoolId(poolId, out var pool))
    {
      await pool.Dispatcher.CloseInstance(lambdaId, lambdaInitiated: true);
    }
    else
    {
      return NotFound();
    }

    return Ok();
  }

  [DisableRequestTimeout]
  [HttpPost]
  [DisableRequestSizeLimit]
  [Route("request/{lambdaId}/{channelId}")]
  public async Task Post(string lambdaId, string channelId)
  {
    var poolId = GetPoolId();
    await Post(poolId, lambdaId, channelId);
  }

  [DisableRequestTimeout]
  [HttpPost]
  [DisableRequestSizeLimit]
  [Route("request/poolId/{poolId}/lambdaId/{lambdaId}/channelId/{channelId}")]
  public async Task Post(string poolId, string lambdaId, string channelId)
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
        if (poolManager.GetPoolByPoolId(poolId, out var pool))
        {
          var result = await pool.Dispatcher.AddConnectionForLambda(Request, Response, lambdaId, channelId);

          if (result == null || result.LambdaIDNotFound || result.Connection == null)
          {
            try
            {
              MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.LambdaConnectionRejectedCount);
              logger.LogDebug("Router.ChunkedController.Post - No LambdaInstance found for X-Pool-Id: {}, X-Lambda-Id: {}, X-Channel-Id: {}, closing", poolId, lambdaId, channelId);
              // Only in this case can we close the response because it hasn't been started
              // Have to write the response body first since the Lambda
              // is blocking on reading the Response before they will stop sending the Request
              Response.StatusCode = 409;
              Response.ContentType = "text/plain";
              await Response.StartAsync();
              await Response.WriteAsync($"No LambdaInstance found for X-PoolId: {poolId}, X-Lambda-Id: {lambdaId}, X-Channel-Id: {channelId}, closing");
              await Response.CompleteAsync();
              try { await Request.Body.CopyToAsync(Stream.Null); } catch { }
              logger.LogDebug("Router.ChunkedController.Post - No LambdaInstance found for X-PoolId: {}, X-Lambda-Id: {}, X-Channel-Id: {}, closed", poolId, lambdaId, channelId);
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
        else
        {
          logger.LogDebug("Router.ChunkedController.Post - Pool not found for X-Pool-Id: {poolId}, closing", poolId);
          Response.StatusCode = 409;
          Response.ContentType = "text/plain";
          await Response.StartAsync();
          await Response.WriteAsync($"No Pool found for X-Pool-Id: {poolId}, closing");
          await Response.CompleteAsync();
          try { await Request.Body.CopyToAsync(Stream.Null); } catch { }
          logger.LogDebug("Router.ChunkedController.Post - Pool not found for X-PoolId: {}, closed", poolId);
          return;
        }
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