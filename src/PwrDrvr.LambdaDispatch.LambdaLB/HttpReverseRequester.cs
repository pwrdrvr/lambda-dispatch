// #define USE_SOCKETS_HTTP_HANDLER
// #define USE_INSECURE_CIPHER_FOR_WIRESHARK

using System.Net;
using System.Net.Security;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PwrDrvr.LambdaDispatch.LambdaLB;

public class HttpReverseRequester
{
  private readonly ILogger<HttpReverseRequester> _logger;

  private readonly string _id;
  private readonly string _dispatcherUrl;

  private readonly Uri _uri;

  private readonly HttpClient _client;

#if USE_SOCKETS_HTTP_HANDLER
  private readonly SocketsHttpHandler? _handler;
#else
  private readonly HttpClientHandler? _handler;
#endif

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
    // Handler is owned by client now
    // _handler?.Dispose();

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

    using (var responseContentReaderForRequest = new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8, leaveOpen: true))
    {
      try
      {
        // Read the request line
        var firstLine = await responseContentReaderForRequest.ReadLineAsync();
        if (string.IsNullOrEmpty(firstLine))
        {
          // We need to let go of the request body
          throw new EndOfStreamException("End of stream reached while reading request line");
        }
        else if (firstLine == "GOAWAY")
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

        // Read the headers
        string? requestHeaderLine;
        var contentHeaders = new List<(string, string)>();
        while (!string.IsNullOrEmpty(requestHeaderLine = await responseContentReaderForRequest.ReadLineAsync()))
        {
          // Split the header into key and value
          var parts = requestHeaderLine.Split(new[] { ": " }, 2, StringSplitOptions.None);
          var key = parts[0];
          var value = parts[1];
          if (parts.Length != 2)
          {
            throw new Exception($"Invalid header line: {requestHeaderLine}");
          }

          if (string.Compare(key, "Content-Type", StringComparison.OrdinalIgnoreCase) == 0)
          {
            contentHeaders.Add((key, value));
            // The Host header is not allowed to be set by the client
            // DotNet will throw `System.InvalidOperationException` if you try to set it
          }
          else
          {
            receivedRequest.Headers.Add(key, value);
          }
        }

        // Set the request body
        // TODO: The StreamReader will have stolen and buffered some of the underlying stream data
        receivedRequest.Content = new StreamContent(responseContentReaderForRequest.BaseStream);

        // Add all the content headers
        foreach (var (key, value) in contentHeaders)
        {
          receivedRequest.Content.Headers.Add(key, value);
        }

        return ((int)response.StatusCode, receivedRequest, request, requestStreamForResponse, duplexContent);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error reading request from response");
        // Indicate that we don't need the response body anymore
        try { response.Content.Dispose(); } catch { }
        // Close the request body
        try { requestStreamForResponse.Close(); } catch { }
        try { duplexContent?.Complete(); } catch { }

        throw;
      }
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