using System.Net.Http;
using System.Net.Sockets;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace PwrDrvr.LambdaDispatch.Router.TestClient;

public class Program
{
  static public async Task TestChunkedRequest()
  {
    using (var client = new TcpClient("localhost", 5003))
    using (var stream = client.GetStream())
    using (var writer = new StreamWriter(stream))
    using (var reader = new StreamReader(stream))
    {
      // Send the request headers
      await writer.WriteAsync("POST /api/chunked HTTP/1.1\r\n");
      await writer.WriteAsync("Host: localhost:5003\r\n");
      await writer.WriteAsync("Content-Type: text/plain\r\n");
      await writer.WriteAsync("Transfer-Encoding: chunked\r\n");
      await writer.WriteAsync("\r\n");
      await writer.FlushAsync();

      // Start a task to read the response headers and body
      var readResponseTask = Task.Run(async () =>
      {
        // Read the response headers
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
          Console.WriteLine(line);
        }

        // Read the response body
        while ((line = await reader.ReadLineAsync()) != null)
        {
          Console.WriteLine(line);
        }
      });

      // Send the request body in chunks
      for (int i = 0; i < 10; i++)
      {
        var chunk = $"Chunk {i}\r\n";
        var chunkSize = chunk.Length.ToString("X");

        Console.WriteLine($"Sending chunk {i} of size {chunkSize}");

        await writer.WriteAsync($"{chunkSize}\r\n");
        await writer.WriteAsync(chunk);
        await writer.WriteAsync("\r\n");
        await writer.FlushAsync();
      }

      // Send the last chunk
      await writer.WriteAsync("0\r\n\r\n");
      await writer.FlushAsync();

      // Wait for the response reading task to complete
      await readResponseTask;
    }
  }

  static async Task Main(string[] args)
  {
    await TestChunkedRequest();
  }
}