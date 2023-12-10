# Remaining Tasks

- [ ] `Dispatcher.RunRequest` treats the response as plain text, which will not work
- [ ] `Dispatcher.RunRequest` may not handle error relay correctly - particularly if some status codes throw
- [ ] The C# Lambda is just sending a dummy static response instead of doing something dynamic
- [ ] When the LambdaInvoke times out it's not recovering gracefully
- [ ] The Router eventually dies because it is allowing an exception to be thrown out of thread pool threads and eventually runs out of thread pool threads
- [ ] Need to add logic to LambdaInstanceManager that will start additional instances based on queue depth
- [ ] Need to add response to Lambda that says it is drain stopping and for the Router to not start that instance again due to underutilization - The Router is free to use the ramp up logic to start a new instance which may actually end up in the same execution environment
- [ ] The router should age out Lambda instances that have not connected back in a while
- [ ] The Lambda should monitor how much time it has left and close the connection before it gets within 60 seconds of the timeout
- [ ] There needs to be a control protocol on the chunked request/response that allows the router to tell a Lambda to close the connection
- [ ] The LambdaInstance should be destroyed after a timeout
- [x] The Router may not invoke the Lambda correctly
- [x] The done instances are not put back in the idle queue
- [x] Lambda disconnects in any state are not handled (should mark the instance as bad)
- [x] The chunked request/response are closed after 1 request - Currrently this means that the LambdaInstance should be destroyed and a new one created after each request
- [x] The router needs to get it's own IP address and port to send to the LambdaInstance
- [x] The router needs to start an additional lambda for each request that it puts in the queue
- [x] The Lambda invoke should tell it how many connections to establish to the router
