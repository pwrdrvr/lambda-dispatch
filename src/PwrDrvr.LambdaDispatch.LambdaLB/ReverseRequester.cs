using System.Net.Sockets;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace PwrDrvr.LambdaDispatch.LambdaLB;

public class ReverseRequester : IAsyncDisposable
{
  private readonly string _dispatcherUrl;

  private readonly string _id;

  private readonly TcpClient _client;

  /// <summary>
  /// The read side of this stream is the response from the router, which
  /// will contain the request to the Lambda
  /// The write side of this stream is the request to the router, which
  /// will we use to send a response from the Lambda
  /// </summary>
  private readonly NetworkStream _stream;

  private readonly Uri _uri;

  public ReverseRequester(string id, string dispatcherUrl)
  {
    _id = id;
    _dispatcherUrl = dispatcherUrl;

    // Parse out the host, port, and path
    _uri = new Uri(_dispatcherUrl);

    _client = new TcpClient(_uri.Host, _uri.Port);
    _stream = _client.GetStream();
  }

  // Async Dispose
  public async ValueTask DisposeAsync()
  {
    await _stream.DisposeAsync();
    _client.Dispose();
  }

  private static int ReadChunkSize(Stream stream)
  {
    var chunkSizeBytes = new List<byte>();
    int b;
    while ((b = stream.ReadByte()) != -1)
    {
      // Stop reading when a newline character is encountered
      if (b == '\n')
      {
        break;
      }

      chunkSizeBytes.Add((byte)b);
    }

    var chunkSizeString = Encoding.UTF8.GetString(chunkSizeBytes.ToArray());
    return int.Parse(chunkSizeString, System.Globalization.NumberStyles.HexNumber);
  }

  private List<string> ReadHeaders(Stream stream)
  {
    var headerStream = new MemoryStream();
    int b;
    int lastByte = -1;
    while ((b = stream.ReadByte()) != -1)
    {
      headerStream.WriteByte((byte)b);

      // Check for \r\n on a line by itself (i.e., \r\n\r\n)
      if (lastByte == '\n' && b == '\r')
      {
        if ((b = stream.ReadByte()) != -1)
        {
          headerStream.WriteByte((byte)b);
          if (b == '\n')
          {
            break;
          }
        }
      }

      lastByte = b;
    }

    var headerBytes = headerStream.ToArray();
    var headerString = Encoding.UTF8.GetString(headerBytes);
    var headers = new List<string>(headerString.Split(new[] { "\r\n" }, StringSplitOptions.None));
    return headers;
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
    await _stream.WriteAsync(Encoding.UTF8.GetBytes($"POST {_uri.PathAndQuery} HTTP/1.1\r\n"));
    await _stream.WriteAsync(Encoding.UTF8.GetBytes($"Host: {_uri.Host}:{_uri.Port}\r\n"));
    await _stream.WriteAsync(Encoding.UTF8.GetBytes($"X-Lambda-Id: {_id}\r\n"));
    await _stream.WriteAsync(Encoding.UTF8.GetBytes("Content-Type: application/octet-stream\r\n"));
    await _stream.WriteAsync(Encoding.UTF8.GetBytes("Transfer-Encoding: chunked\r\n"));
    await _stream.WriteAsync(Encoding.UTF8.GetBytes("\r\n"));
    await _stream.FlushAsync();

    // Start a task to read the response headers and body
    var readResponseTask = Task.Run(async Task<string>? () =>
    {
      StringWriter requestWriter = new();

      // TODO: Read the chunk size and use it to read the response body
      // TODO: Make sure the chunk sizes are removed from the request

      Console.WriteLine("Starting reading response from router, containing request to Lambda");

      //
      // Read the response headers from the Router itself
      //

      // These are not part of the request that we're going to run
      var headerLines = ReadHeaders(_stream);
      for (int i = 0; i < headerLines.Count; i++)
      {
        var headerLine = headerLines[i];
        requestWriter.Write($"{headerLine}\r\n");
        Console.WriteLine($"Router header: {headerLine}");
      }
      // Write the blank line after headers
      requestWriter.Write("\r\n");

      Console.WriteLine("Finished reading response headers from router, containing request to Lambda");

      //
      // Read the actual request off the Response from the router
      //

      // The response body on the TCP Stream is send with the HTTP chunked protocol
      // We need to read the advertised chunk sizes and collected them in a StreamWriter
      // When we see a chunk size of 0 we know we have reached the end of the response body
      // even though the response will not be marked as closed yet

      // Create a MemoryStream to hold the response body
      using var responseBody = new MemoryStream();

      // Read the response body
      while (true)
      {
        // Read the chunk size from the stream
        int chunkSize = ReadChunkSize(_stream);

        Console.WriteLine($"Chunk size: {chunkSize}");

        // If the chunk size is 0, break the loop
        if (chunkSize == 0)
        {
          Console.WriteLine("Chunk size is 0, breaking loop");
          break;
        }

        // Read the specified number of bytes from the stream
        byte[] buffer = new byte[chunkSize];
        Console.WriteLine($"Reading {chunkSize} bytes from response body");
        await _stream.ReadAsync(buffer, 0, chunkSize);
        Console.WriteLine($"Read {chunkSize} bytes from response body");

        // Write the bytes to the MemoryStream
        await responseBody.WriteAsync(buffer, 0, chunkSize);

        // Read the newline that separates chunks
        await _stream.ReadAsync(buffer, 0, 2);
      }

      // Reset the position of the MemoryStream to the beginning
      responseBody.Seek(0, SeekOrigin.Begin);

      Console.WriteLine("Finished reading request from response body");

      Console.WriteLine("Starting parsing request headers from response body");
      using (var reader = new StreamReader(responseBody, Encoding.UTF8, leaveOpen: true))
      {
        // Read the headers
        string? requestHeaderLine;
        while (!string.IsNullOrEmpty(requestHeaderLine = await reader.ReadLineAsync()))
        {
          requestWriter.Write($"{requestHeaderLine}\r\n");
          Console.WriteLine($"Request header: {requestHeaderLine}");
        }

        Console.WriteLine("Finished parsing request headers from response body");

        // Dump the request body
        string remainingData = await reader.ReadToEndAsync();
        Console.WriteLine("Remaining data: " + remainingData);

        await requestWriter.WriteAsync(remainingData);
      }

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
}