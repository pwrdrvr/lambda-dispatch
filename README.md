# Lambda Dispatch

**Stop paying for idle Lambda containers. Keep the warm ones busy.**

Lambda Dispatch is a game-changing solution that maximizes your AWS Lambda performance while minimizing costs. It intelligently routes requests to already-warm Lambda containers, dramatically reducing cold starts and ensuring optimal resource utilization.

## The Problem: Cold Starts for Web Requests

Web applications can have lengthy cold start times, and users waiting for those cold starts can increase bounce and frustration rates, making it difficult to choose AWS Lambda for hosting web applications.

Watch how Lambda Dispatch solves this problem:

[![AWS Lambda Cold Starts for Web Requests](https://github.com/pwrdrvr/lambda-dispatch/assets/5617868/80089108-7cc0-4cb5-ab87-08c8282094bf)](https://www.youtube.com/watch?v=2dW1mSFCbdM)

## Key Benefits

- 🚀 **Eliminate Cold Starts** - Route requests to warm containers first
- 💰 **Reduce Costs** - Up to 80% cost reduction with similar throughput
- 🎯 **Optimal Performance** - Maintain consistent response times under varying loads
- 🔄 **Smart Scaling** - Automatically scales containers based on actual demand
- 📊 **Better Resource Utilization** - Handle multiple concurrent requests per container

## How It Works

Lambda Dispatch introduces a new paradigm in Lambda container management:

![Request Distribution Comparison](docs/request-distribution-comparison.png)

Instead of AWS's default round-robin distribution that can leave some containers idle while others are overwhelmed, Lambda Dispatch intelligently routes requests to ensure optimal utilization of your Lambda containers.

### Performance Impact

Our tests show significant improvements in both steady-state and scale-up scenarios:

**Steady State Performance:**
![Steady State Performance](docs/perf-lambdadispatch-steady.jpg)

**Scale-up Performance:**
![Scale-up Performance](docs/perf-lambdadispatch-scaleup.jpg)

## Quick Start

1. Install the Lambda Dispatch CDK construct:
```bash
npm install @pwrdrvr/lambda-dispatch-construct
# or
yarn add @pwrdrvr/lambda-dispatch-construct
```

2. Add it to your CDK stack:
```typescript
import { LambdaDispatch } from '@pwrdrvr/lambda-dispatch-construct';

// Create the Lambda Dispatch construct
const dispatch = new LambdaDispatch(this, 'LambdaDispatch', {
  vpc,
  lambdaFunction: yourExistingFunction,
});
```

3. Deploy and watch your Lambda performance improve!

## Project Status

Consider this a `0.9` release as of 2024-01-01. This has been tested with billions of requests using `hey` but has not yet been tested with a production load.

This can be tested for production loads and can be carefully monitored in a production environment with a portion of traffic.

## Documentation

- [Technical Details](TECHNICAL.md) - Detailed technical information, configuration options, and implementation details
- [Development Guide](DEVELOPMENT.md) - Guide for building and running the project locally
- [Performance Analysis](PERFORMANCE.md) - Detailed performance testing results

## Origin Story

It all started with a tweet: https://x.com/huntharo/status/1527256565941673984?s=20

![Tweet describing the problem and proposed solution](docs/lambda-dispatch-tweet-2022-05-19.jpg)

The desire was to enable easily migrating an existing Next.js web application with a nominal response time of 100 ms and a cold start time of 8 seconds to Lambda. The problem is that the cold start time is 80x the response time, so any burst of traffic will potentially cause a large number of requests to wait for the cold start time.

## Similar Projects

- [AWS Lambda Web Adapter](https://github.com/awslabs/aws-lambda-web-adapter)
- [serverless-adapter](https://github.com/H4ad/serverless-adapter)
- [serverless-express](https://github.com/CodeGenieApp/serverless-express)

## License

This project is licensed under the MIT License - see the LICENSE file for details.
