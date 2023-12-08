using Microsoft.AspNetCore.Mvc;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using PwrDrvr.LambdaDispatch.Messages;
using System.Text.Json;
using System.Collections.Concurrent;

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
  /// <summary>
  /// Raised when the Lambda Instance has completed it's invocation
  /// </summary>
  public event Action<LambdaInstance> OnInvocationComplete;

  /// <summary>
  /// Raised when the Lambda Instance has opened
  /// </summary>
  public event Action<LambdaInstance> OnOpen;

  public LambdaInstanceState State { get; private set; } = LambdaInstanceState.Initial;

  public static readonly AmazonLambdaClient LambdaClient = new();

  private readonly int maxConcurrentCount;

  public string Id { get; private set; }

  public TaskCompletionSource TCS { get; private set; } = new TaskCompletionSource();

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

  public int OutstandingRequestCount => maxConcurrentCount - availableConnectionCount;

  public int AvailableConnectionCount => availableConnectionCount;

  public LambdaInstance()
  {
    // _logger = logger;
    Id = Guid.NewGuid().ToString();
  }

  /// <summary>
  /// Called when we get a connection for the Lambda Instance ID
  /// </summary>
  /// <param name="request"></param>
  /// <param name="response"></param>
  public void AddConnection(HttpRequest request, HttpResponse response)
  {
    if (State == LambdaInstanceState.Closing || State == LambdaInstanceState.Closed)
    {
      Console.WriteLine("Connection added to Lambda Instance that is closing or closed - closing with 1001");

      // Close the connection
      try
      {
        response.StatusCode = 1001;
        response.Body.Close();
        request.Body.Close();
      }
      catch (Exception ex)
      {
        Console.WriteLine("Exception closing down connection to Lambda");
        Console.WriteLine(ex.Message);
      }

      return;
    }

    // Signal that we are ready if this the first connection
    if (State == LambdaInstanceState.Starting)
    {
      State = LambdaInstanceState.Open;

      // Signal that we are open
      OnOpen.Invoke(this);
    }

    if (State != LambdaInstanceState.Open)
    {
      throw new InvalidOperationException("Cannot add a connection to a Lambda Instance that is not open");
    }

    var connection = new LambdaConnection(request, response, this);

    Interlocked.Increment(ref availableConnectionCount);

    connectionQueue.Enqueue(connection);
  }

  /// <summary>
  /// Get an available connection from this Lambda Instance
  /// </summary>
  /// <returns></returns>
  public LambdaConnection? TryGetConnection()
  {
    if (State == LambdaInstanceState.Closing || State == LambdaInstanceState.Closed)
    {
      Console.WriteLine("Connection requested from Lambda Instance that is closing or closed");
      return null;
    }

    if (State != LambdaInstanceState.Open)
    {
      throw new InvalidOperationException("Cannot get a connection from a Lambda Instance that is not open");
    }

    // Loop through the connections until we find one that is available
    while (connectionQueue.TryDequeue(out var connection))
    {
      // The connection should only be Closed unexpectedly, not Busy
      if (connection.State == LambdaConnectionState.Busy)
      {
        Interlocked.Decrement(ref availableConnectionCount);
        throw new InvalidOperationException("Connection should not be busy");
      }

      // If the connection is Closed we discard it (can happen on abnormal close during idle)
      if (connection.State == LambdaConnectionState.Closed)
      {
        Interlocked.Decrement(ref availableConnectionCount);
        continue;
      }

      // We found an available connection
      Interlocked.Decrement(ref availableConnectionCount);
      return connection;
    }

    return null;
  }

  /// <summary>
  /// Called when a connection is closed
  /// </summary>
  /// <param name="isBusy"></param>
  public void ConnectionClosed(bool isBusy)
  {
    // If the connection was busy then it was not counted as available

    if (!isBusy && State == LambdaInstanceState.Open)
    {
      // Connection was not busy, so it was available
      Interlocked.Decrement(ref availableConnectionCount);
    }

    // Note: the Lambda itself will connect back if it's still running normally
    // Our invoke should cause maxConcurrentCount connections to be established
  }

  /// <summary>
  /// Close all available connections to the Lambda Instance
  /// 
  /// Use a status code that indicates that the connection should not be
  /// re-opened by the Lambda
  /// </summary>
  public async Task Close()
  {
    // Throw if the instance is already closed
    if (State == LambdaInstanceState.Closed || State == LambdaInstanceState.Closing)
    {
      throw new InvalidOperationException("Cannot close a Lambda Instance that is already closed or closing");
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
        connection.Response.StatusCode = 1001;
        await connection.Response.StartAsync();
        connection.Response.Body.Close();
        await connection.Response.CompleteAsync();
        connection.Request.Body.Close();
      }
      catch (Exception ex)
      {
        Console.WriteLine("Exception closing down connection to Lambda");
        Console.WriteLine(ex.Message);
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
    // Throw if the instance is already open or closed
    if (State != LambdaInstanceState.Initial)
    {
      throw new InvalidOperationException("Cannot start a Lambda Instance that is not in the initial state");
    }

    State = LambdaInstanceState.Starting;

    // Setup the Lambda payload
    var payload = new WaiterRequest
    {
      Id = Id,
      DispatcherUrl = await GetCallbackIP.Get()
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
      this.State = LambdaInstanceState.Closing;

      // NOTE: The Lambda will return via the callback to indicate that it's shutting down
      // but it will linger until we close the responses to it's requests
      // This prevents race conditions on shutdown

      this.OnInvocationComplete.Invoke(this);

      if (t.IsFaulted)
      {
        // Handle any exceptions that occurred during the invocation
        Exception ex = t.Exception;
        Console.WriteLine(ex.Message);
      }
      else if (t.IsCompleted)
      {
        // The Lambda invocation has completed
      }
    });
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
  }
}