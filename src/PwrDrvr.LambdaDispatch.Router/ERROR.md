# Wireshark Era

- LambdaId: `e9e56095-586f-45e0-97d0-505a93fe7048`
- ChannelId: `7a0ebe6c-535a-408f-b069-f82b4e065c77`
- Timestamp of first exception: `00:56:21.396`
- 

## Connection History from Router

```log
# Lambda Instance Started
00:56:10.932 info: PwrDrvr.LambdaDispatch.Router.LambdaInstanceManager[0]
      LambdaInstance e9e56095-586f-45e0-97d0-505a93fe7048 opened

# Lambda Invoke completes
00:56:21.411 info: PwrDrvr.LambdaDispatch.Router.LambdaInstanceManager[0]
      LambdaInstance e9e56095-586f-45e0-97d0-505a93fe7048 invocation complete, _desiredInstanceCount 4, _runningInstanceCount 0, _startingInstanceCount 3 (after decrement)

# Fail - These timestamps make no sense - The request was receive in the same second as the log message but it says it was 18 seconds ago
00:56:21.460 fail: PwrDrvr.LambdaDispatch.Router.LambdaConnection[0]
      LambdaConnection.RunRequest - Exception - Request was received at 12/17/2023 00:56:21, 18000.459575 seconds ago, LambdaID: e9e56095-586f-45e0-97d0-505a93fe7048, ChannelId: 7a0ebe6c-535a-408f-b069-f82b4e065c77
      System.IO.IOException: The request stream was aborted.
       ---> Microsoft.AspNetCore.Connections.ConnectionAbortedException: The HTTP/2 connection faulted.
       ---> Microsoft.AspNetCore.Connections.ConnectionResetException: Connection reset by peer
       ---> System.Net.Sockets.SocketException (54): Connection reset by peer
         --- End of inner exception stack trace ---
```



## Exception Summary from LambdaLB


## Full Router Exception - First Instance

```log
00:56:21.460 fail: PwrDrvr.LambdaDispatch.Router.LambdaConnection[0]
      LambdaConnection.RunRequest - Exception - Request was received at 12/17/2023 00:56:21, 18000.459575 seconds ago, LambdaID: e9e56095-586f-45e0-97d0-505a93fe7048, ChannelId: 7a0ebe6c-535a-408f-b069-f82b4e065c77
      System.IO.IOException: The request stream was aborted.
       ---> Microsoft.AspNetCore.Connections.ConnectionAbortedException: The HTTP/2 connection faulted.
       ---> Microsoft.AspNetCore.Connections.ConnectionResetException: Connection reset by peer
       ---> System.Net.Sockets.SocketException (54): Connection reset by peer
         --- End of inner exception stack trace ---
         at System.IO.Pipelines.Pipe.GetReadResult(ReadResult& result)
         at System.IO.Pipelines.Pipe.GetReadAsyncResult()
         at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.DuplexPipeStream.ReadAsyncInternal(Memory`1 destination, CancellationToken cancellationToken)
         at System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder`1.StateMachineBox`1.System.Threading.Tasks.Sources.IValueTaskSource<TResult>.GetResult(Int16 token)
         at System.Net.Security.SslStream.EnsureFullTlsFrameAsync[TIOAdapter](CancellationToken cancellationToken, Int32 estimatedSize)
         at System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder`1.StateMachineBox`1.System.Threading.Tasks.Sources.IValueTaskSource<TResult>.GetResult(Int16 token)
         at System.Net.Security.SslStream.ReadAsyncInternal[TIOAdapter](Memory`1 buffer, CancellationToken cancellationToken)
         at System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder`1.StateMachineBox`1.System.Threading.Tasks.Sources.IValueTaskSource<TResult>.GetResult(Int16 token)
         at System.IO.Pipelines.StreamPipeReader.<ReadInternalAsync>g__Core|40_0(StreamPipeReader reader, Nullable`1 minimumSize, CancellationTokenSource tokenSource, CancellationToken cancellationToken)
         at System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder`1.StateMachineBox`1.System.Threading.Tasks.Sources.IValueTaskSource<TResult>.GetResult(Int16 token)
         at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2.Http2Connection.CopyPipeAsync(PipeReader reader, PipeWriter writer)
         at System.IO.Pipelines.Pipe.GetReadResult(ReadResult& result)
         at System.IO.Pipelines.Pipe.GetReadAsyncResult()
         at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2.Http2Connection.ProcessRequestsAsync[TContext](IHttpApplication`1 application)
         --- End of inner exception stack trace ---
         --- End of inner exception stack trace ---
         at System.IO.Pipelines.Pipe.GetReadResult(ReadResult& result)
         at System.IO.Pipelines.Pipe.GetReadAsyncResult()
         at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2.Http2MessageBody.ReadAsync(CancellationToken cancellationToken)
         at System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder`1.StateMachineBox`1.System.Threading.Tasks.Sources.IValueTaskSource<TResult>.GetResult(Int16 token)
         at System.IO.Pipelines.PipeReaderStream.ReadAsyncInternal(Memory`1 buffer, CancellationToken cancellationToken)
         at System.IO.StreamReader.ReadBufferAsync(CancellationToken cancellationToken)
         at System.IO.StreamReader.ReadLineAsyncInternal(CancellationToken cancellationToken)
         at PwrDrvr.LambdaDispatch.Router.LambdaConnection.RunRequest(HttpRequest request, HttpResponse response) in /Users/huntharo/pwrdrvr/lambda-dispatch/src/PwrDrvr.LambdaDispatch.Router/LambdaConnection.cs:line 216
```

## Full LambdaLB Exception from HttpClient - First Instance

```log
00:56:20.912 info: PwrDrvr.LambdaDispatch.LambdaLB.HttpReverseRequester[0]
      => LambdaId: e9e56095-586f-45e0-97d0-505a93fe7048
      Pinged instance: e9e56095-586f-45e0-97d0-505a93fe7048, OK
00:56:21.396 fail: PwrDrvr.LambdaDispatch.LambdaLB.Function[0]
      => LambdaId: e9e56095-586f-45e0-97d0-505a93fe7048 => TaskNumber: 2 => ChannelId: 7a0ebe6c-535a-408f-b069-f82b4e065c77
      HttpRequestException caught
      System.Net.Http.HttpRequestException: The HTTP/2 server sent invalid data on the connection. HTTP/2 error code 'PROTOCOL_ERROR' (0x1). (HttpProtocolError)
       ---> System.Net.Http.HttpProtocolException: The HTTP/2 server sent invalid data on the connection. HTTP/2 error code 'PROTOCOL_ERROR' (0x1). (HttpProtocolError)
         at System.Net.Http.Http2Connection.ThrowRequestAborted(Exception innerException)
         at System.Net.Http.Http2Connection.Http2Stream.TryEnsureHeaders()
         at System.Net.Http.Http2Connection.Http2Stream.ReadResponseHeadersAsync(CancellationToken cancellationToken)
         at System.Net.Http.Http2Connection.SendAsync(HttpRequestMessage request, Boolean async, CancellationToken cancellationToken)
         at System.Net.Http.Http2Connection.SendAsync(HttpRequestMessage request, Boolean async, CancellationToken cancellationToken)
         at System.Net.Http.Http2Connection.SendAsync(HttpRequestMessage request, Boolean async, CancellationToken cancellationToken)
         at System.Net.Http.Http2Connection.SendAsync(HttpRequestMessage request, Boolean async, CancellationToken cancellationToken)
         at System.Net.Http.Http2Connection.SendAsync(HttpRequestMessage request, Boolean async, CancellationToken cancellationToken)
         at System.Net.Http.Http2Connection.SendAsync(HttpRequestMessage request, Boolean async, CancellationToken cancellationToken)
         --- End of inner exception stack trace ---
         at System.Net.Http.Http2Connection.SendAsync(HttpRequestMessage request, Boolean async, CancellationToken cancellationToken)
         at System.Net.Http.HttpConnectionPool.SendWithVersionDetectionAndRetryAsync(HttpRequestMessage request, Boolean async, Boolean doRequestAuth, CancellationToken cancellationToken)
         at System.Net.Http.RedirectHandler.SendAsync(HttpRequestMessage request, Boolean async, CancellationToken cancellationToken)
         at System.Net.Http.HttpClient.<SendAsync>g__Core|83_0(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationTokenSource cts, Boolean disposeCts, CancellationTokenSource pendingRequestsCts, CancellationToken originalCancellationToken)
         at PwrDrvr.LambdaDispatch.LambdaLB.HttpReverseRequester.GetRequest(String channelId) in /Users/huntharo/pwrdrvr/lambda-dispatch/src/PwrDrvr.LambdaDispatch.LambdaLB/HttpReverseRequester.cs:line 157
         at PwrDrvr.LambdaDispatch.LambdaLB.Function.<>c__DisplayClass3_1.<<FunctionHandler>b__1>d.MoveNext() in /Users/huntharo/pwrdrvr/lambda-dispatch/src/PwrDrvr.LambdaDispatch.LambdaLB/Function.cs:line 137
00:56:21.396 fail: PwrDrvr.LambdaDispatch.LambdaLB.Function[0]
```

# Pre-Wireshark

LambdaId: be8dc363-f20f-470f-a324-f6885d544fbe
Channel: f349bf53-33b7-44bd-94f6-e7045de4963d

## Fixes Needed
- [ ] Router waits 2 minutes to read the request before it errors out on line 216 in RunRequest, need to make sure that the request body is closed out properly in LambdaLB on HttpRequestException

## Router Error Line - Fires Later After Timeout

src/PwrDrvr.LambdaDispatch.Router/LambdaConnection.cs:line 216
// First line should be status
line = await lambdaResponseReader.ReadLineAsync();

## Lambda invoked at 17:29:25.797
lambda-dispatch-router-1    | 17:29:25.797 info: PwrDrvr.LambdaDispatch.Router.LambdaInstance[0]
lambda-dispatch-router-1    |       Starting Lambda Instance be8dc363-f20f-470f-a324-f6885d544fbe

## Lambda born at 17:29:26.032
lambda-dispatch-lambdalb-1  | 17:29:26.032 info: PwrDrvr.LambdaDispatch.LambdaLB.Function[0]
lambda-dispatch-lambdalb-1  |       Received WaiterRequest id: be8dc363-f20f-470f-a324-f6885d544fbe, dispatcherUrl: http://router:5003/api/chunked

## Router notices Lambda's first connection at 17:29:26.105
lambda-dispatch-router-1    | 17:29:26.105 info: PwrDrvr.LambdaDispatch.Router.LambdaInstanceManager[0]
lambda-dispatch-router-1    |       LambdaInstance be8dc363-f20f-470f-a324-f6885d544fbe opened
lambda-dispatch-router-1    | 17:29:26.106 warn: PwrDrvr.LambdaDispatch.Router.Dispatcher[0] Dispatching (foreground) pending request that has been waiting for 5477.3411 ms, LambdaId: be8dc363-f20f-470f-a324-f6885d544fbe, ChannelId: 04bf96f2-358f-4bdc-beca-fe0e1778da40

## Lambda exception happens at 17:29:28.918
lambda-dispatch-lambdalb-1  | 17:29:28.918 fail: PwrDrvr.LambdaDispatch.LambdaLB.HttpReverseRequester[0]
lambda-dispatch-lambdalb-1  |       => LambdaId: be8dc363-f20f-470f-a324-f6885d544fbe => TaskNumber: 4 => ChannelId: f349bf53-33b7-44bd-94f6-e7045de4963d
lambda-dispatch-lambdalb-1  |       Error reading request from response
lambda-dispatch-lambdalb-1  |       System.Net.Http.HttpProtocolException: The HTTP/2 server sent invalid data on the connection. HTTP/2 error code 'PROTOCOL_ERROR' (0x1). (HttpProtocolError)

## Lambda exception bubbles up at 17:29:28.923
lambda-dispatch-lambdalb-1  | 17:29:28.923 fail: PwrDrvr.LambdaDispatch.LambdaLB.Function[0]
lambda-dispatch-lambdalb-1  |       => LambdaId: be8dc363-f20f-470f-a324-f6885d544fbe => TaskNumber: 4 => ChannelId: f349bf53-33b7-44bd-94f6-e7045de4963d
lambda-dispatch-lambdalb-1  |       Exception caught in task
lambda-dispatch-lambdalb-1  |       System.Net.Http.HttpProtocolException: The HTTP/2 server sent invalid data on the connection. HTTP/2 error code 'PROTOCOL_ERROR' (0x1). (HttpProtocolError)

## Lambda returns at 17:29:28.925
lambda-dispatch-lambdalb-1  | 17:29:28.925 info: PwrDrvr.LambdaDispatch.LambdaLB.Function[0]
lambda-dispatch-lambdalb-1  |       => LambdaId: be8dc363-f20f-470f-a324-f6885d544fbe
lambda-dispatch-lambdalb-1  |       Responding with WaiterResponse id: be8dc363-f20f-470f-a324-f6885d544fbe

## Router knows the Lambda has returned at 17:29:28.933
lambda-dispatch-router-1    | 17:29:28.933 info: PwrDrvr.LambdaDispatch.Router.LambdaInstanceManager[0]
lambda-dispatch-router-1    |       LambdaInstance be8dc363-f20f-470f-a324-f6885d544fbe invocation complete, _desiredInstanceCount 4, _runningInstanceCount 1, _startingInstanceCount 2 (after decrement)

## Router notices the connection is reset at 17:30:19.021
lambda-dispatch-router-1    | 17:30:19.021 fail: PwrDrvr.LambdaDispatch.Router.LambdaConnection[0]
lambda-dispatch-router-1    |       LambdaConnection.RunRequest - Exception - Request was received at 12/16/2023 17:29:28, 51.0210471 seconds ago, LambdaID: be8dc363-f20f-470f-a324-f6885d544fbe, ChannelId: f349bf53-33b7-44bd-94f6-e7045de4963d
lambda-dispatch-router-1    |       System.IO.IOException: The request stream was aborted.
lambda-dispatch-router-1    |        ---> Microsoft.AspNetCore.Connections.ConnectionAbortedException: The HTTP/2 connection faulted.
lambda-dispatch-router-1    |        ---> Microsoft.AspNetCore.Connections.ConnectionResetException: Connection reset by peer
