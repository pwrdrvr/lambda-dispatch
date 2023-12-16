LambdaId: be8dc363-f20f-470f-a324-f6885d544fbe
Channel: f349bf53-33b7-44bd-94f6-e7045de4963d

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

