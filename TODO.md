# Remaining Tasks

- [ ] `Dispatcher.RunRequest` treats the response as plain text, which will not work
- [ ] `Dispatcher.RunRequest` may not handle error relay correctly - particularly if some status codes throw
- [ ] The C# Lambda is just sending a dummy static response instead of doing something dynamic
- [ ] The Router may not invoke the Lambda correctly
- [ ] The done instances are not put back in the idle queue
- [ ] Lambda disconnects in any state are not handled (should mark the instance as bad)
- [ ] The chunked request/response are closed after 1 request - Currrently this means that the LambdaInstance should be destroyed and a new one created after each request
- [ ] The LambdaInstance should be destroyed after a timeout
- [ ] The router needs to get it's own IP address and port to send to the LambdaInstance
- [ ] The router needs to start an additional lambda for each request that it puts in the queue
- [ ] There needs to be a control protocol on the chunked request/response that allows the router to tell a Lambda to close the connection
- [ ] The Lambda invoke should tell it how many connections to establish to the router
- [ ] The router should age out Lambda instances that have not connected back in a while
- [ ] The Lambda should monitor how much time it has left and close the connection before it gets within 60 seconds of the timeout