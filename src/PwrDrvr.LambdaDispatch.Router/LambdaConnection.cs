using System.Buffers;
using System.Globalization;
using System.Text;

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

public class LambdaConnection
{
  private readonly ILogger<LambdaConnection> _logger = LoggerInstance.CreateLogger<LambdaConnection>();

  /// <summary>
  /// The state of the connection
  /// </summary>
  public LambdaConnectionState State { get; private set; }

  /// <summary>
  /// The Request from the Lambda (which we will send the response on)
  /// </summary>
  public HttpRequest Request { get; private set; }

  /// <summary>
  /// The Response from the Lambda (which we will send the request on)
  /// </summary>
  public HttpResponse Response { get; private set; }

  /// <summary>
  /// Handle back to the Lambda Instance that owns this Connection
  /// </summary>
  public LambdaInstance Instance { get; private set; }

  /// <summary>
  /// The channel id for this connection
  /// </summary>
  public string ChannelId { get; private set; }

  /// <summary>
  /// Task that completes when the connection is closed
  /// </summary>
  public TaskCompletionSource TCS { get; private set; } = new TaskCompletionSource();

  public LambdaConnection(HttpRequest request, HttpResponse response, LambdaInstance instance, string channelId)
  {
    // HttpRequestFeature foo = new HttpRequestFeature();

    // System.Net.Http.HttpRequestMessage bar = new System.Net.Http.HttpRequestMessage();
    // bar.Headers.Add("X-Lambda-Id", instance.Id);
    // bar.Content = new StreamContent(request.BodyReader.AsStream());

    // HttpParser.ParseRequestLine(feature, requestLineBuffer, out var method, out var requestUrl, out var version, out var badRequest)
    // Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.HttpParser parser = new Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.HttpParser();
    Request = request;
    Response = response;
    Instance = instance;
    ChannelId = channelId;

    // Set the state to open
    State = LambdaConnectionState.Open;

    // Handle an abnormal connection termination
    Request.HttpContext.RequestAborted.Register(() =>
    {
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

  public async Task Discard()
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
    // Response.StatusCode = 409;
    await Response.WriteAsync($"GOAWAY\r\nDiscarding connection for X-Lambda-Id: {Instance.Id}, X-Channel-Id: {ChannelId}, closing\r\n");
    await Response.CompleteAsync();
    try { await Request.Body.CopyToAsync(Stream.Null); } catch { }

    this.TCS.SetResult();
  }

  public async Task ProxyRequestToLambda(HttpRequest incomingRequest)
  {
    // Send the incoming Request on the lambda's Response
    _logger.LogDebug("Sending incoming request headers to Lambda");

    // Write the request line
    await this.Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes($"{incomingRequest.Method} {incomingRequest.Path} {incomingRequest.Protocol}\r\n"));

    // Send the headers to the Lambda
    foreach (var header in incomingRequest.Headers)
    {
      await this.Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes($"{header.Key}: {header.Value}\r\n"));
    }

    // Send the end of the headers
    await this.Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes("\r\n"));

    // Only copy the request body if the request has a body
    if (incomingRequest.ContentLength > 0 || (incomingRequest.Headers.ContainsKey("Transfer-Encoding") && incomingRequest.Headers["Transfer-Encoding"] == "chunked"))
    {
      _logger.LogDebug("Sending incoming request body to Lambda");

      // Send the body to the Lambda
      await incomingRequest.BodyReader.CopyToAsync(this.Response.BodyWriter);
      await incomingRequest.BodyReader.CompleteAsync();

      _logger.LogDebug("Finished sending incoming request body to Lambda");
    }

    // Mark that the Request has been sent on the LambdaInstances
    await this.Response.BodyWriter.CompleteAsync();
    await Response.CompleteAsync();

    // Get the response from the lambda request and relay it back to the caller
    _logger.LogDebug("Finished sending entire request to Lambda");
  }

  /// <summary>
  /// Run the request on the Lambda
  /// </summary>
  public async Task RunRequest(HttpRequest request, HttpResponse response)
  {
    try
    {
      // Check if state is wrong
      if (State != LambdaConnectionState.Open)
      {
        throw new InvalidOperationException("Connection is not open");
      }

      // Set the state to busy
      State = LambdaConnectionState.Busy;

      //
      // Send the incoming request to the Lambda
      //
      await this.ProxyRequestToLambda(request);

      //
      //
      // Read response from Lambda and relay back to caller
      //
      //

      _logger.LogDebug("Copying response body from Lambda");

      var headerBuffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
      try
      {
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

          var bytesRead = await Request.Body.ReadAsync(headerBuffer, totalBytesRead, headerBuffer.Length - totalBytesRead);
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

        string statusLine = Encoding.UTF8.GetString(headerBuffer, 0, endOfStatusLine);

        response.StatusCode = int.Parse(statusLine.Split(' ')[1]);

        // Start processing the rest of the headers from the character after '\n'
        int startOfNextLine = endOfStatusLine + 1;

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
          var value = string.Join(": ", parts.Skip(1));
          if (key == "Transfer-Encoding")
          {
            // Don't set the Transfer-Encoding header as it breaks the response
            continue;
          }

          // Set the header on the Kestrel response
          response.Headers[parts[0]] = parts[1];

          // Move the start to the character after '\n'
          startOfNextLine = endOfLine + 1;
        }

        // Flush any remaining bytes in the buffer
        if (startOfNextLine < totalBytesRead)
        {
          // There are bytes left in the buffer
          // Copy them to the response
          await response.BodyWriter.WriteAsync(headerBuffer.AsMemory(startOfNextLine, totalBytesRead - startOfNextLine));
        }
      }
      finally
      {
        ArrayPool<byte>.Shared.Return(headerBuffer);
      }

      // Copy the rest of the response body
      await Request.BodyReader.CopyToAsync(response.BodyWriter);

      _logger.LogDebug("Copied response body from Lambda");
    }
    catch (Exception ex)
    {
      if (this.Request.Headers.TryGetValue("Date", out var dateValues)
          && dateValues.Count == 1
          && DateTime.TryParse(dateValues.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var requestDate))
      {
        var duration = DateTime.UtcNow - requestDate;

        _logger.LogError(ex, "LambdaConnection.RunRequest - Exception - Request was received at {RequestDate}, {DurationInSeconds} seconds ago, LambdaID: {LambdaId}, ChannelId: {ChannelId}",
            requestDate.ToLocalTime().ToString("o"),
            duration.TotalSeconds,
            this.Instance.Id,
            this.ChannelId);
      }
      else
      {
        _logger.LogError(ex, "LambdaConnection.RunRequest - Exception - Receipt time not known");
      }
      try { await this.Request.Body.CopyToAsync(Stream.Null); } catch { }
    }
    finally
    {
      // Set the state to closed
      State = LambdaConnectionState.Closed;

      try { await response.CompleteAsync(); } catch { }

      // Mark that the Response has been sent on the LambdaInstance
      this.TCS.SetResult();
    }
  }
}