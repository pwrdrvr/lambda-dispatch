//
// This is the entry point when deploying the app as a direct-invoked Lambda fronted by an ALB, API Gateway, or Function URL
//

import { handler, performInit } from "./serverlessloader.cjs";
export { handler };

await performInit();

// Sample AWS ALB event
const event = {
  httpMethod: "GET",
  path: "/cats",
  headers: {
    accept:
      "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8",
    "accept-encoding": "gzip, deflate, lzma, sdch, br",
    "accept-language": "en-US,en;q=0.8",
    "cloudfront-forwarded-proto": "https",
    "cloudfront-is-desktop-viewer": "true",
    "cloudfront-is-mobile-viewer": "false",
    "cloudfront-is-smarttv-viewer": "false",
    "cloudfront-viewer-country": "US",
    host: "host.example.com",
    "upgrade-insecure-requests": "1",
    "user-agent": "Custom User Agent String",
    via: "1.1 08f323deadbeefa7af34d5feb414ce27.cloudfront.net (CloudFront)",
    "x-amz-cf-id": "cDehVQoZnx43VYQb9j2-nvCh-9z396Uhbp027Y2JvkCPNLmGJHqlaA==",
    "x-forwarded-for": "127.0.0.1, 127.0.0.2",
    "x-forwarded-port": "443",
    "x-forwarded-proto": "https",
  },
  multiValueHeaders: {},
  queryStringParameters: {},
  multiValueQueryStringParameters: {},
  requestContext: {
    elb: {
      targetGroupArn:
        "arn:aws:elasticloadbalancing:us-east-1:123456789012:targetgroup/my-target-group/6d0ecf831eec9f09",
    },
  },
  isBase64Encoded: false,
};

// Sample AWS Lambda context
const context = {
  callbackWaitsForEmptyEventLoop: false,
  functionVersion: "1",
  functionName: "myLambdaFunction",
  memoryLimitInMB: "128",
  logGroupName: "/aws/lambda/myLambdaFunction",
  logStreamName: "2015/09/22/[HEAD]13370a84ca4ed8b77c427af260",
  invokedFunctionArn:
    "arn:aws:lambda:us-east-1:123456789012:function:myLambdaFunction",
  awsRequestId: "c6af9ac6-7b61-11e6-9a41-93e8deadbeef",
};

// Only invoke the handler if this script is run from the command line
if (process.env.TEST_MODE === "true") {
  handler(event, context);
}
