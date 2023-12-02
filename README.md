# Overview

This performs reverse routing with Lambda functions, where the Lambda functions continue running and call back to the router to pickup requests.  This allows explicit control and determination of the number of available execution environments for the Lambda functions, allowing for a more predictable and consistent performance profile by mostly avoiding requests waiting for cold starts.

Additionally, when there are  more parallel requests than expected execution environments, a queue is formed while additional execution environments are spun up.  Requests are dispatched from the front of the queue to the next available execution environment.  This differs substantially from Lambda's built-in dispatch which will allocate a request to a new execution environment, wait for the cold start (even if several seconds) and then dispatch the request on that new execution environment even if there are already idle execution environments available.

# Packaging DotNet 8 for Lambda

https://coderjony.com/blogs/running-net-8-lambda-functions-on-aws-using-custom-runtime-and-lambda-internals-for-net-developers?sc_channel=sm&sc_campaign=Developer_Campaigns&sc_publisher=TWITTER&sc_geo=GLOBAL&sc_outcome=awareness&trk=Developer_Campaigns&linkId=250770379

# Testing Lambda Locally

https://github.com/aws/aws-lambda-dotnet/tree/master/Tools/LambdaTestTool

# Developer Setup

## Prerequisites

* [AWS CLI](https://aws.amazon.com/cli/)
* [DotNet 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

## Install Lambda Templates

```bash
dotnet new -i Amazon.Lambda.Templates
```

## Building

```bash
dotnet build
```

## Running Locally

```bash
dotnet run --project PwrDrvr.LambdaDispatch.Router
```

## Running Unit Tests

```bash
dotnet test
```
