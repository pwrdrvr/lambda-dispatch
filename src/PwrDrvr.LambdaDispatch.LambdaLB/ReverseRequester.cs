using System.Net.Sockets;
using System.Net;
using System.Net.Http.Headers;

namespace PwrDrvr.LambdaDispatch.LambdaLB;

public class ReverseRequester : IAsyncDisposable
{
  private readonly string _dispatcherUrl;

  private readonly string _id;

  private readonly TcpClient _client;

  private readonly NetworkStream _stream;

  /// <summary>
  /// This reads the "response" from the router, which is the request being sent to the Lambda
  /// </summary>
  private readonly StreamReader _reader;

  /// <summary>
  /// This writes the "request" to the router, which is the response from the Lambda
  /// </summary>
  private readonly StreamWriter _writer;

  private readonly Uri _uri;

  public ReverseRequester(string id, string dispatcherUrl)
  {
    _id = id;
    _dispatcherUrl = dispatcherUrl;

    // Parse out the host, port, and path
    _uri = new Uri(_dispatcherUrl);

    _client = new TcpClient(_uri.Host, _uri.Port);
    _stream = _client.GetStream();
    _reader = new StreamReader(_stream);
    _writer = new StreamWriter(_stream);
  }

  // Async Dispose
  public async ValueTask DisposeAsync()
  {
    await _writer.DisposeAsync();
    _reader.Dispose();
    await _stream.DisposeAsync();
    _client.Dispose();
  }

  /// <summary>
  /// Pickup a request from the router over the "response" channel
  /// 
  /// This requires starting a real HTTP request to the router,
  /// within which we will send the response as the request "body"
  /// and we will receive the request as the response "body"
  /// </summary>
  /// <returns></returns>
  public async Task<System.Net.HttpWebRequest> GetRequest()
  {
    // Initiate a chunked request to the router
    // This opens the channel for the response to come back
    // This will allow the router to write a response to us which will send us a "request"
    await _writer.WriteAsync($"POST {_uri.PathAndQuery} HTTP/1.1\r\n");
    await _writer.WriteAsync($"Host: {_uri.Host}:{_uri.Port}\r\n");
    await _writer.WriteAsync($"X-Lambda-Id: {_id}\r\n");
    await _writer.WriteAsync("Content-Type: application/octet-stream\r\n");
    await _writer.WriteAsync("Transfer-Encoding: chunked\r\n");
    await _writer.WriteAsync("\r\n");
    await _writer.FlushAsync();

    // 1 chunk for the headers and a 2nd chunk for the body?



    // Start a task to read the response headers and body
    var readResponseTask = Task.Run(async Task<string>? () =>
    {
      StringWriter requestWriter = new();

      // TODO: Read the chunk size and use it to read the response body
      // TODO: Make sure the chunk sizes are removed from the request

      Console.WriteLine("Starting reading response from router, containing request to Lambda");

      // Read the response headers
      string? line;
      while (!string.IsNullOrEmpty(line = await _reader.ReadLineAsync()))
      {
        requestWriter.WriteLine(line);
        Console.WriteLine(line);
      }

      Console.WriteLine("Finished reading response headers from router, containing request to Lambda");

      // Read the response body
      while ((line = await _reader.ReadLineAsync()) != null)
      {
        requestWriter.WriteLine(line);
        Console.WriteLine(line);
      }

      Console.WriteLine("Finished reading response");
      return requestWriter.ToString();
    });

    // Wait for the response reading task to complete
    // TODO: We may need to move this await till after we send the request body (our response)
    string requestString = await readResponseTask;

    Console.WriteLine($"Received request from router: {requestString}");

#pragma warning disable SYSLIB0014 // Type or member is obsolete
    return WebRequest.CreateHttp("http://localhost:5001/api/chunked");
#pragma warning restore SYSLIB0014 // Type or member is obsolete
  }

  public async Task SendResponse()
  {
    // These request headeers are HTTP within HTTP

    Console.WriteLine("Sending response to dispatcher over request channel");

    // Create a StringWriter to write the response body with chunk size prefix
    var writer = new StringWriter();
    writer.Write("Content-Type: text/plain\r\n");
    writer.Write("Transfer-Encoding: chunked\r\n");
    writer.Write("\r\n");

    // Write the response headers onto the request body
    var headerString = writer.ToString();
    var headerChunkSize = headerString.Length.ToString("X");
    await _writer.WriteAsync($"{headerChunkSize}\r\n");
    // Blank line before next chunk size
    await _writer.WriteAsync("\r\n");
    await _writer.FlushAsync();

    // // Send the request body in chunks
    // for (int i = 0; i < 10; i++)
    // {
    //   var chunk = $"Chunk {i}\r\n";
    //   var chunkSize = chunk.Length.ToString("X");

    //   Console.WriteLine($"Sending chunk {i} of size {chunkSize}");

    //   await _writer.WriteAsync($"{chunkSize}\r\n");
    //   await _writer.WriteAsync(chunk);
    //   await _writer.WriteAsync("\r\n");
    //   await _writer.FlushAsync();
    // }

    // // Send the last chunk
    // await _writer.WriteAsync("0\r\n\r\n");
    // await _writer.FlushAsync();
  }
}