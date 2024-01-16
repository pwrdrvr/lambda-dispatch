ChannelId: channel-39fee888-8024-4913-92f1-b5eba7ff9ae3 - Error reading from res_stream: Some(hyper::Error(Body, Error { kind: Reset(StreamId(137), INTERNAL_ERROR, Remote) }))
Connection failed: hyper::Error(User(BodyWriteAborted), NotEof(2202744))
ChannelId: channel-39fee888-8024-4913-92f1-b5eba7ff9ae3 - Error reading from app_res_stream: Some(hyper::Error(Body, "connection error"))
ChannelId: channel-5e1f1d8b-4402-43c1-bbef-b4e6adbec63a - Error reading from res_stream: Some(hyper::Error(Body, Error { kind: Reset(StreamId(139), INTERNAL_ERROR, Remote) }))
Connection failed: hyper::Error(User(BodyWriteAborted), NotEof(1002616))
thread 'main' panicked at extension/src/run.rs:229:23:
Connection ready check threw error - connection has disconnected, should reconnect