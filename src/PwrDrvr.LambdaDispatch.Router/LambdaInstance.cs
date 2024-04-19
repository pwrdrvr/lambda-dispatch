using Amazon.Lambda;
using Amazon.Lambda.Model;
using PwrDrvr.LambdaDispatch.Messages;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace PwrDrvr.LambdaDispatch.Router;

public enum AddConnectionDispatchMode
{
  ImmediateDispatch,
  TentativeDispatch,
  Enqueue
}

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
  /// The Lambda is being evaluated for closing
  /// The lambda will either wait to be replaced
  /// or immediately proceed to closing
  /// </summary>
  Draining,

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

public struct TransitionResult
{
  public bool TransitionedToDraining { get; set; }
  public bool WasOpened { get; set; }
  public bool OpenWasRejected { get; set; }
}

public struct AddConnectionResult
{
  public bool WasRejected { get; set; }
  public bool CanUseNow { get; set; }
  public ILambdaConnection? Connection { get; set; }
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
  /// Is the Lambda effectively "Open" and able to be used
  /// </summary>
  bool IsOpen { get; }

  /// <summary>
  /// Set by LambdaInstanceManager if the instance is being replaced
  /// </summary>
  bool Replacing { get; set; }

  /// <summary>
  /// Duration that the close can be delayed as determined by the LambdaInstanceManager
  /// </summary>
  TimeSpan AllowedDrainingDelay { get; set; }

  /// <summary>
  /// If true, the Lambda Instance close was initiated by the Lambda itself
  /// </summary>
  public bool LamdbdaInitiatedClose { get; }

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
  Task Close(bool doNotReplace = false, bool lambdaInitiated = false, bool openWasRejected = false);

  /// <summary>
  /// Used to mark the instance as closed when detected by invoke completion
  /// </summary>
  /// <returns></returns>
  bool InvokeCompleted();

  /// <summary>
  /// Destructive - Transitions to the Draining state
  /// if not already in Draining, Closing, or Closed
  /// </summary>
  /// <returns>Whether the transition was successful</returns>
  TransitionResult TransitionToDraining();

  bool TryGetConnection([NotNullWhen(true)] out ILambdaConnection? connection, bool tentative = false);

  void TryGetConnectionWillUse(ILambdaConnection connection);

  Task<AddConnectionResult> AddConnection(HttpRequest request, HttpResponse response, string channelId, AddConnectionDispatchMode dispatchMode);

  void ReenqueueUnusedConnection(ILambdaConnection connection);
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

  private readonly Stopwatch _startTime = new();

  /// <summary>
  /// Raised when the Lambda Instance has completed it's invocation
  /// </summary>
  public event Action<ILambdaInstance>? OnInvocationComplete;

  /// <summary>
  /// Raised when the Lambda Instance initiates a close before invoke ends
  /// </summary>
  public event Action<ILambdaInstance>? OnCloseInitiated;

  /// <summary>
  /// Raised when the Lambda Instance has opened
  /// </summary>
  public event Action<ILambdaInstance>? OnOpen;

  public bool Replacing { get; set; }

  public TimeSpan AllowedDrainingDelay { get; set; } = TimeSpan.Zero;

  public bool IsOpen
  {
    get
    {
      return State == LambdaInstanceState.Open || State == LambdaInstanceState.Draining;
    }
  }

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

  /// <summary>
  /// If true, the Lambda Instance close was initiated by the Lambda itself
  /// </summary>
  public bool LamdbdaInitiatedClose { get; private set; } = false;

  /// <summary>
  /// Was the lambda opened and registered with the InstanceManager
  /// If true, and OpenWasRejected is true, this means the InstanceManager should decrement the running count on invoke complete
  /// Else, if OpenWasRejected is false, then the InstanceManager should decrment the starting count on invoke complete
  /// </summary>
  private bool WasOpened { get; set; } = false;

  /// <summary>
  /// Was the open rejected by the OnOpen handler
  /// If true, this means the InstanceManager should not decrement starting or running counts on invoke complete
  /// </summary>
  private bool OpenWasRejected { get; set; } = false;

  // Add a task completion source
  // When the lambda is done we set the task completion source
  // and then we can wait on it to know when the lambda is done
  private readonly TaskCompletionSource<bool> _tcs = new();

  private readonly string functionName;

  private readonly string poolId;

  private readonly int maxConcurrentCount;

  private readonly int channelCount;

  /// <summary>
  /// Lock for changes to State and reads that will mututate other state
  /// </summary>
  private readonly object stateLock = new();

  private readonly IAmazonLambda LambdaClient;

  public string Id { get; private set; } = Guid.NewGuid().ToString();

  /// <summary>
  /// Connections to the Lambda Instance
  /// If a connection closes we change it's state and decrement the available connection count
  /// But we do not remove it from the queue, it just gets discarded later when removed from the queue
  /// </summary>
  private readonly ConcurrentQueue<ILambdaConnection> connectionQueue = new();

  /// <summary>
  /// This count should be accurate: as connections finish or abort, this count should be updated
  /// This allow us to subtract maxConcurrentCount - availableConnectionCount to get the number of
  /// connections that are busy or non-existing (which we can treat as busy)
  /// 
  /// This reduces funny business like instances being in the idle queue
  /// but actually not having any available connections
  /// </summary>
  private volatile int internalActualAvailableConnectionCount = 0;

  private readonly object requestCountLock = new();

  /// <summary>
  /// The actual number of requests in flight
  /// This is used to determine how busy we are, regardless of whether
  /// we have a full set of idle connections or a partial set
  /// </summary>
  private int outstandingRequestCount = 0;

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

  private readonly IGetCallbackIP getCallbackIP;

  private readonly IBackgroundDispatcher dispatcher;

  private readonly IMetricsRegistry metricsRegistry;

  /// <summary>
  /// 
  /// </summary>
  /// <param name="maxConcurrentCount"></param>
  /// <param name="functionName"></param>
  /// <param name="lambdaClient"></param>
  /// <param name="dispatcher">REQUIRED: Link to an IBackgroundDispatcher</param>
  /// <exception cref="ArgumentNullException"></exception>
  /// <exception cref="ArgumentException"></exception>
  /// <exception cref="ArgumentOutOfRangeException"></exception>
  public LambdaInstance(
    int maxConcurrentCount,
    string functionName,
    string poolId,
    IGetCallbackIP getCallbackIP,
    IBackgroundDispatcher dispatcher,
    IMetricsRegistry metricsRegistry,
    ILambdaClientConfig lambdaClientConfig,
    IAmazonLambda? lambdaClient = null,
    int channelCount = -1)
  {
    this.getCallbackIP = getCallbackIP;
    this.dispatcher = dispatcher;
    this.channelCount = channelCount;
    if (string.IsNullOrWhiteSpace(functionName))
    {
      throw new ArgumentException("Cannot be null or whitespace", nameof(functionName));
    }
    this.functionName = functionName;
    this.poolId = poolId;
    this.metricsRegistry = metricsRegistry;

    if (maxConcurrentCount < 1)
    {
      throw new ArgumentOutOfRangeException(nameof(maxConcurrentCount), "Must be greater than 0");
    }
    this.maxConcurrentCount = maxConcurrentCount;

    LambdaClient = lambdaClient ?? lambdaClientConfig.LambdaClient;
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
  public async Task<AddConnectionResult> AddConnection(HttpRequest request, HttpResponse response, string channelId,
    AddConnectionDispatchMode dispatchMode)
  {
    // This does not need a state lock because the race around
    // closing is handled
    if (State == LambdaInstanceState.Closed
        || State == LambdaInstanceState.Closing
        || State == LambdaInstanceState.Initial)
    {
      _logger.LogDebug("Connection added to Lambda Instance that is not starting or open - closing with 409 LambdaId: {LambdaId}, ChannelId: {channelId}", Id, channelId);

      return new AddConnectionResult
      {
        WasRejected = true,
        CanUseNow = false,
        Connection = null
      };
    }

    // Signal that we are ready if this the first connection
    var firstConnectionForInstance = false;
    if (State == LambdaInstanceState.Starting)
    {
      lock (stateLock)
      {
        if (State == LambdaInstanceState.Starting)
        {
          State = LambdaInstanceState.Open;
          firstConnectionForInstance = true;
          // Signal that we are open
          WasOpened = true;
          metricsRegistry.Metrics.Measure.Histogram.Update(metricsRegistry.LambdaOpenDelay, _startTime.ElapsedMilliseconds);
          OnOpen?.Invoke(this);
        }
      }
    }

    Interlocked.Increment(ref openConnectionCount);
    metricsRegistry.Metrics.Measure.Histogram.Update(metricsRegistry.LambdaInstanceOpenConnections, openConnectionCount);

    var connection = new LambdaConnection(request, response, this, channelId, firstConnectionForInstance);

    // Start the response
    // This sends the headers
    // The response will then hang around waiting for the data to be written to it
    response.StatusCode = 200;
    await response.StartAsync();

    // Only make this connection visible if we're not going to immediately use it for a queued request
    if (dispatchMode == AddConnectionDispatchMode.Enqueue)
    {
      Interlocked.Increment(ref internalActualAvailableConnectionCount);
      connectionQueue.Enqueue(connection);
      return new AddConnectionResult
      {
        WasRejected = false,
        CanUseNow = false,
        Connection = connection
      };
    }

    //
    // IMMEDIATE or TENTATIVE DISPATCH - We're going to try to use this right now
    //
    var canUseNow = false;
    lock (requestCountLock)
    {
      // Only allow the immediate dispatch if we have room for it
      if (outstandingRequestCount < maxConcurrentCount)
      {
        // The connection is being immediately used
        // Need to track the outstanding request
        outstandingRequestCount++;
        canUseNow = true;
      }
    }

    if (!canUseNow)
    {
      // We are not allowed to use this right now
      // Enqueue the connection for later use
      Interlocked.Increment(ref internalActualAvailableConnectionCount);
      connectionQueue.Enqueue(connection);
      return new AddConnectionResult
      {
        WasRejected = false,
        CanUseNow = false,
        Connection = connection
      };
    }

    // Add handler to register the decrement of outstanding requests
    if (dispatchMode == AddConnectionDispatchMode.ImmediateDispatch)
    {
      TryGetConnectionWillUse(connection);
    }

    return new AddConnectionResult
    {
      WasRejected = false,
      CanUseNow = true,
      Connection = connection
    };
  }

  public bool TryGetConnection([NotNullWhen(true)] out ILambdaConnection? connection, bool tentative = false)
  {
    connection = null;

    // We do not use a state lock here because we do not care if we get
    // a connection from an instance that was going to close 1 ms later, or 1 ms earlier,
    // as this is the same as getting a connection from an instance that is not closing
    // If we can't get a connection we just try another instance

    if (!IsOpen)
    {
      _logger.LogWarning("Connection requested from Lambda Instance that is not open, LambdaId: {LambdaId}", Id);
      return false;
    }

    // Loop through the connections until we find one that is available
    // Hide the connections that are not supposed to be used
    lock (requestCountLock)
    {
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

        outstandingRequestCount++;

        if (!tentative)
        {
          TryGetConnectionWillUse(dequeuedConnection);
        }

        return true;
      }
    }

    // No available connections
    // This can happen from shutdowns, dispatches when another request finishes, etc
    _logger.LogDebug("TryGetConnection - No available connections for LambdaId: {LambdaId}, AvailableConnectionsCount: {AvailableConnectionCount}, QueueApproximateCount: {QueueApproximateCount}", Id, AvailableConnectionCount, QueueApproximateCount);

    return false;
  }

  /// <summary>
  /// Registers an OnCompleted handler for the response that will
  /// decrement the outstanding request count when the response is closed
  /// and try to dispatch another request if there is one pending
  /// </summary>
  /// <param name="connection"></param>
  public void TryGetConnectionWillUse(ILambdaConnection connection)
  {
    connection.Response.OnCompleted(Task () =>
    {
      var wakeupDispatcher = false;
      lock (requestCountLock)
      {
        outstandingRequestCount--;

        // If we went from busy to non-busy, wakeup the background dispatcher
        if (AvailableConnectionCount > 0)
        {
          wakeupDispatcher = true;
        }
      }

      if (wakeupDispatcher)
      {
        dispatcher.WakeupBackgroundDispatcher(null);
      }

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

  public TransitionResult TransitionToDraining()
  {
    lock (stateLock)
    {
      if (State == LambdaInstanceState.Draining
          || State == LambdaInstanceState.Closing
          || State == LambdaInstanceState.Closed)
      {
        // Already closing
        return new TransitionResult
        {
          TransitionedToDraining = false,
          WasOpened = WasOpened,
          OpenWasRejected = OpenWasRejected
        };
      }

      State = LambdaInstanceState.Draining;
      return new TransitionResult
      {
        TransitionedToDraining = true,
        WasOpened = WasOpened,
        OpenWasRejected = OpenWasRejected
      };
    }
  }

  public bool InvokeCompleted()
  {
    lock (stateLock)
    {
      if (State == LambdaInstanceState.Closed)
      {
        // Already closed
        return false;
      }

      State = LambdaInstanceState.Closed;
      return true;
    }
  }

  /// <summary>
  /// Closes in the background so the Lambda can exit as soon as it sees it's last connection close
  /// </summary>
  public async Task Close(bool doNotReplace = false, bool lambdaInitiated = false, bool openWasRejected = false)
  {
    // Ignore if already closing
    if (!TransitionToDraining().TransitionedToDraining)
    {
      // Already closing
      return;
    }

    if (openWasRejected)
    {
      lock (stateLock)
      {
        // If the open was rejected, we should not consider the instance as having been opened
        // The open is rejected if there are too many instances running vs the desired count
        OpenWasRejected = true;
      }
    }

    DoNotReplace = doNotReplace;
    LamdbdaInitiatedClose = lambdaInitiated;

    // We own the close, so we can replace this instance
    // Invoke close handler on the Instance Manager
    OnCloseInitiated?.Invoke(this);

    if (Replacing)
    {
      // Check if the instances likely hit its deadline rather than being closed for being idle
      if (_startTime.Elapsed > TimeSpan.FromSeconds(10))
      {
        // Wait 10% of the elapsed time or up to 5 seconds for a replacement
        var waitTime = TimeSpan.FromSeconds(Math.Min(_startTime.Elapsed.TotalSeconds * 0.1, 5));
        _logger.LogInformation("Close - Waiting for replacement for LambdaId: {LambdaId}, WaitTime: {WaitTime} ms", Id, (int)waitTime.TotalMilliseconds);
        await Task.Delay(waitTime);
      }
    }

    // Flip all instances from Draining to Closing here
    // We either waited or didn't
    lock (stateLock)
    {
      if (State == LambdaInstanceState.Draining)
      {
        State = LambdaInstanceState.Closing;
      }
    }

    // We do this in the background so the Lambda can exit as soon as the last
    // connection to it is closed
    _ = Task.Run(async () =>
    {
      var releasedConnectionCount = 0;

      // This will typically loop only a few times
      for (int i = 0; i < 60; i++)
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

    // NOTE: Some connections may still be open, but they will be closed when
    // their in flight request is finished
  }

#if false
  /// <summary>
  /// Close all available connections to the Lambda Instance
  /// 
  /// Use a status code that indicates that the connection should not be
  /// re-opened by the Lambda
  /// </summary>
  /// <deprecated>Use Close instead</deprecated>
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
#endif

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
  /// Invoke a Lambda but have it return immediately after cold start
  /// This is used to ensure that we have a ready replacement Lambda exec env
  /// when one of our instances is shutting down (which can take several seconds to
  /// 1 minute for all in-flight requests to finish)
  /// 
  /// This is a "buddy" to the `Start` call that uses the same parameters
  /// </summary>
  /// <returns></returns>
  public void StartInitOnly()
  {
    var initOnlyLambdaId = $"{Id}-initonly";

    var options = new JsonSerializerOptions
    {
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Setup the Lambda payload
    var payload = new WaiterRequest
    {
      PoolId = poolId,
      Id = initOnlyLambdaId,
      DispatcherUrl = getCallbackIP.CallbackUrl,
      NumberOfChannels = 0,
      SentTime = DateTime.Now,
      InitOnly = true
    };

    // Invoke the Lambda
    var request = new InvokeRequest
    {
      FunctionName = functionName,
      InvocationType = InvocationType.RequestResponse,
      Payload = JsonSerializer.Serialize(payload, options),
    };

    // Should not wait here as we will not get a response until the Lambda is done
    var invokeTask = LambdaClient.InvokeAsync(request);

    // Handle completion of the task
    _ = invokeTask.ContinueWith(t =>
    {
      if (t.IsFaulted)
      {
        // Handle any exceptions that occurred during the invocation
        Exception ex = t.Exception;
        _logger.LogError("LambdaInvoke for InitOnly LambdaId: {Id}, gave error: {Message}", initOnlyLambdaId, ex.Message);
      }
      else if (t.IsCompleted)
      {
        // The Lambda invocation has completed
        _logger.LogInformation("LambdaInvoke for InitOnly completed for LambdaId: {Id}", initOnlyLambdaId);
      }
    });
  }

  /// <summary>
  /// Invoke the Lambda, which should cause it to connect back to us
  /// We do not count the connection as available until it connects back to us
  /// Each instance can have many connections at a time to us
  /// </summary>
  /// <returns></returns>
  public void Start()
  {
    _logger.LogInformation("Starting Lambda Instance {Id}", Id);

    metricsRegistry.Metrics.Measure.Counter.Increment(metricsRegistry.LambdaInvokeCount);

    // Throw if the instance is already open or closed
    // There should only be a single call to this ever
    // Haven't even invoked the Lambda yet
    // If state is wrong this was called twice
    // A lock is not a problem here because it's per-instance and this happens
    // only once per instance
    lock (stateLock)
    {
      if (State != LambdaInstanceState.Initial)
      {
        throw new InvalidOperationException("Cannot start a Lambda Instance that is not in the initial state");
      }

      State = LambdaInstanceState.Starting;
    }

    // Setup the Lambda payload
    var payload = new WaiterRequest
    {
      PoolId = poolId,
      Id = Id,
      DispatcherUrl = getCallbackIP.CallbackUrl,
      NumberOfChannels = channelCount == -1 ? 2 * maxConcurrentCount : channelCount,
      SentTime = DateTime.Now,
      InitOnly = false
    };

    // Invoke the Lambda
    var request = new InvokeRequest
    {
      FunctionName = functionName,
      InvocationType = InvocationType.RequestResponse,
      Payload = JsonSerializer.Serialize(payload),
    };

    // Should not wait here as we will not get a response until the Lambda is done
    _startTime.Start();
    var invokeTask = LambdaClient.InvokeAsync(request);

    // Handle completion of the task
    _ = invokeTask.ContinueWith(t =>
     {
       metricsRegistry.Metrics.Measure.Counter.Decrement(metricsRegistry.LambdaInvokeCount);

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
       else
       {
         _tcs.SetException(new Exception("LambdaInvoke for LambdaId: " + this.Id + " did not complete"));
       }
     });

    _ = Task.Run(async () =>
    {
      // Wait a short time before starting the buddy invoke
      // This does not guarantee that the buddy request will arrive after
      // the real request, but it's likely
      await Task.Delay(TimeSpan.FromMilliseconds(100));

      // Start an init-only buddy
      StartInitOnly();
    });
  }

  public void ReenqueueUnusedConnection(ILambdaConnection connection)
  {
    lock (requestCountLock)
    {
      // Decrement the outstanding request count
      outstandingRequestCount--;
    }

    // If the lambda is Closing or Closed then we don't re-enqueue the connection
    // No lock here because we're not changing state
    // and because getting a non-Closed state with a lock
    // then enqueueing the connection is still a race condition
    // that has to be handled in shutdown
    if (State == LambdaInstanceState.Closing
        || State == LambdaInstanceState.Closed)
    {
      _ = connection.Discard();
      return;
    }

    // Increment the available connection count
    Interlocked.Increment(ref internalActualAvailableConnectionCount);

    // Re-enqueue the connection
    connectionQueue.Enqueue(connection);
  }
}