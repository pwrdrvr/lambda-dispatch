using System.Net.Sockets;

namespace PwrDrvr.LambdaDispatch.LambdaLB;

public class ReverseRequester : IAsyncDisposable
{
  private readonly string _dispatcherUrl;
  private readonly string _id;

  private readonly TcpClient _client;

  private readonly NetworkStream _stream;

  private readonly StreamReader _reader;

  private readonly StreamWriter _writer;

  public ReverseRequester(string id, string dispatcherUrl)
  {
    _id = id;
    _dispatcherUrl = dispatcherUrl;

    // Parse out the host, port, and path
    var uri = new Uri(_dispatcherUrl);

    _client = new TcpClient(uri.Host, uri.Port);
    _stream = _client.GetStream();
    _reader = new StreamReader(_stream);
    _writer = new StreamWriter(_stream);

    // Send the request headers
    _writer.Write($"POST {uri.PathAndQuery} HTTP/1.1\r\n");
    _writer.Write($"Host: {uri.Host}:{uri.Port}\r\n");
    _writer.Write("Content-Type: application/octet-stream\r\n");
    _writer.Write("Transfer-Encoding: chunked\r\n");
    _writer.Write("\r\n");
  }

  // Async Dispose
  public async ValueTask DisposeAsync()
  {
    await _writer.DisposeAsync();
    _reader.Dispose();
    await _stream.DisposeAsync();
    _client.Dispose();
  }

  public async Task GetRequest()
  {
    using (var client = new TcpClient("localhost", 5000))
    using (var stream = client.GetStream())
    using (var writer = new StreamWriter(stream))
    using (var reader = new StreamReader(stream))
    {
      // Send the request headers
      await writer.WriteAsync("GET / HTTP/1.1\r\n");
      await writer.WriteAsync("Host: localhost:5000\r\n");
      await writer.WriteAsync("\r\n");
      await writer.FlushAsync();

      // TODO: How many chunks should we send this in?
      // 1 chunk for the headers and a 2nd chunk for the body?

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

      // Wait for the response reading task to complete
      // TODO: We may need to move this await till after we send the request body (our response)
      await readResponseTask;
    }
  }

  public async Task SendResponse()
  {
    using (var client = new TcpClient("localhost", 5000))
    using (var stream = client.GetStream())
    using (var writer = new StreamWriter(stream))
    using (var reader = new StreamReader(stream))
    {
      // Send the request headers
      await writer.WriteAsync("POST / HTTP/1.1\r\n");
      await writer.WriteAsync("Host: localhost:5000\r\n");
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


  public async Task TestChunkedRequest()
  {
    using (var client = new TcpClient("localhost", 5001))
    using (var stream = client.GetStream())
    using (var writer = new StreamWriter(stream))
    using (var reader = new StreamReader(stream))
    {
      // Send the request headers
      await writer.WriteAsync("POST /api/chunked HTTP/1.1\r\n");
      await writer.WriteAsync("Host: localhost:5001\r\n");
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
}