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
  /// This reducs funny business like instances being in the idle queue
  /// but actually not having any available connections
  /// </summary>
  private volatile int availableConnectionCount = 0;

  private volatile int openConnectionCount = 0;

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
        await response.CompleteAsync();
        await request.Body.CopyToAsync(Stream.Null);
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

      // We found an available connection
      Interlocked.Decrement(ref availableConnectionCount);
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

    // If the connection was busy then it was not counted as available
    if (!isBusy)
    {
      // Connection was not busy, so it was available
      Interlocked.Decrement(ref availableConnectionCount);
    }

    // Note: the Lambda itself will connect back if it's still running normally
    // Our invoke should cause maxConcurrentCount connections to be established
  }

  public void ReleaseConnections()
  {
    // Mark that we are closing
    State = LambdaInstanceState.Closing;

    // We do this in the background so the Lambda can exit as soon as the last
    // connection to it is closed
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    Task.Run(async () =>
    {
      // Close all the connections that are not in use
      var releasedConnectionCount = 0;
      for (int i = 0; i < 5; i++)
      {
        while (connectionQueue.TryDequeue(out var connection))
        {
          // If the connection is Closed we discard it (can happen on abnormal close during idle)
          if (connection.State == LambdaConnectionState.Closed)
          {
            continue;
          }

          // Decrement the available connection count
          Interlocked.Decrement(ref availableConnectionCount);

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

        await Task.Delay(1000);
      }

      _logger.LogInformation("Released {ReleasedConnectionCount} connections for LambdaId: {LambdaId}, AvailableConnectionCount {AvailableConnectionCount}", releasedConnectionCount, Id, this.AvailableConnectionCount);

      // The state should be set to Closed when the InvokeAsync returns
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
  public async Task Close()
  {
    // Ignore if already closing
    if (Interlocked.Exchange(ref signalClosing, 1) == 1)
    {
      // Already closing
      return;
    }

    State = LambdaInstanceState.Closing;

    // Close all the connections that are not in use
    while (connectionQueue.TryDequeue(out var connection))
    {
      // If the connection is Closed we discard it (can happen on abnormal close during idle)
      if (connection.State == LambdaConnectionState.Closed)
      {
        continue;
      }

      // Decrement the available connection count
      Interlocked.Decrement(ref availableConnectionCount);

      // Close the connection
      try
      {
        await connection.Discard();
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Close - Exception closing down connection to LambdaId: {LambdaId}", Id);
      }
    }

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
      NumberOfChannels = maxConcurrentCount
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