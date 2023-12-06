using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

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

  [HttpPost]
  [DisableRequestSizeLimit]
  public async Task Post()
  {
    if (!Request.Headers.TryGetValue("X-Lambda-Id", out Microsoft.Extensions.Primitives.StringValues value))
    {
      logger.LogDebug("Router.ChunkedController.Post - No X-Lambda-Id header");
      Response.StatusCode = 400;
      Response.ContentType = "text/plain";
      await Response.WriteAsync("No X-Lambda-Id header");
      return;
    }

    logger.LogDebug($"Router.ChunkedController.Post - A Lambda has connected with Id: {value}");

    // Response.Headers["Transfer-Encoding"] = "chunked";
    // This is our content type for the body that will contain a request
    // and (optional) request body
    Response.ContentType = "application/octet-stream";
    // This is our status code for the response
    Response.StatusCode = 200;
    // If you set this it hangs... it's implied that the transfer-encoding is chunked
    // and is already handled by the server

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

    // TODO: Lookup the LambdaInstance for this request
    // Based on the X-Lambda-Id header
    // We should have this LambdaInstance in a dictionary keyed by the X-Lambda-Id header
    LambdaInstance lambdaInstance = new(Request, Response, value);

    // Register this Lambda with the Dispatcher
    await dispatcher.AddLambda(lambdaInstance);

    // Wait until we have processed a request and send a response
    await lambdaInstance.TCS.Task;

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
}