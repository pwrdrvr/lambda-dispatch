using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Extensions;
using System.Diagnostics;

namespace PwrDrvr.LambdaDispatch.Router;

public enum LambdaConnectionState
{
  /// <summary>
  /// The connection is open and ready to accept requests
  /// </summary>
  Open,

  /// <summary>
  /// Request has been sent and we are waiting for a response
  /// </summary>
  Busy,

  /// <summary>
  /// The connection is closed and should not be used
  /// </summary>
  Closed
}

public interface ILambdaConnection
{

  /// <summary>
  /// The state of the connection
  /// </summary>
  public LambdaConnectionState State { get; }

  /// <summary>
  /// The Request from the Lambda (which we will send the response on)
  /// </summary>
  public HttpRequest Request { get; }

  /// <summary>
  /// The Response from the Lambda (which we will send the request on)
  /// </summary>
  public HttpResponse Response { get; }

  /// <summary>
  /// Handle back to the Lambda Instance that owns this Connection
  /// </summary>
  public ILambdaInstance Instance { get; }

  /// <summary>
  /// The channel id for this connection
  /// </summary>
  public string ChannelId { get; }

  /// <summary>
  /// Indicates whether this was the first connection for an instance, causing the instance to be marked `Open`
  /// </summary>
  public bool FirstConnectionForInstance { get; }

  /// <summary>
  /// Task that completes when the connection is closed
  /// </summary>
  public TaskCompletionSource TCS { get; }

  public Task Discard();

  public Task RunRequest(HttpRequest incomingRequest, HttpResponse incomingResponse);

}

public class LambdaConnection : ILambdaConnection
{
  private readonly static string _gitHash = Environment.GetEnvironmentVariable("GIT_HASH") ?? "unknown";
  private readonly static string _buildTime = Environment.GetEnvironmentVariable("BUILD_TIME") ?? "unknown";

  private readonly ILogger<LambdaConnection> _logger = LoggerInstance.CreateLogger<LambdaConnection>();

  private volatile int _runRequestCalled = 0;

  public LambdaConnectionState State { get; private set; }

  public HttpRequest Request { get; private set; }

  public HttpResponse Response { get; private set; }

  public ILambdaInstance Instance { get; private set; }

  public string ChannelId { get; private set; }

  public bool FirstConnectionForInstance { get; private set; }

  public TaskCompletionSource TCS { get; private set; } = new TaskCompletionSource();

  private CancellationTokenSource CTS = new CancellationTokenSource();

  public LambdaConnection(HttpRequest request, HttpResponse response, ILambdaInstance instance, string channelId, bool firstConnectionForInstance)
  {
    Request = request;
    Response = response;
    Instance = instance;
    ChannelId = channelId;
    FirstConnectionForInstance = firstConnectionForInstance;

    // Set the state to open
    State = LambdaConnectionState.Open;

    // Handle an abnormal connection termination
    Request.HttpContext.RequestAborted.Register(() =>
    {
      _logger.LogWarning("LambdaId: {}, ChannelId: {} - LambdaConnection - Incoming request aborted", Instance.Id, ChannelId);

      Instance.ConnectionClosed(State == LambdaConnectionState.Busy);

      // Set the state to closed
      State = LambdaConnectionState.Closed;
    });

    // Register a callback for when the connection closes
    Response.OnCompleted(() =>
    {
      Instance.ConnectionClosed(State == LambdaConnectionState.Busy);

      // Set the state to closed
      State = LambdaConnectionState.Closed;

      return Task.CompletedTask;
    });
  }

  /// <summary>
  /// Discard the connection by writing a well known request path for a goaway to the Lambda
  /// </summary>
  /// <returns></returns>
  public async Task Discard()
  {
    try
    {
      if (State == LambdaConnectionState.Closed)
      {
        return;
      }

      State = LambdaConnectionState.Closed;

      // Close the connection
      // Do not set the status code because it's already been sent
      // as 200 if this connection was in the queue
      // There will either be no subsequent connection or it will
      // get immediately rejected with a 409
      try
      {
        await Response.WriteAsync($"GET /_lambda_dispatch/goaway HTTP/1.1\r\nX-Lambda-Id: {Instance.Id}\r\nX-Channel-Id: {ChannelId}\r\n\r\n", CTS.Token);
        await Response.CompleteAsync();
        try { await Request.Body.CopyToAsync(Stream.Null); }
        catch { try { Request.HttpContext.Abort(); } catch { } }
      }
      catch { try { Request.HttpContext.Abort(); } catch { } }
    }
    finally
    {
      TCS.SetResult();
    }
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private async Task ProxyRequestToLambda(HttpRequest incomingRequest)
  {
    // Send the incoming Request on the lambda's Response
    _logger.LogDebug("LambdaId: {}, ChannelId: {} - Sending incoming request headers to Lambda", Instance.Id, ChannelId);

    // TODO: Get the 32 KB header size limit from configuration
    var headerBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
    try
    {
      int offset = 0;

      // Write the request line to the buffer
      // Even though these requests are sent over HTTP2, we send them as "HTTP/1.1"
      // in the encoding we use on top of the HTTP2 body
      var requestLine = $"{incomingRequest.Method} {incomingRequest.GetEncodedPathAndQuery()} HTTP/1.1\r\n";
      var requestLineBytes = Encoding.UTF8.GetBytes(requestLine);
      requestLineBytes.CopyTo(headerBuffer, offset);
      offset += requestLineBytes.Length;

      // Send the headers to the Lambda
      foreach (var header in incomingRequest.Headers)
      {
        // Write the header to the buffer
        var headerLine = $"{header.Key}: {header.Value}\r\n";
        var headerLineBytes = Encoding.UTF8.GetBytes(headerLine);
        headerLineBytes.CopyTo(headerBuffer, offset);
        offset += headerLineBytes.Length;
      }

      // Send the end of the headers
      headerBuffer[offset + 1] = (byte)'\n';
      headerBuffer[offset] = (byte)'\r';
      offset += 2;

      // Send the headers to the Lambda
      await Response.BodyWriter.WriteAsync(headerBuffer.AsMemory(0, offset), CTS.Token).ConfigureAwait(false);

      // Only copy the request body if the request has a body
      // or it's HTTP2, in which case we don't know until we start reading
      if (incomingRequest.Protocol == HttpProtocol.Http2
          || incomingRequest.ContentLength > 0
          || incomingRequest.Headers.TransferEncoding == "chunked")
      {
        _logger.LogDebug("LambdaId: {}, ChannelId: {} - Sending incoming request body to Lambda", Instance.Id, ChannelId);

        // Send the body to the Lambda
        var bytes = ArrayPool<byte>.Shared.Rent(128 * 1024);
        int totalBytesRead = 0;
        int totalBytesWritten = 0;
        var loopTimer = Stopwatch.StartNew();
        var lastReadTimer = Stopwatch.StartNew();
        try
        {
          // Read from the source stream and write to the destination stream in a loop
          var responseStream = incomingRequest.Body;
          int bytesRead;
          while ((bytesRead = await responseStream.ReadAsync(bytes, CTS.Token)) > 0)
          {
            totalBytesRead += bytesRead;
            await Response.BodyWriter.WriteAsync(bytes.AsMemory(0, bytesRead), CTS.Token);
            totalBytesWritten += bytesRead;
            lastReadTimer.Restart();
          }
        }
        catch (Exception ex)
        {
          _logger.LogError(ex,
            "LambdaConnection.ProxyRequestToLambda - LambdaId: {}, ChannelId: {} - Exception reading request body from incoming request - Request Line: {}, Bytes Read: {}, Bytes Written: {}, Total Time: {:.0f}ms, Time Since Last Read: {:.0f}ms",
            Instance.Id,
            ChannelId,
            requestLine,
            totalBytesRead,
            totalBytesWritten,
            loopTimer.ElapsedMilliseconds,
            lastReadTimer.ElapsedMilliseconds);
          throw;
        }
        finally
        {
          ArrayPool<byte>.Shared.Return(bytes);
        }
        await incomingRequest.BodyReader.CompleteAsync().ConfigureAwait(false);

        _logger.LogDebug("Finished sending incoming request body to Lambda");
      }

      // Mark that the Request has been sent on the LambdaInstances
      await Response.BodyWriter.CompleteAsync().ConfigureAwait(false);
      await Response.CompleteAsync().ConfigureAwait(false);

      // Get the response from the lambda request and relay it back to the caller
      _logger.LogDebug("Finished sending entire request to Lambda");
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(headerBuffer);
    }
  }

  /// <summary>
  /// Run the request on the Lambda
  /// </summary>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public async Task RunRequest(HttpRequest incomingRequest, HttpResponse incomingResponse)
  {
    try
    {
      // Check if the connection has already been used
      if (Interlocked.CompareExchange(ref _runRequestCalled, 1, 0) == 1)
      {
        throw new InvalidOperationException("RunRequest can only be called once");
      }

      // Check if state is wrong
      if (State != LambdaConnectionState.Open)
      {
        throw new InvalidOperationException("Connection is not open");
      }

      // Set the state to busy
      State = LambdaConnectionState.Busy;

      var proxyRequestTask = ProxyRequestToLambda(incomingRequest);
      var proxyResponseTask = RelayResponseFromLambda(incomingRequest, incomingResponse);

      // Wait for both to finish
      // This allows us to continue sending request body while receiving
      // and relaying the response body (duplex)
      var completedTask = await Task.WhenAny(proxyRequestTask, proxyResponseTask).ConfigureAwait(false);
      if (completedTask.Exception != null)
      {
        throw completedTask.Exception;
      }

      if (completedTask == proxyRequestTask)
      {
        // ProxyRequestToLambda finished first
        // Wait for RelayResponseFromLambda to finish
        await proxyResponseTask.ConfigureAwait(false);
      }
      else
      {
        // RelayResponseFromLambda finished first
        // Wait for ProxyRequestToLambda to finish
        await proxyRequestTask.ConfigureAwait(false);
      }
    }
    catch (AggregateException ae)
    {
      foreach (var ex in ae.InnerExceptions)
      {
        bool logExceptionStack;

        // This happens when the client aborts the request and we are still reading
        if (ex is BadHttpRequestException || ex is ConnectionResetException)
        {
          logExceptionStack = false;
        }
        else
        {
          logExceptionStack = true;
        }

        if (Request.Headers.TryGetValue("Date", out var dateValues)
                  && dateValues.Count == 1
                  && DateTime.TryParse(dateValues.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var requestDate))
        {
          var duration = DateTime.UtcNow - requestDate;

          if (logExceptionStack)
          {
            _logger.LogError(ex, "LambdaId: {}, ChannelId: {} - LambdaConnection.RunRequest - Exception - Request was received at {RequestDate}, {DurationInSeconds} seconds ago",
                Instance.Id,
                ChannelId,
                requestDate.ToLocalTime().ToString("o"),
                duration.TotalSeconds
                );
          }
          else
          {
            _logger.LogError("LambdaId: {}, ChannelId: {} - LambdaConnection.RunRequest - Exception - Request was received at {RequestDate}, {DurationInSeconds} seconds ago",
                Instance.Id,
                ChannelId,
                requestDate.ToLocalTime().ToString("o"),
                duration.TotalSeconds
                );
          }
        }
        else
        {
          if (logExceptionStack)
          {
            _logger.LogError(ex, "LambdaId: {}, ChannelId: {} - LambdaConnection.RunRequest - Exception - Receipt time not known",
                Instance.Id,
                ChannelId);
          }
          else
          {
            _logger.LogError("LambdaId: {}, ChannelId: {} - LambdaConnection.RunRequest - Exception - Receipt time not known",
                Instance.Id,
                ChannelId);
          }
        }

        throw;
      }
    }
    catch
    {
      // We have to abort the connection for HTTP/1.1 or the stream
      // for HTTP2 because we don't know if we sent or finished the whole
      // request or not.
      try { Request.HttpContext.Abort(); } catch { }
      try { Response.HttpContext.Abort(); } catch { }

      // Just in case anything is still stuck
      CTS.Cancel();

      throw;
    }
    finally
    {
      // Set the state to closed
      State = LambdaConnectionState.Closed;

      try
      {
        await incomingResponse.CompleteAsync().ConfigureAwait(false);
      }
      catch { }

      // Mark that the Response has been sent on the LambdaInstance
      TCS.SetResult();
    }
  }

  private async Task RelayResponseFromLambda(HttpRequest incomingRequest, HttpResponse incomingResponse)
  {
    _logger.LogDebug("LambdaId: {} - Copying response body from Lambda", Instance.Id);

    // TODO: Get the 32 KB header size limit from configuration
    var headerBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
    try
    {
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

        var bytesRead = await Request.Body.ReadAsync(headerBuffer.AsMemory(totalBytesRead, headerBuffer.Length - totalBytesRead), CTS.Token).ConfigureAwait(false);
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
          if (headerBuffer[i] == (byte)'\n'
              && ((idxPriorLineFeed == i - 1)
                  || (idxPriorLineFeed == i - 2 && headerBuffer[i - 1] == (byte)'\r')))
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

      // Check if the Extension closed the connection without a response
      if (totalBytesRead == 0)
      {
        // The connection was closed without a response
        throw new Exception("Connection closed without a response");
      }

      // Read the status line
      // This buffer is huge and we may only have a small portion of it
      // Do not read past the end of where we have written to
      // The above loop will break out when ReadAsync returns 0,
      // which can happen if a connection closes before the headers are finished
      int endOfStatusLine = Array.IndexOf(headerBuffer, (byte)'\n', 0, totalBytesRead);
      if (endOfStatusLine == -1)
      {
        // Handle error: '\n' not found in the buffer
        throw new Exception("Status line not found in response");
      }

      string statusLine = Encoding.UTF8.GetString(headerBuffer, 0, endOfStatusLine);

      try
      {
        incomingResponse.StatusCode = int.Parse(statusLine.Split(' ')[1]);
      }
      catch (FormatException)
      {
        // This can happen if we get misaligned
        throw new Exception($"Invalid status code in response status line: {statusLine}");
      }

      // Start processing the rest of the headers from the character after '\n'
      int startOfNextLine = endOfStatusLine + 1;

      // Process the rest of the headers
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
        var parts = headerLine.Split(": ", 2, StringSplitOptions.None);

        var key = parts[0];
        var value = parts[1];
        if (string.Compare(key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) == 0)
        {
          // Don't set the Transfer-Encoding header as it breaks the response
          startOfNextLine = endOfLine + 1;
          continue;
        }

        // Set the header on the Kestrel response
        // We append otherwise we'd overwrite keys that appear multiple times
        // such as Set-Cookie
        incomingResponse.Headers.Append(key, value);

        // Move the start to the character after '\n'
        startOfNextLine = endOfLine + 1;
      }

      // Add identifying headers
      incomingResponse.Headers.Append("x-lambda-dispatch-version", _gitHash);
      incomingResponse.Headers.Append("x-lambda-dispatch-build-time", _buildTime);

      // Flush any remaining bytes in the buffer
      if (startOfNextLine < totalBytesRead)
      {
        // There are bytes left in the buffer
        // Copy them to the response
        await incomingResponse.BodyWriter.WriteAsync(headerBuffer.AsMemory(startOfNextLine, totalBytesRead - startOfNextLine), CTS.Token).ConfigureAwait(false);
      }
    }
    catch (Exception ex)
    {
      // The lambda application has not sent valid response headers
      // We do what an AWS ALB does which is to send a 502 status code
      // and close the connection
      _logger.LogError(ex, "LambdaId: {}, ChannelId: {} - Exception reading response headers from Lambda, Path: {}", Instance.Id, ChannelId, incomingRequest.Path);
      try
      {// Clear the headers
        incomingResponse.Headers.Clear();
        // Set the status code
        incomingResponse.StatusCode = StatusCodes.Status502BadGateway;
      }
      catch { }
      // Close the response from the extension
      try { Request.HttpContext.Abort(); } catch { }
      // Close the response to the client
      try { await incomingResponse.CompleteAsync(); }
      catch
      {
        try { incomingResponse.HttpContext.Abort(); } catch { }
      }
      throw;
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(headerBuffer);
    }

    //
    // Start writing the Response Body
    // This implicitly flushes the headers
    //

    var bytes = ArrayPool<byte>.Shared.Rent(128 * 1024);
    try
    {
      // Read from the source stream and write to the destination stream in a loop
      int bytesRead;
      var responseStream = Request.Body;
      while ((bytesRead = await responseStream.ReadAsync(bytes, CTS.Token).ConfigureAwait(false)) > 0)
      {
        await incomingResponse.Body.WriteAsync(bytes.AsMemory(0, bytesRead), CTS.Token).ConfigureAwait(false);
      }
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(bytes);
    }

    _logger.LogDebug("LambdaId: {} - Copied response body from Lambda", Instance.Id);
  }
}