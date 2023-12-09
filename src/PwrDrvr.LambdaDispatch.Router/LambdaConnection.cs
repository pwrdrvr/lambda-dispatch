using Amazon.Lambda;
using Amazon.Lambda.Model;
using PwrDrvr.LambdaDispatch.Messages;
using System.Text.Json;
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
  /// Task that completes when the connection is closed
  /// </summary>
  public TaskCompletionSource TCS { get; private set; } = new TaskCompletionSource();

  public LambdaConnection(HttpRequest request, HttpResponse response, LambdaInstance instance)
  {
    Request = request;
    Response = response;
    Instance = instance;

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

  public async Task RunRequest(HttpRequest request, HttpResponse response)
  {
    // Check if state is wrong
    if (State != LambdaConnectionState.Open)
    {
      throw new InvalidOperationException("Connection is not open");
    }

    // Set the state to busy
    State = LambdaConnectionState.Busy;

    // Send the incoming Request on the lambda's Response
    _logger.LogDebug("Sending incoming request headers to Lambda");

    // TODO: Write the request line
    await this.Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes($"{request.Method} {request.Path} {request.Protocol}\r\n"));

    // Send the headers to the Lambda
    foreach (var header in request.Headers)
    {
      await this.Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes($"{header.Key}: {header.Value}\r\n"));
    }

    // Only copy the request body if the request has a body
    if (request.ContentLength > 0 || (request.Headers.ContainsKey("Transfer-Encoding") && request.Headers["Transfer-Encoding"] == "chunked"))
    {
      {
        await this.Response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes("\r\n"));
      }

      _logger.LogDebug("Sending incoming request body to Lambda");

      // Send the body to the Lambda
      await request.BodyReader.CopyToAsync(this.Response.BodyWriter.AsStream());
      await request.BodyReader.CompleteAsync();

      _logger.LogDebug("Finished sending incoming request body to Lambda");
    }

    // Mark that the Request has been sent on the LambdaInstances
    await this.Response.BodyWriter.CompleteAsync();

    // Get the response from the lambda request and relay it back to the caller
    _logger.LogDebug("Finished sending entire request to Lambda");

    //
    //
    // Read response from Lambda and relay back to caller
    //
    //

    _logger.LogDebug("Reading response headers from Lambda");

    // Send the headers to the caller
    // This was reading the response headers from the Lambda
    // foreach (var header in this.Response.Headers)
    // {
    //   // Do not set the status code by adding a header
    //   if (header.Key == "Status-Code")
    //   {
    //     // Set the status code on the response
    //     response.StatusCode = int.Parse(header.Value);
    //     _logger.LogDebug($"Set response status code to {header.Value}");
    //     continue;
    //   }

    //   if (header.Key == "Transfer-Encoding")
    //   {
    //     // Don't send the Transfer-Encoding header
    //     continue;
    //   }
    //   response.Headers.Add(header.Key, header.Value);
    //   _logger.LogDebug($"Sent reponse header to caller: {header.Key}: {header.Value}");
    // }

    _logger.LogDebug("Finished reading response headers from Lambda");

    _logger.LogDebug("Copying response body from Lambda");

    // Send the body to the caller
    using var lambdaResponseReader = new StreamReader(this.Request.BodyReader.AsStream(), leaveOpen: true);
    string? line;
    // First line should be status
    line = await lambdaResponseReader.ReadLineAsync();
    _logger.LogDebug("Got status line from lambda: {line}", line);
    response.StatusCode = int.Parse(line.Split(' ')[1]);
    while (!string.IsNullOrEmpty(line = await lambdaResponseReader.ReadLineAsync()))
    {
      _logger.LogDebug("Got header line from lambda: {line}", line);

      // Parse the header
      var parts = line.Split(new[] { ": " }, 2, StringSplitOptions.None);
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
    }

    while ((line = await lambdaResponseReader.ReadLineAsync()) != null)
    {
      _logger.LogDebug("Got body line from lambda: {line}", line);
      await response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes(line));
      await response.BodyWriter.WriteAsync(Encoding.UTF8.GetBytes("\r\n"));
    }

    // await lambdaInstance.Request.BodyReader.CopyToAsync(response.BodyWriter.AsStream());

    _logger.LogDebug("Copied response body from Lambda");

    await response.BodyWriter.CompleteAsync();

    // Mark that the Response has been sent on the LambdaInstance
    this.TCS.SetResult();
  }
}