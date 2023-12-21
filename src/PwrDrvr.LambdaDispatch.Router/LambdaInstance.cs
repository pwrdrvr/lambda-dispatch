using Amazon.Lambda;
using Amazon.Lambda.Model;
using PwrDrvr.LambdaDispatch.Messages;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace PwrDrvr.LambdaDispatch.Router;

public enum LambdaInstanceState
{
  /// <summary>
  /// Object initial state
  /// </summary>
  Initial,

  /// <summary>
  /// The Lambda is starting up
  /// </summary>
  Starting,

  /// <summary>
  /// The Lambda is running normally
  /// </summary>
  Open,

  /// <summary>
  /// The Lambda is closing down
  /// New connections will be rejected
  /// </summary>
  Closing,

  /// <summary>
  /// The Lambda is closed
  /// Invoke has returned and all sockets have been closed
  /// </summary>
  Closed,
}

/// <summary>
/// Handles one lifetime of a lambda invoke
/// 
/// One Lambda will call back with 1 or more connections in parallel,
/// repeatedly until it's time to stop (e.g. 60 seconds left in the lifetime or idle for 10+ seconds)
/// </summary>
public class LambdaInstance
{
  private readonly ILogger<LambdaInstance> _logger = LoggerInstance.CreateLogger<LambdaInstance>();

  private readonly DateTime _startTime = DateTime.Now;

  /// <summary>
  /// Raised when the Lambda Instance has completed it's invocation
  /// </summary>
  public event Action<LambdaInstance>? OnInvocationComplete;

  /// <summary>
  /// Raised when the Lambda Instance has opened
  /// </summary>
  public event Action<LambdaInstance>? OnOpen;

  public bool WasOpened { get; private set; } = false;

  /// <summary>
  /// WARNING: This is going to enumerate the items in the queue to count them
  /// </summary>
  public int QueueApproximateCount => connectionQueue.Count;

  public LambdaInstanceState State { get; private set; } = LambdaInstanceState.Initial;


  private static AmazonLambdaConfig CreateConfig()
  {
    // Set env var AWS_LAMBDA_SERVICE_URL=http://host.docker.internal:5051
    // When testing with LambdaTestTool hosted outside of the dev container
    var serviceUrl = System.Environment.GetEnvironmentVariable("AWS_LAMBDA_SERVICE_URL");
    var config = new AmazonLambdaConfig
    {
      Timeout = TimeSpan.FromMinutes(15),
      MaxErrorRetry = 0
    };

    if (!string.IsNullOrEmpty(serviceUrl))
    {
      config.ServiceURL = serviceUrl;
    }

    return config;
  }

  public static readonly AmazonLambdaClient LambdaClient = new(CreateConfig());

  private readonly int maxConcurrentCount;

  public string Id { get; private set; } = Guid.NewGuid().ToString();

  /// <summary>
  /// Connections to the Lambda Instance
  /// If a connection closes we change it's state and decrement the available connection count
  /// But we do not remove it from the queue, it just gets discarded later when removed from the queue
  /// </summary>
  private readonly ConcurrentQueue<LambdaConnection> connectionQueue = new();

  /// <summary>
  /// This count should be accurate: as connections finish or abort, this count should be updated
  /// This allow us to subtract maxConcurrentCount - availableConnectionCount to get the number of
  /// connections that are busy or non-existing (which we can treat as busy)
  /// 
  /// This reduces funny business like instances being in the idle queue
  /// but actually not having any available connections
  /// </summary>
  private volatile int availableConnectionCount = 0;

  private volatile int openConnectionCount = 0;

  /// <summary>
  /// The number of requests that are outstanding
  /// But really the delta between the max concurrent count and the available connection count
  /// This prevents instances from being marked as idle when they are actually busy / have no available connections
  /// </summary>
  public int OutstandingRequestCount => maxConcurrentCount - availableConnectionCount;

  public int AvailableConnectionCount => availableConnectionCount;

  private int signaledStarting = 0;

  private int signalClosing = 0;

  public LambdaInstance(int maxConcurrentCount)
  {
    this.maxConcurrentCount = maxConcurrentCount;
  }

  /// <summary>
  /// Called when we get a connection for the Lambda Instance ID
  /// </summary>
  /// <param name="request"></param>
  /// <param name="response"></param>
  public async Task<LambdaConnection?> AddConnection(HttpRequest request, HttpResponse response, string channelId, bool immediateDispatch = false)
  {
    if (State == LambdaInstanceState.Closing || State == LambdaInstanceState.Closed)
    {
      _logger.LogWarning("Connection added to Lambda Instance that is closing or closed - closing with 409 LambdaId: {LambdaId}, ChannelId: {channelId}", Id, channelId);

      // Close the connection
      try
      {
        response.StatusCode = 409;
        await response.StartAsync();
        await response.WriteAsync($"No LambdaInstance found for X-Lambda-Id: {Id}, X-Channel-Id: {channelId}, closing");
        await response.CompleteAsync();
        try { await request.Body.CopyToAsync(Stream.Null); } catch { }
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "AddConnection - Exception closing down connection to LambdaId: {LambdaId}", Id);
      }

      return null;
    }

    // Signal that we are ready if this the first connection
    if (State == LambdaInstanceState.Starting && Interlocked.Exchange(ref signaledStarting, 1) == 0)
    {
      State = LambdaInstanceState.Open;

      MetricsRegistry.Metrics.Measure.Histogram.Update(MetricsRegistry.LambdaOpenDelay, (int)(DateTime.Now - _startTime).TotalMilliseconds);

      // Signal that we are open
      WasOpened = true;
      OnOpen?.Invoke(this);
    }

    // This is a race condition - it can absolutely happen because the instance
    // can be marked as closing at anytime, including between marking the instance open just above
    // and this line
    // if (State != LambdaInstanceState.Open)
    // {
    //   throw new InvalidOperationException("Cannot add a connection to a Lambda Instance that is not open");
    // }

    Interlocked.Increment(ref openConnectionCount);
    MetricsRegistry.Metrics.Measure.Histogram.Update(MetricsRegistry.LambdaInstanceOpenConnections, openConnectionCount);

    var connection = new LambdaConnection(request, response, this, channelId);

    // Only make this connection visible if we're not going to immediately use it for a queued request
    if (!immediateDispatch)
    {
      // Start the response
      // This sends the headers
      // The response will then hang around waiting for the data to be written to it
      await response.StartAsync();

      Interlocked.Increment(ref availableConnectionCount);
      connectionQueue.Enqueue(connection);
    }

    return connection;
  }

  /// <summary>
  /// Get an available connection from this Lambda Instance
  /// </summary>
  /// <returns></returns>
  public bool TryGetConnection([NotNullWhen(true)] out LambdaConnection? connection)
  {
    connection = null;

    if (State == LambdaInstanceState.Closing || State == LambdaInstanceState.Closed)
    {
      _logger.LogWarning("Connection requested from Lambda Instance that is closing or closed, LambdaId: {LambdaId}", Id);
      return false;
    }

    if (State != LambdaInstanceState.Open)
    {
      _logger.LogWarning("Connection requested from Lambda Instance that is not open, LambdaId: {LambdaId}", Id);
      return false;
    }

    // Loop through the connections until we find one that is available
    while (connectionQueue.TryDequeue(out var dequeuedConnection))
    {
      // We found an available connection
      Interlocked.Decrement(ref availableConnectionCount);

      // The connection should only be Closed unexpectedly, not Busy
      // This should not be a race condition as only one thread should
      // dequeue the connection and handle it
      if (dequeuedConnection.State == LambdaConnectionState.Busy)
      {
        _logger.LogError("TryGetConnection - Got a busy connection from queue, LambdaId: {LambdaId}, ChannelId: {ChannelId}", Id, dequeuedConnection.ChannelId);
        continue;
      }

      // If the connection is Closed we discard it (can happen on abnormal close during idle)
      if (dequeuedConnection.State == LambdaConnectionState.Closed)
      {
        // A connection that is closed should have triggered the onclose
        // handler which would have decremented the available connection count
        continue;
      }

      connection = dequeuedConnection;
      return true;
    }

    // No available connections
    _logger.LogInformation("TryGetConnection - No available connections for LambdaId: {LambdaId}, AvailableConnectionsCount: {AvailableConnectionCount}, QueueApproximateCount: {QueueApproximateCount}", Id, AvailableConnectionCount, QueueApproximateCount);

    return false;
  }

  /// <summary>
  /// Called when a connection is closed
  /// </summary>
  /// <param name="isBusy"></param>
  public void ConnectionClosed(bool isBusy)
  {
    Interlocked.Decrement(ref openConnectionCount);

    // Do not decrement the availableConnectionCount here because
    // it will be decremented when the connection is removed from the queue

    // Note: the Lambda itself will connect back if it's still running normally
    // Our invoke should cause maxConcurrentCount connections to be established
  }

  /// <summary>
  /// Release the connections without state checks
  /// We use this from both Close and CloseAsync
  /// </summary>
  private async Task<int> ReleaseConnections()
  {
    // Close all the connections that are not in use
    var releasedConnectionCount = 0;
    while (connectionQueue.TryDequeue(out var connection))
    {
      // Decrement the available connection count
      Interlocked.Decrement(ref availableConnectionCount);

      // If the connection is Closed we discard it (can happen on abnormal close during idle)
      if (connection.State == LambdaConnectionState.Closed)
      {
        continue;
      }

      // Close the connection
      try
      {
        await connection.Discard();
        releasedConnectionCount++;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "ReleaseConnections - Exception closing down connection to LambdaId: {LambdaId}", Id);
      }
    }

    // NOTE: Some connections may still be open, but they will be closed when
    // their in flight request is finished
    return releasedConnectionCount;
  }

  /// <summary>
  /// Closes in the background so the Lambda can exist as soon as it sees it's last connection close
  /// </summary>
  public void Close()
  {
    // Ignore if already closing
    if (Interlocked.Exchange(ref signalClosing, 1) == 1)
    {
      // Already closing
      return;
    }

    // We do this in the background so the Lambda can exit as soon as the last
    // connection to it is closed
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    Task.Run(async () =>
    {
      var releasedConnectionCount = 0;
      for (int i = 0; i < 5; i++)
      {
        releasedConnectionCount += await ReleaseConnections();

        await Task.Delay(1000);
      }

      _logger.LogInformation("Released {ReleasedConnectionCount} connections for LambdaId: {LambdaId}, AvailableConnectionCount: {AvailableConnectionCount}", releasedConnectionCount, Id, this.AvailableConnectionCount);
    });
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

    // NOTE: Some connections may still be open, but they will be closed when
    // their in flight request is finished
  }

  /// <summary>
  /// Close all available connections to the Lambda Instance
  /// 
  /// Use a status code that indicates that the connection should not be
  /// re-opened by the Lambda
  /// </summary>
  public async Task CloseAsync()
  {
    // Ignore if already closing
    if (Interlocked.Exchange(ref signalClosing, 1) == 1)
    {
      // Already closing
      return;
    }

    State = LambdaInstanceState.Closing;

    // Close all the connections that are not in use
    var releasedConnectionCount = await ReleaseConnections();
    _logger.LogInformation("Released {ReleasedConnectionCount} connections for LambdaId: {LambdaId}, AvailableConnectionCount: {AvailableConnectionCount}", releasedConnectionCount, Id, this.AvailableConnectionCount);

    // Set the state to closed
    State = LambdaInstanceState.Closed;

    // NOTE: Some connections may still be open, but they will be closed when
    // their in flight request is finished
  }

  /// <summary>
  /// Invoke the Lambda, which should cause it to connect back to us
  /// We do not count the connection as available until it connects back to us
  /// Each instance can have many connections at a time to us
  /// </summary>
  /// <returns></returns>
  public async Task Start()
  {
    _logger.LogInformation("Starting Lambda Instance {Id}", Id);

    MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.LambdaInstanceCount);

    // Throw if the instance is already open or closed
    // This isn't a race condition because there should only be a single call
    // to this ever, before anything async starts (haven't even invoked the Lambda yet)
    if (State != LambdaInstanceState.Initial)
    {
      throw new InvalidOperationException("Cannot start a Lambda Instance that is not in the initial state");
    }

    State = LambdaInstanceState.Starting;

    // Setup the Lambda payload
    var payload = new WaiterRequest
    {
      Id = Id,
      DispatcherUrl = await GetCallbackIP.Get(),
      NumberOfChannels = maxConcurrentCount,
      SentTime = DateTime.Now
    };

    // Invoke the Lambda
    var request = new InvokeRequest
    {
      // TODO: Get this from the configuration
      FunctionName = "lambda-dispatch-lambdalb",
      InvocationType = InvocationType.RequestResponse,
      Payload = JsonSerializer.Serialize(payload)
    };

    // Should not wait here as we will not get a response until the Lambda is done
    var invokeTask = LambdaClient.InvokeAsync(request);

    // Handle completion of the task
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    invokeTask.ContinueWith(t =>
    {
      MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.LambdaInstanceCount);

      // NOTE: The Lambda will return via the callback to indicate that it's shutting down
      // but it will linger until we close the responses to it's requests
      // This prevents race conditions on shutdown

      OnInvocationComplete?.Invoke(this);

      if (t.IsFaulted)
      {
        // Handle any exceptions that occurred during the invocation
        Exception ex = t.Exception;
        _logger.LogError("LambdaInvoke for Id {Id}, gave error: {Message}", this.Id, ex.Message);
      }
      else if (t.IsCompleted)
      {
        // The Lambda invocation has completed
        _logger.LogDebug("LambdaInvoke completed for Id: {Id}", this.Id);
      }
    });
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
  }

  public async Task ReenqueueUnusedConnection(LambdaConnection connection)
  {
    // If the lambda is closing then we don't re-enqueue the connection
    if (State == LambdaInstanceState.Closing || State == LambdaInstanceState.Closed)
    {
      await connection.Discard();
      return;
    }

    // Increment the available connection count
    Interlocked.Increment(ref availableConnectionCount);

    // Re-enqueue the connection
    connectionQueue.Enqueue(connection);
  }
}