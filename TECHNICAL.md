# Technical Details

## Implementation Details

The project was initially built in DotNet 8 with C# for both the Router and the Lambda Extension, but the Lambda Extension was later rewritten in Rust using Hyper and the Tokio async runtime to resolve a high CPU usage issue. However, the high CPU usage issue was not resolved directly by the Rust rewrite; instead both the router and extension needed to be restricted to use only 1 worker thread to avoid the high CPU usage; the problem of too much CPU usage was common to the DotNet Router, DotNet Extension, and the Rust Extension.

The structure of the extension is similar to the [AWS Lambda Web Adapter](https://github.com/awslabs/aws-lambda-web-adapter) in that the extension connects to that application on port 3000, and waits for a `/health` route (which can perform cold start logic) to return a 200 before connecting back to the Router over HTTP2 to pickup requests.

## Technical Advantages

- Avoids cold start wait durations in most cases where at least 1 exec env is running
  - Caveat: if the number of queued requests (Q), divided by the total concurrent request capacity available (C), multiplied by the avg response time (t) is greater than the cold start time (T), then some requests will have to wait for the same duration as a cold start, `Q/C*t >= T`, example: Q = 100 queued requests, C = 10 request concurrent capacity, t = 1 second avg response time, T = 10 second cold start time, 100/10*1 = 10, 10 seconds of waiting for some requests
  - Completely eliminates the blocking issue preventing many web apps from using Lambda, which is that an increase in total concurrent requests will cause a large portion of requests to wait for an entire cold start duration when other exec envs are available shortly after the request is received
- Avoids `base64` encoding and decoding of requests and responses in both the Lambda function itself (where it costs CPU time) and in the API Gateway/ALB/Function URL (where it costs response time)
- Allows sending first / streaming bytes of responses all the way to the client without waiting for entire response to be buffered
  - For large responses this better utilizes the available bandwidth to the client and reduces the time to first byte
- Eliminates the request / response body size limitations of API Gateway/ALB/Function URLs
- Allows each exec env to handle multiple requests concurrently
  - Eliminates "paying to wait" for I/O bound requests
  - Allows increasing the CPU available to each exec env

## Configuration

### Router Configuration

The router is configured with environment variables.

| Name                                              | Description                                                                                                                                                             | Default                 |
| ------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------- |
| `LAMBDA_DISPATCH_MaxWorkerThreads`                | The maximum number of worker threads to use for processing requests. For best efficiency, set this to `1` and scale up router instances at ~50-70% CPU usage of 1 core. | default DotNet handling |
| `LAMBDA_DISPATCH_ChannelCount`                    | The number of channels that the Lambda extension should create back to the router                                                                                       | 20                      |
| `LAMBDA_DISPATCH_MaxConcurrentCount`              | The maximum number of concurrent requests that the Lambda extension should allow to be processed                                                                        | 10                      |
| `LAMBDA_DISPATCH_AllowInsecureControlChannel`     | Opens a non-TLS HTTP2 port                                                                                                                                              | false                   |
| `LAMBDA_DISPATCH_PreferredControlChannelScheme`   | The scheme to use for the control channel<br>- `http` - Use HTTP<br>- `https` - Use HTTPS                                                                               | `https`                 |
| `LAMBDA_DISPATCH_IncomingRequestHTTPPort`         | The port to listen for incoming requests. This is the port contacted by the ALB.                                                                                        | 5001                    |
| `LAMBDA_DISPATCH_IncomingRequestHTTPSPort`        | The port to listen for incoming requests. This is the port contacted by the ALB.                                                                                        | 5002                    |
| `LAMBDA_DISPATCH_ControlChannelInsecureHTTP2Port` | The non-TLS port to listen for incoming control channel requests. This is the port contacted by the Lambda extension.                                                   | 5003                    |
| `LAMBDA_DISPATCH_ControlChannelHTTP2Port`         | The TLS port to listen for incoming control channel requests. This is the port contacted by the Lambda extension.                                                       | 5004                    |
| `LAMBDA_DISPATCH_InstanceCountMultiplier`         | Divides the MaxConcurrentCount to setup a TargetConcurrentCount, leaving additional connections to more quickly pickup the next request or to handle bursts of traffic. | 2                       |
| `LAMBDA_DISPATCH_EnvVarForCallbackIp`             | The name of the environment variable that will contain the IP that the Lambda extension will use to callback to the current instance of the router.                     | `K8S_POD_IP`            |
| `LAMBDA_DISPATCH_ScalingAlgorithm`                | The algorithm to use for scaling the number of instances of the router                                                                                                  | `simple`                |
| `LAMBDA_DISPATCH_CloudWatchMetricsEnabled`        | Enables sending metrics to CloudWatch                                                                                                                                   | `false`                 |

### Lambda Extension Configuration

The extension is configured with environment variables.

| Name                               | Description                                 | Default          |
| ---------------------------------- | ------------------------------------------- | ---------------- |
| LAMBDA_DISPATCH_RUNTIME            | The runtime to use for the Lambda dispatch  | `current_thread` |
| LAMBDA_DISPATCH_ENABLE_COMPRESSION | Enables gzip compression of response bodies | `true`           |
| LAMBDA_DISPATCH_PORT               | Port that the contained app is listening on | 3001             |
| LAMBDA_DISPATCH_ASYNC_INIT         | Allows async initialization                 | `false`          |

## Docker Images

The docker images are published to the AWS ECR Public Gallery:

- [Lambda Dispatch Router](https://gallery.ecr.aws/pwrdrvr/lambda-dispatch-router)
  - Latest: `public.ecr.aws/pwrdrvr/lambda-dispatch-router:main`
  - Available for both ARM64 and AMD64
- [Lambda Dispatch Extension](https://gallery.ecr.aws/pwrdrvr/lambda-dispatch-extension)
  - Latest: `public.ecr.aws/pwrdrvr/lambda-dispatch-extension:main`
  - Available for both ARM64 and AMD64
- [Lambda Dispatch Demo App](https://gallery.ecr.aws/pwrdrvr/lambda-dispatch-demo-app)
  - Latest: `public.ecr.aws/pwrdrvr/lambda-dispatch-demo-app:main`
  - Available for both ARM64 and AMD64

## AWS Bills / Cost Risks

- Your AWS bill is your own!
- This project is not responsible for any AWS charges you incur
- Contributors to this project are not responsible for any AWS charges you incur
- Institute monitoring and alerting on Lambda costs and ECS Fargate costs to detect any potential runaway invokes immediately
