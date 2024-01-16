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

public interface ILambdaInstance
{
  /// <summary>
  /// Called when a connection is closed
  /// </summary>
  void ConnectionClosed(bool isBusy);

  /// <summary>
  /// Raised when the Lambda Instance has completed it's invocation
  /// </summary>
  event Action<ILambdaInstance>? OnInvocationComplete;

  /// <summary>
  /// Raised when the Lambda Instance has opened
  /// </summary>
  event Action<ILambdaInstance>? OnOpen;

  /// <summary>
  /// WARNING: This is going to enumerate the items in the queue to count them
  /// </summary>
  int QueueApproximateCount { get; }

  int MaxConcurrentCount { get; }

  /// <summary>
  /// State of this Lambda Instance
  /// </summary>
  LambdaInstanceState State { get; }

  /// <summary>
  /// If true, the Lambda Instance should not be replaced when the OnInvocationComplete event is raised
  /// We set this when we decide to stop an instance
  /// </summary>
  bool DoNotReplace { get; }

  /// <summary>
  /// Task that completes when the Lambda Instance is done
  /// </summary>
  Task<bool> InvokeCompletionTask { get; }

  /// <summary>
  /// This count should be accurate: as connections finish or abort, this count should be updated
  /// This allow us to subtract maxConcurrentCount - availableConnectionCount to get the number of
  /// connections that are busy or non-existing (which we can treat as busy)
  /// 
  /// This reduces funny business like instances being in the idle queue
  /// but actually not having any available connections
  /// </summary>
  int AvailableConnectionCount { get; }

  /// <summary>
  /// The number of requests that are outstanding
  /// But really the delta between the max concurrent count and the available connection count
  /// This prevents instances from being marked as idle when they are actually busy / have no available connections
  /// </summary>
  int OutstandingRequestCount { get; }

  /// <summary>
  /// Id of the instance
  /// </summary>
  string Id { get; }

  /// <summary>
  /// Mark this instance as closing
  /// </summary>
  public void Close(bool doNotReplace = false);

  public bool WasOpened { get; }

  public bool TryGetConnection([NotNullWhen(true)] out LambdaConnection? connection, bool tentative = false);

  public void TryGetConnectionWillUse(LambdaConnection connection);

  public Task<LambdaConnection?> AddConnection(HttpRequest request, HttpResponse response, string channelId, bool immediateDispatch = false);

  public ValueTask ReenqueueUnusedConnection(LambdaConnection connection);
}

/// <summary>
/// Handles one lifetime of a lambda invoke
/// 
/// One Lambda will call back with 1 or more connections in parallel,
/// repeatedly until it's time to stop (e.g. 60 seconds left in the lifetime or idle for 10+ seconds)
/// </summary>
public class LambdaInstance : ILambdaInstance
{
  private readonly ILogger<LambdaInstance> _logger = LoggerInstance.CreateLogger<LambdaInstance>();

  private readonly DateTime _startTime = DateTime.Now;

  /// <summary>
  /// Raised when the Lambda Instance has completed it's invocation
  /// </summary>
  public event Action<ILambdaInstance>? OnInvocationComplete;

  /// <summary>
  /// Raised when the Lambda Instance has opened
  /// </summary>
  public event Action<ILambdaInstance>? OnOpen;

  public bool WasOpened { get; private set; } = false;

  /// <summary>
  /// WARNING: This is going to enumerate the items in the queue to count them
  /// </summary>
  public int QueueApproximateCount => connectionQueue.Count;

  public int MaxConcurrentCount => maxConcurrentCount;

  /// <summary>
  /// State of this Lambda Instance
  /// </summary>
  public LambdaInstanceState State { get; private set; } = LambdaInstanceState.Initial;

  /// <summary>
  /// If true, the Lambda Instance should not be replaced when the OnInvocationComplete event is raised
  /// We set this when we decide to stop an instance
  /// </summary>
  public bool DoNotReplace { get; private set; } = false;

  // Add a task completion source
  // When the lambda is done we set the task completion source
  // and then we can wait on it to know when the lambda is done
  private readonly TaskCompletionSource<bool> _tcs = new();

  private readonly string functionName;

  private readonly string? functionQualifier;

  private readonly int maxConcurrentCount;

  private readonly int channelCount;

  private readonly IAmazonLambda LambdaClient;

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
  private volatile int internalActualAvailableConnectionCount = 0;

  /// <summary>
  /// The actual number of requests in flight
  /// This is used to determine how busy we are, regardless of whether
  /// we have a full set of idle connections or a partial set
  /// </summary>
  private volatile int outstandingRequestCount = 0;

  private volatile int openConnectionCount = 0;

  /// <summary>
  /// The number of requests that are outstanding
  /// But really the delta between the max concurrent count and the available connection count
  /// This prevents instances from being marked as idle when they are actually busy / have no available connections
  /// </summary>
  public int OutstandingRequestCount => Math.Max(outstandingRequestCount, 0);

  /// <summary>
  /// The number of connections that we should use
  /// We may have more connections than we are supposed to use, we hide these
  /// 
  /// OutstandingRequestCount can go slightly higher than the limit
  /// when there are pre-emptive connections because we don't do
  /// aggressive locking as it is too expensive.
  /// </summary>
  public int AvailableConnectionCount => Math.Min(Math.Max(maxConcurrentCount - Math.Min(OutstandingRequestCount, maxConcurrentCount), 0), internalActualAvailableConnectionCount);
  private int signaledStarting = 0;

  private int signalClosing = 0;

  private readonly IBackgroundDispatcher dispatcher;

  /// <summary>
  /// 
  /// </summary>
  /// <param name="maxConcurrentCount"></param>
  /// <param name="functionName"></param>
  /// <param name="functionQualifier"></param>
  /// <param name="lambdaClient"></param>
  /// <param name="dispatcher">REQUIRED: Link to an IBackgroundDispatcher</param>
  /// <exception cref="ArgumentNullException"></exception>
  /// <exception cref="ArgumentException"></exception>
  /// <exception cref="ArgumentOutOfRangeException"></exception>
  public LambdaInstance(int maxConcurrentCount, string functionName, string? functionQualifier = null, IAmazonLambda? lambdaClient = null, IBackgroundDispatcher? dispatcher = null, int channelCount = -1)
  {
    ArgumentNullException.ThrowIfNull(dispatcher);
    this.dispatcher = dispatcher;
    this.channelCount = channelCount;
    if (string.IsNullOrWhiteSpace(functionName))
    {
      throw new ArgumentException("Cannot be null or whitespace", nameof(functionName));
    }
    this.functionName = functionName;
    this.functionQualifier = functionQualifier;

    if (maxConcurrentCount < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(maxConcurrentCount), "Must be greater than 0");
    }
    this.maxConcurrentCount = maxConcurrentCount;

    LambdaClient = lambdaClient ?? LambdaClientConfig.LambdaClient;
  }

  /// <summary>
  /// Task that completes when the Lambda Instance is done
  /// </summary>
  public Task<bool> InvokeCompletionTask => _tcs.Task;

  /// <summary>
  /// Called when we get a connection for the Lambda Instance ID
  /// </summary>
  /// <param name="request"></param>
  /// <param name="response"></param>
  public async Task<LambdaConnection?> AddConnection(HttpRequest request, HttpResponse response, string channelId, bool immediateDispatch = false)
  {
    if (State == LambdaInstanceState.Closing || State == LambdaInstanceState.Closed || State == LambdaInstanceState.Initial)
    {
      _logger.LogWarning("Connection added to Lambda Instance that is not starting or open - closing with 409 LambdaId: {LambdaId}, ChannelId: {channelId}", Id, channelId);

      // Close the connection
      try
      {
        response.StatusCode = 409;
        await response.StartAsync();
        await response.WriteAsync($"LambdaInstance not in Open or Starting state for X-Lambda-Id: {Id}, X-Channel-Id: {channelId}, closing");
        await response.CompleteAsync();
        try { await request.BodyReader.CopyToAsync(Stream.Null); } catch { }
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

      Interlocked.Increment(ref internalActualAvailableConnectionCount);
      connectionQueue.Enqueue(connection);
    }
    else
    {
      // The connection is being immediately used
      // Need to track the outstanding request
      Interlocked.Increment(ref outstandingRequestCount);
      // Register the decrement
      TryGetConnectionWillUse(connection);
    }

    return connection;
  }

  public bool TryGetConnection([NotNullWhen(true)] out LambdaConnection? connection, bool tentative = false)
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
    // Hide the connections that are not supposed to be used
    while (AvailableConnectionCount > 0
          && connectionQueue.TryDequeue(out var dequeuedConnection))
    {
      // We found an available connection
      Interlocked.Decrement(ref internalActualAvailableConnectionCount);

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

      Interlocked.Increment(ref outstandingRequestCount);

      if (!tentative)
      {
        TryGetConnectionWillUse(dequeuedConnection);
      }

      return true;
    }

    // No available connections
    // This can happen from shutdowns, dispatches when another request finishes, etc
    _logger.LogDebug("TryGetConnection - No available connections for LambdaId: {LambdaId}, AvailableConnectionsCount: {AvailableConnectionCount}, QueueApproximateCount: {QueueApproximateCount}", Id, AvailableConnectionCount, QueueApproximateCount);

    return false;
  }

  public void TryGetConnectionWillUse(LambdaConnection connection)
  {
    connection.Response.OnCompleted(Task () =>
    {
      Interlocked.Decrement(ref outstandingRequestCount);

      //
      // We have decremented the outstanding request count, which means,
      // if our instance has pre-emptive connections, that our instance
      // can now handle one more request
      //
      // If we do not dispatch here then the background dispatcher will
      // pick this up, but only 1 every 10 ms.  That's very slow.
      //
      // So see if there is a pending request that we can dispatch
      // and a connection we can do it on
      //

      // NOTE: We do not await this else we'd get locked up requests and deep call stacks
      const int maxAttempts = 3;
      Task.Run(async () =>
      {
        // We try this a few times, similarly to a spin lock
        // Otherwise, single-channel situations will cause 2x-3x more
        // background dispatches which are slow
        for (int i = 0; i < maxAttempts; i++)
        {
          var result = await dispatcher.TryBackgroundDispatchOne(countAsForeground: true).ConfigureAwait(false);

          // If the dispatch was successful, break out of the loop
          if (result)
          {
            break;
          }
        }
      });

      return Task.CompletedTask;
    });
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
      Interlocked.Decrement(ref internalActualAvailableConnectionCount);

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
  public void Close(bool doNotReplace = false)
  {
    // Ignore if already closing
    if (Interlocked.Exchange(ref signalClosing, 1) == 1)
    {
      // Already closing
      return;
    }

    DoNotReplace = doNotReplace;

    State = LambdaInstanceState.Closing;

    // We do this in the background so the Lambda can exit as soon as the last
    // connection to it is closed
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    Task.Run(async () =>
    {
      var releasedConnectionCount = 0;
      for (int i = 0; i < 5; i++)
      {
        releasedConnectionCount += await ReleaseConnections();

        // Wait for the task completion or 1 second
        var delayTask = Task.Delay(TimeSpan.FromSeconds(1));
        var completedTask = await Task.WhenAny(_tcs.Task, delayTask);

        if (completedTask == _tcs.Task)
        {
          // The lambda is done
          break;
        }
      }

      State = LambdaInstanceState.Closed;

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
  public async Task CloseAsync(bool doNotReplace = false)
  {
    // Ignore if already closing
    if (Interlocked.Exchange(ref signalClosing, 1) == 1)
    {
      // Already closing
      return;
    }

    DoNotReplace = doNotReplace;

    State = LambdaInstanceState.Closing;

    // Close all the connections that are not in use
    var releasedConnectionCount = await ReleaseConnections();
    _logger.LogInformation("Released {ReleasedConnectionCount} connections for LambdaId: {LambdaId}, AvailableConnectionCount: {AvailableConnectionCount}", releasedConnectionCount, Id, this.AvailableConnectionCount);

    // Set the state to closed
    State = LambdaInstanceState.Closed;

    // NOTE: Some connections may still be open, but they will be closed when
    // their in flight request is finished
  }

#if TEST_RUNNERS
  public void FakeStart(string instanceId)
  {
    State = LambdaInstanceState.Starting;
    signaledStarting = 1;
    State = LambdaInstanceState.Open;
    WasOpened = true;
    Id = instanceId;
    // OnOpen?.Invoke(this);
  }
#endif

  /// <summary>
  /// Invoke the Lambda, which should cause it to connect back to us
  /// We do not count the connection as available until it connects back to us
  /// Each instance can have many connections at a time to us
  /// </summary>
  /// <returns></returns>
  public async Task Start()
  {
    _logger.LogInformation("Starting Lambda Instance {Id}", Id);

    MetricsRegistry.Metrics.Measure.Counter.Increment(MetricsRegistry.LambdaInvokeCount);

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
      NumberOfChannels = channelCount == -1 ? 2 * maxConcurrentCount : channelCount,
      SentTime = DateTime.Now
    };

    // Invoke the Lambda
    var request = new InvokeRequest
    {
      FunctionName = functionName,
      InvocationType = InvocationType.RequestResponse,
      Payload = JsonSerializer.Serialize(payload),
    };
    if (functionQualifier != null && functionQualifier != "$LATEST")
    {
      request.Qualifier = functionQualifier;
    }

    // Should not wait here as we will not get a response until the Lambda is done
    var invokeTask = LambdaClient.InvokeAsync(request);

    // Handle completion of the task
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    invokeTask.ContinueWith(t =>
    {
      MetricsRegistry.Metrics.Measure.Counter.Decrement(MetricsRegistry.LambdaInvokeCount);

      // NOTE: The Lambda will return via the callback to indicate that it's shutting down
      // but it will linger until we close the responses to it's requests
      // This prevents race conditions on shutdown

      OnInvocationComplete?.Invoke(this);

      if (t.IsFaulted)
      {
        // Handle any exceptions that occurred during the invocation
        Exception ex = t.Exception;
        _tcs.SetException(ex);
        _logger.LogError("LambdaInvoke for LambdaId: {Id}, gave error: {Message}", this.Id, ex.Message);
      }
      else if (t.IsCompleted)
      {
        // The Lambda invocation has completed
        _tcs.SetResult(true);
        _logger.LogDebug("LambdaInvoke completed for LambdaId: {Id}", this.Id);
      }
    });
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
  }

  public async ValueTask ReenqueueUnusedConnection(LambdaConnection connection)
  {
    Interlocked.Decrement(ref outstandingRequestCount);

    // If the lambda is closing then we don't re-enqueue the connection
    if (State == LambdaInstanceState.Closing || State == LambdaInstanceState.Closed)
    {
      await connection.Discard();
      return;
    }

    // Increment the available connection count
    Interlocked.Increment(ref internalActualAvailableConnectionCount);

    // Re-enqueue the connection
    connectionQueue.Enqueue(connection);
  }
}