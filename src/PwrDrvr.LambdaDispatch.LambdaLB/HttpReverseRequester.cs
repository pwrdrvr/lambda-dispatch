using System.Buffers;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PwrDrvr.LambdaDispatch.LambdaLB;

public class CustomStream : Stream
{
  private MemoryStream _bufferStream;
  private Stream _responseStream;

  public CustomStream(MemoryStream bufferStream, Stream responseStream)
  {
    _bufferStream = bufferStream;
    _responseStream = responseStream;
  }

  public override bool CanRead => _bufferStream.CanRead || _responseStream.CanRead;

  public override bool CanSeek => false;

  public override bool CanWrite => false;

  public override long Length => throw new NotSupportedException();

  public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

  public override void Flush()
  {
    throw new IOException();
  }

  public override int Read(byte[] buffer, int offset, int count)
  {
    throw new NotImplementedException();
  }

  public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
  {
    int bytesRead = await _bufferStream.ReadAsync(buffer, offset, count, cancellationToken);
    if (bytesRead == 0)
    {
      bytesRead = await _responseStream.ReadAsync(buffer, offset, count, cancellationToken);
    }
    return bytesRead;
  }

  public override long Seek(long offset, SeekOrigin origin)
  {
    throw new NotSupportedException();
  }

  public override void SetLength(long value)
  {
    throw new NotSupportedException();
  }

  public override void Write(byte[] buffer, int offset, int count)
  {
    throw new NotSupportedException();
  }
}

public class HttpReverseRequester
{
  private readonly ILogger<HttpReverseRequester> _logger;

  private readonly string _id;
  private readonly string _dispatcherUrl;

  private readonly Uri _uri;

  private readonly HttpClient _client;

  public HttpReverseRequester(string id, string dispatcherUrl, HttpClient httpClient, ILogger<HttpReverseRequester> logger = null)
  {
    _logger = logger ?? LoggerInstance.CreateLogger<HttpReverseRequester>();
    _id = id;
    _dispatcherUrl = dispatcherUrl;

    // Change Proto and Port
    _uri = new UriBuilder(_dispatcherUrl)
    {
      Port = 5003,
      Scheme = "https",
    }.Uri;

    _client = httpClient;
  }

  public ValueTask DisposeAsync()
  {
    _client.Dispose();

    return ValueTask.CompletedTask;
  }

  /// <summary>
  /// Get the request from the response of the Router
  /// </summary>
  /// <returns>
  /// outer status code, requestToRun, requestForResponse
  /// </returns>
  public async Task<(int, HttpRequestMessage, HttpRequestMessage, Stream, HttpDuplexContent)> GetRequest(string channelId)
  {
    var duplexContent = new HttpDuplexContent();

    var uri = new UriBuilder(_uri)
    {
      Path = $"{_uri.AbsolutePath}/request/{_id}/{channelId}",
      Port = 5003,
      Scheme = "https",
    }.Uri;

    var request = new HttpRequestMessage(HttpMethod.Post, uri)
    {
      Version = new Version(2, 0)
    };
    request.Headers.Host = "lambdadispatch.local:5003";
    request.Headers.Add("X-Lambda-Id", _id);
    request.Headers.Add("X-Channel-Id", channelId);
    request.Headers.Add("Date", DateTime.UtcNow.ToString("R"));
    request.Content = duplexContent;

    //
    // Send the request to the router
    // Read the Response before sending the Request
    // TODO: We can await the pair of the Response or Request closing to avoid deadlocks
    //
    var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

    // Get the stream that we can write the response to
    Stream requestStreamForResponse = await duplexContent.WaitForStreamAsync();

    // HH: This is from the dotnet example, but they do not await the SendAsync that
    // returns when the response headers are read. I don't think we need this
    // since we await it.
    // Flush the content stream. Otherwise, the request headers are not guaranteed to be sent.
    // await requestStreamForResponse.FlushAsync();
    if (response.StatusCode != HttpStatusCode.OK)
    {
      _logger.LogWarning("CLOSING - Got a {status} on the outer request LambdaId: {id}, ChannelId: {channelId}", response.StatusCode, _id, channelId);
      // Discard the response first since that's normally what we do
      // Gotta clean up the connection
      try { await response.Content.CopyToAsync(Stream.Null); } catch { }
      requestStreamForResponse.Close();
      // This is going to let the request be closed
      duplexContent?.Complete();

      _logger.LogWarning("CLOSED - Got a {status} on the outer request LambdaId: {id}, ChannelId: {channelId}", response.StatusCode, _id, channelId);
      return ((int)response.StatusCode, null!, null!, null!, null!);
    }

    //
    // Read the actual request off the Response from the router
    //
    var receivedRequest = new HttpRequestMessage();

    //
    // TODO: We need to read the request headers into a buffer
    //
    var headerBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
    try
    {
      var requestStream = await response.Content.ReadAsStreamAsync();

      // Read up to max headers size of data
      // Read until we fill the bufer OR we get an EOF
      int totalBytesRead = 0;
      int idxToExamine = 0;
      int idxPriorLineFeed = -1;
      int idxHeadersLast = -1;
      while (true)
      {
        if (totalBytesRead >= headerBuffer.Length)
        {
          // Buffer is full
          break;
        }

        var bytesRead = await requestStream.ReadAsync(headerBuffer, totalBytesRead, headerBuffer.Length - totalBytesRead);
        if (bytesRead == 0)
        {
          // Done reading
          break;
        }

        totalBytesRead += bytesRead;

        // Check if we have a `\r\n\r\n` sequence
        // We have to check for this in the buffer because we can't
        // read past the end of the stream
        for (int i = idxToExamine; i < totalBytesRead; i++)
        {
          // If this is a `\n` and the -1 or -2 character is `\n` then we we are done
          if (headerBuffer[i] == (byte)'\n' && (idxPriorLineFeed == i - 1 || (idxPriorLineFeed == i - 2 && headerBuffer[i - 1] == (byte)'\r')))
          {
            // We found the `\r\n\r\n` sequence
            // We are done reading
            idxHeadersLast = i;
            break;
          }
          else if (headerBuffer[i] == (byte)'\n')
          {
            // Update the last line feed index
            idxPriorLineFeed = i;
          }
        }

        if (idxHeadersLast != -1)
        {
          // We found the `\r\n\r\n` sequence
          // We are done reading
          break;
        }
      }

      //
      // NOTE: This starts reading the buffer again at the start
      // This could be combined with the end of headers check above to read only once
      //

      // Read the status line
      int endOfStatusLine = Array.IndexOf(headerBuffer, (byte)'\n');
      if (endOfStatusLine == -1)
      {
        // Handle error: '\n' not found in the buffer
        throw new Exception("Status line not found in response");
      }

      string firstLine = Encoding.UTF8.GetString(headerBuffer, 0, endOfStatusLine);

      if (string.IsNullOrEmpty(firstLine))
      {
        // We need to let go of the request body
        throw new EndOfStreamException("End of stream reached while reading request line");
      }
      else if (firstLine.StartsWith("GOAWAY"))
      {
        _logger.LogDebug("CLOSING - Got a GOAWAY instead of a request line on LambdaId: {id}, ChannelId: {channelId}", _id, channelId);
        // Clean up
        // Indicate that we don't need the response body anymore
        try { response.Content.Dispose(); } catch { }
        // Close the request body
        try { requestStreamForResponse.Close(); } catch { }
        try { duplexContent?.Complete(); } catch { }
        return ((int)HttpStatusCode.Conflict, null!, null!, null!, null!);
      }

      var partsOfFirstLine = firstLine.Split(' ');
      if (partsOfFirstLine.Length != 3)
      {
        throw new Exception($"Invalid request line: {firstLine}");
      }
      receivedRequest.Method = new HttpMethod(partsOfFirstLine[0]);
      receivedRequest.RequestUri = new Uri(partsOfFirstLine[1], UriKind.Relative);
      receivedRequest.Version = new Version(partsOfFirstLine[2].Split('/')[1]);

      // Start processing the rest of the headers from the character after '\n'
      int startOfNextLine = endOfStatusLine + 1;
      var contentHeaders = new List<(string, string)>();

      // Process the rest of the headers
      while (startOfNextLine < totalBytesRead)
      {
        // Find the index of the next '\n' in headerBuffer
        int endOfLine = Array.IndexOf(headerBuffer, (byte)'\n', startOfNextLine);
        if (endOfLine == -1)
        {
          // No more '\n' found
          break;
        }

        // Check if this is the end of the headers
        if (endOfLine == startOfNextLine || (endOfLine == startOfNextLine + 1 && headerBuffer[startOfNextLine] == '\r'))
        {
          // End of headers
          // Move the start to the character after '\n'
          startOfNextLine = endOfLine + 1;
          break;
        }

        // We don't want the \n or the possibly proceeding \r
        var endOfHeaderIdx = endOfLine;
        if (headerBuffer[endOfHeaderIdx - 1] == '\r')
        {
          endOfHeaderIdx--;
        }

        // Extract the line
        string headerLine = Encoding.UTF8.GetString(headerBuffer, startOfNextLine, endOfHeaderIdx - startOfNextLine);

        // Parse the line as a header
        var parts = headerLine.Split(new[] { ": " }, 2, StringSplitOptions.None);

        var key = parts[0];
        // Join all the parts after the first one
        var value = string.Join(", ", parts.Skip(1));
        if (string.Compare(key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) == 0)
        {
          // Don't set the Transfer-Encoding header as it breaks the response
          continue;
        }
        else if (string.Compare(key, "Content-Type", StringComparison.OrdinalIgnoreCase) == 0)
        {
          contentHeaders.Add((key, value));
        }
        else if (string.Compare(key, "Content-Length", StringComparison.OrdinalIgnoreCase) == 0)
        {
          contentHeaders.Add((key, value));
        }
        else
        {
          receivedRequest.Headers.Add(key, value);
        }

        // Move the start to the character after '\n'
        startOfNextLine = endOfLine + 1;
      }

      // Flush any remaining bytes in the buffer
      MemoryStream accumulatedBuffer = new MemoryStream();
      if (startOfNextLine < totalBytesRead)
      {
        // Write the bytes after the headers to the memory stream
        await accumulatedBuffer.WriteAsync(headerBuffer.AsMemory(startOfNextLine, totalBytesRead - startOfNextLine));
        await accumulatedBuffer.FlushAsync();
        accumulatedBuffer.Position = 0;
      }

      // Set the request body
      Stream customStream = new CustomStream(accumulatedBuffer, requestStream);
      receivedRequest.Content = new StreamContent(customStream);

      // Add all the content headers
      foreach (var (key, value) in contentHeaders)
      {
        receivedRequest.Content.Headers.Add(key, value);
      }

      return ((int)response.StatusCode, receivedRequest, request, requestStreamForResponse, duplexContent);
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(headerBuffer);
    }
  }

  /// <summary>
  /// Send the response on the request to the Router
  /// </summary>
  /// <returns></returns>
  public async Task SendResponse(HttpResponseMessage response, HttpRequestMessage requestForResponse, Stream requestStreamForResponse, HttpDuplexContent duplexContent, string channelId)
  {
    // Write the status line
    await requestStreamForResponse.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/{requestForResponse.Version} {(int)response.StatusCode} {response.ReasonPhrase}\r\n"));
    // Copy the headers
    foreach (var header in response.Headers)
    {
      if (string.Compare(header.Key, "Keep-Alive", true) == 0)
      {
        // Don't send the Keep-Alive header
        continue;
      }
      if (string.Compare(header.Key, "Connection", true) == 0)
      {
        // Don't send the Connection header
        continue;
      }
      await requestStreamForResponse.WriteAsync(Encoding.UTF8.GetBytes($"{header.Key}: {string.Join(',', header.Value)}\r\n"));
    }
    await requestStreamForResponse.WriteAsync(Encoding.UTF8.GetBytes("X-Lambda-Id: " + _id + "\r\n"));
    await requestStreamForResponse.WriteAsync(Encoding.UTF8.GetBytes("X-Channel-Id: " + channelId + "\r\n"));
    await requestStreamForResponse.WriteAsync(Encoding.UTF8.GetBytes("Server: PwrDrvr.LambdaDispatch.LambdaLB\r\n"));
    await requestStreamForResponse.WriteAsync(Encoding.UTF8.GetBytes("\r\n"));

    // Copy the body from the request to the response
    await response.Content.CopyToAsync(requestStreamForResponse);
    // await requestStreamForResponse.WriteAsync(Encoding.UTF8.GetBytes("Hello World!\r\n"));
    await requestStreamForResponse.FlushAsync();
    requestStreamForResponse.Close();
    duplexContent.Complete();
  }

  private int _closeStarted = 0;

  public async Task CloseInstance()
  {
    try
    {
      if (Interlocked.CompareExchange(ref _closeStarted, 1, 0) == 1)
      {
        _logger.LogInformation("Close already started for instance: {id}", _id);
        return;
      }

      _logger.LogInformation("Starting close of instance: {id}", _id);
      var uri = new UriBuilder(_dispatcherUrl)
      {
        Path = $"{_uri.AbsolutePath}/close/{_id}",
        Port = 5003,
        Scheme = "https",
      }.Uri;
      var request = new HttpRequestMessage(HttpMethod.Get, uri)
      {
        Version = new Version(2, 0)
      };
      request.Headers.Host = "lambdadispatch.local:5003";
      request.Headers.Add("X-Lambda-Id", _id);

      using var response = await _client.SendAsync(request);

      if (response.StatusCode != HttpStatusCode.OK)
      {
        _logger.LogError("Error closing instance: {id}, {statusCode}", _id, response.StatusCode);
      }
      else
      {
        _logger.LogInformation("Closed instance: {id}, {statusCode}", _id, response.StatusCode);
        try { await response.Content.CopyToAsync(Stream.Null); } catch { }
      }
    }
    catch
    {
      _logger.LogError("Error closing instance: {id}", _id);
    }
  }

  public async Task<bool> Ping()
  {
    _logger.LogDebug("Starting ping of instance: {id}", _id);
    var uri = new UriBuilder(_uri)
    {
      Path = $"{_uri.AbsolutePath}/ping/{_id}",
      Port = 5003,
      Scheme = "https",
    }.Uri;
    var request = new HttpRequestMessage(HttpMethod.Get, uri)
    {
      Version = new Version(2, 0)
    };
    request.Headers.Host = "lambdadispatch.local:5003";
    request.Headers.Add("X-Lambda-Id", _id);

    using var response = await _client.SendAsync(request);

    if (response.StatusCode != HttpStatusCode.OK)
    {
      _logger.LogError("Error pinging instance: {id}, {statusCode}", _id, response.StatusCode);
      try { await response.Content.CopyToAsync(Stream.Null); } catch { }
      return false;
    }
    else
    {
      _logger.LogDebug("Pinged instance: {id}, {statusCode}", _id, response.StatusCode);
      try { await response.Content.CopyToAsync(Stream.Null); } catch { }
      return true;
    }
  }
}