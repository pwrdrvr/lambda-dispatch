using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;

[Route("api/chunked")]
public class ChunkedController : ControllerBase
{
  [HttpPost]
  [DisableRequestSizeLimit]
  public async Task Post()
  {
    Console.WriteLine("Got request");

    Response.StatusCode = 200;
    Response.ContentType = "text/plain";
    Response.Headers.Append("Transfer-Encoding", "chunked");

    // Print when we start the response
    Response.OnStarting(() =>
    {
      Console.WriteLine("Starting response");
      return Task.CompletedTask;
    });

    // Print when we finish the response
    Response.OnCompleted(() =>
    {
      Console.WriteLine("Finished response");
      return Task.CompletedTask;
    });

    // Write the response body
    await using (var writer = new StreamWriter(Response.Body))
    {
      await writer.WriteAsync("Chunked response");
      await writer.FlushAsync();
    }

    // Read the request body
    using (var reader = new StreamReader(Request.Body))
    {
      string? line;
      while ((line = await reader.ReadLineAsync()) != null)
      {
        // Process the line...
        await Task.Delay(1000);

        // Dump request to the console
        Console.WriteLine(line);
      }
    }
  }
}