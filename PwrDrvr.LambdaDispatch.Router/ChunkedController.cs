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
        await Task.Delay(5000);

        // Dump request to the console
        Console.WriteLine(line);
      }
    }
  }
}