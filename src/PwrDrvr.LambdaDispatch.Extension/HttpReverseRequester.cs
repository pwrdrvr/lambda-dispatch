using System.Buffers;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PwrDrvr.LambdaDispatch.Extension;

public class CustomStream : Stream
{
  private MemoryStream _bufferStream;
  private Stream _responseStream;
  private bool _bufferStreamRead = false;

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
    if (_bufferStreamRead)
    {
      return _responseStream.Read(buffer, offset, count);
    }

    int bytesRead = _bufferStream.Read(buffer, offset, count);
    if (bytesRead == 0)
    {
      _bufferStreamRead = true;
      return _responseStream.Read(buffer, offset, count);
    }
    return bytesRead;
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
  {
    if (_bufferStreamRead)
    {
      return _responseStream.ReadAsync(buffer, offset, count, cancellationToken);
    }

    int bytesRead = _bufferStream.Read(buffer, offset, count);
    if (bytesRead == 0)
    {
      _bufferStreamRead = true;
      return _responseStream.ReadAsync(buffer, offset, count, cancellationToken);
    }
    return Task.FromResult(bytesRead);
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

  public HttpReverseRequester(
    string id,
    string dispatcherUrl,
    HttpClient httpClient,
    ILogger<HttpReverseRequester>? logger = null)
  {
    _logger = logger ?? LoggerInstance.CreateLogger<HttpReverseRequester>();
    _id = id;
    _dispatcherUrl = dispatcherUrl;
    _uri = new UriBuilder(_dispatcherUrl)
    {
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
    }.Uri;

    var request = new HttpRequestMessage(HttpMethod.Post, uri)
    {
      Version = new Version(2, 0),
      VersionPolicy = HttpVersionPolicy.RequestVersionExact,
    };
    request.Headers.Host = "lambdadispatch.local:5004";
    request.Headers.Add("X-Lambda-Id", _id);
    request.Headers.Add("X-Channel-Id", channelId);
    request.Headers.Add("Date", DateTime.UtcNow.ToString("R"));
    request.Content = duplexContent;

    //
    // Send the request to the router
    // Read the Response before sending the Request
    // TODO: We can await the pair of the Response or Request closing to avoid deadlocks
    //
    var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

    // Get the stream that we can write the response to
    Stream requestStreamForResponse = await duplexContent.WaitForStreamAsync().ConfigureAwait(false);
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
    // TODO: Get the 32 KB header size limit from configuration
    var headerBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
    try
    {
      var requestStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

      // Read up to max headers size of data
      // Read until we fill the bufer OR we get an EOF
      int totalBytesRead = 0;
      int idxToExamine = 0;
      int idxPriorLineFeed = int.MinValue;
      int idxHeadersLast = int.MinValue;
      while (true)
      {
        if (totalBytesRead >= headerBuffer.Length)
        {
          // Buffer is full
          break;
        }

        var bytesRead = await requestStream.ReadAsync(headerBuffer, totalBytesRead, headerBuffer.Length - totalBytesRead).ConfigureAwait(false);
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

        if (idxHeadersLast != int.MinValue)
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
      int endOfStatusLine = Array.IndexOf(headerBuffer, (byte)'\n', 0, totalBytesRead);
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
        //
        // TODO: Remove this after 2024-01-09 (use new way below with reserved path)
        //
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

      if (partsOfFirstLine[1] == "/_lambda_dispatch/goaway")
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

      // Start processing the rest of the headers from the character after '\n'
      int startOfNextLine = endOfStatusLine + 1;
      var contentHeaders = new List<(string, string)>();

      // Process the rest of the headers
      var hasBody = false;
      while (startOfNextLine < totalBytesRead)
      {
        // Find the index of the next '\n' in headerBuffer
        int endOfLine = Array.IndexOf(headerBuffer, (byte)'\n', startOfNextLine, totalBytesRead - startOfNextLine);
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
          hasBody = true;
          // Don't set the Transfer-Encoding header as it breaks the response
          continue;
        }
        else if (string.Compare(key, "Content-Type", StringComparison.OrdinalIgnoreCase) == 0)
        {
          contentHeaders.Add((key, value));
        }
        else if (string.Compare(key, "Content-Length", StringComparison.OrdinalIgnoreCase) == 0)
        {
          hasBody = true;
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
        accumulatedBuffer.Write(headerBuffer.AsSpan(startOfNextLine, totalBytesRead - startOfNextLine));
        accumulatedBuffer.Flush();
        accumulatedBuffer.Position = 0;
      }

      // Set the request body, if there is one
      if (hasBody)
      {
        Stream customStream = new CustomStream(accumulatedBuffer, requestStream);
        receivedRequest.Content = new StreamContent(customStream);

        // Add all the content headers
        foreach (var (key, value) in contentHeaders)
        {
          receivedRequest.Content.Headers.Add(key, value);
        }
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
    // TODO: Get the 32 KB header size limit from configuration
    var headerBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
    try
    {
      int offset = 0;
      // TODO: Which HTTP version should be using here?  It seems this should be 1.1 always
      var statusLine = $"HTTP/1.1 {(int)response.StatusCode} {response.ReasonPhrase}\r\n";
      var statusLineBytes = Encoding.UTF8.GetBytes(statusLine);
      statusLineBytes.CopyTo(headerBuffer, offset);
      offset += statusLineBytes.Length;
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

        var headerLine = $"{header.Key}: {string.Join(',', header.Value)}\r\n";
        var headerLineBytes = Encoding.UTF8.GetBytes(headerLine);
        headerLineBytes.CopyTo(headerBuffer, offset);
        offset += headerLineBytes.Length;
      }

      // Copy content headers
      foreach (var header in response.Content?.Headers)
      {
        var headerLine = $"{header.Key}: {string.Join(',', header.Value)}\r\n";
        var headerLineBytes = Encoding.UTF8.GetBytes(headerLine);
        headerLineBytes.CopyTo(headerBuffer, offset);
        offset += headerLineBytes.Length;
      }

      // Add the control headers
      var controlHeaders = new Dictionary<string, string>
      {
        { "X-Lambda-Id", _id },
        { "X-Channel-Id", channelId },
        { "Server", "PwrDrvr.LambdaDispatch.Extension" },
      };
      foreach (var (key, value) in controlHeaders)
      {
        var headerLine = $"{key}: {value}\r\n";
        var headerLineBytes = Encoding.UTF8.GetBytes(headerLine);
        headerLineBytes.CopyTo(headerBuffer, offset);
        offset += headerLineBytes.Length;
      }
      // Write the end of the headers
      var endOfHeaders = "\r\n";
      var endOfHeadersBytes = Encoding.UTF8.GetBytes(endOfHeaders);
      endOfHeadersBytes.CopyTo(headerBuffer, offset);
      offset += endOfHeadersBytes.Length;

      // Write the headers to the stream
      await requestStreamForResponse.WriteAsync(headerBuffer.AsMemory(0, offset)).ConfigureAwait(false);

      // Copy the body from the request to the response
      // NOTE: CopyToAsync will only start sending when EOF is read on the response stream
#if false
      await response.Content.CopyToAsync(requestStreamForResponse).ConfigureAwait(false);
#else
      var bytes = ArrayPool<byte>.Shared.Rent(128 * 1024);
      try
      {
        // Read from the source stream and write to the destination stream in a loop
        int bytesRead;
        var responseStream = response.Content.ReadAsStream();
        while ((bytesRead = await responseStream.ReadAsync(bytes, 0, bytes.Length)) > 0)
        {
          await requestStreamForResponse.WriteAsync(bytes, 0, bytesRead);
          await requestStreamForResponse.FlushAsync();
        }
      }
      finally
      {
        ArrayPool<byte>.Shared.Return(bytes);
      }
#endif
      await requestStreamForResponse.FlushAsync().ConfigureAwait(false);
      requestStreamForResponse.Close();
      duplexContent.Complete();
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(headerBuffer);
    }
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
      }.Uri;
      var request = new HttpRequestMessage(HttpMethod.Get, uri)
      {
        Version = new Version(2, 0),
        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
      };
      request.Headers.Host = $"lambdadispatch.local:{_uri.Port}";
      request.Headers.Add("X-Lambda-Id", _id);

      using var response = await _client.SendAsync(request).ConfigureAwait(false);

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
    try
    {
      _logger.LogDebug("Starting ping of instance: {id}", _id);
      var uri = new UriBuilder(_uri)
      {
        Path = $"{_uri.AbsolutePath}/ping/{_id}",
      }.Uri;
      var request = new HttpRequestMessage(HttpMethod.Get, uri)
      {
        Version = new Version(2, 0),
        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
      };
      request.Headers.Host = $"lambdadispatch.local:{_uri.Port}";
      request.Headers.Add("X-Lambda-Id", _id);

      using var response = await _client.SendAsync(request).ConfigureAwait(false);

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
    catch (Exception ex)
    {
      _logger.LogError("Error pinging instance: {id}, {message}", _id, ex.Message);
      return false;
    }
  }
}