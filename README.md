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
* [LambdaTestTool for DotNet 8](https://github.com/aws/aws-lambda-dotnet/tree/master/Tools/LambdaTestTool)
  * `dotnet tool install -g Amazon.Lambda.TestTool-8.0`
  * `dotnet lambda-test-tool-8.0 --help`

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

## Start the Lambda Test Tool

```bash
dotnet-lambda-test-tool-8.0
```

## Build for Deploy as NativeAoT Lambda

```bash
dotnet build -c Release --sc true --arch arm64
```

## Send an HTTP Request to the Router

```bash
curl http://localhost:5002/fact
```

## Deploy the ECR Template

```bash
aws cloudformation create-stack --stack-name lambda-dispatch-ecr --template-body file://ecr.template.yaml

aws cloudformation update-stack --stack-name lambda-dispatch-ecr --template-body file://ecr.template.yaml
```

## Publish the Docker Image - Router

```bash
aws ecr get-login-password --region us-east-2 | docker login --username AWS --password-stdin 220761759939.dkr.ecr.us-east-2.amazonaws.com

docker build --file DockerfileRouter -t lambda-dispatch-router .
docker tag lambda-dispatch-router:latest 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-router:latest
docker push 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-router:latest
```

## Publish the Docker Image - LambdaLB

```bash
aws ecr get-login-password --region us-east-2 | docker login --username AWS --password-stdin 220761759939.dkr.ecr.us-east-2.amazonaws.com

docker build --file DockerfileLambdaLB -t lambda-dispatch-lambdalb .
docker tag lambda-dispatch-lambdalb:latest 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-lambdalb:latest
docker push 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-lambdalb:latest
```

## Deploy the Fargate and Lambda Template

```bash
aws cloudformation create-stack --stack-name lambda-dispatch-fargate --template-body file://fargate.template.yaml --capabilities CAPABILITY_IAM

aws cloudformation update-stack --stack-name lambda-dispatch-fargate --template-body file://fargate.template.yaml --capabilities CAPABILITY_IAM
```

## curl the Router

```
curl http://lambda-ECSFa-99YoLua7GcRe-1054486381.us-east-2.elb.amazonaws.com/fact
```

## Count Lines of Code

```bash
npm i -g cloc
cloc --exclude-dir=bin,obj --exclude-ext=csproj,sln,json,md .
```

## Performance Analysis

Install PerfView to analyze the `.nettrace` files: https://github.com/microsoft/perfview

https://www.speedscope.app/

```bash
dotnet tool install --global dotnet-trace

dotnet-trace collect -p <PID> --providers Microsoft-Windows-DotNETRuntime

dotnet-trace collect -p <PID> --providers Microsoft-DotNETCore-SampleProfiler

dotnet-trace convert --format speedscope trace.nettrace
```

## Memory Profiling with Son of Strike and lldb

```bash
dotnet tool install --global dotnet-sos
dotnet-sos install

lldb process attach --pid <PID>
plugin load /usr/local/share/dotnet/shared/Microsoft.NETCore.App/<version>/libsosplugin.dylib

sudo apt-get install lldb

AWS_LAMBDA_SERVICE_URL=http://host.docker.internal:5051 AWS_ACCESS_KEY_ID=test-access-key-id AWS_SECRET_ACCESS_KEY=test-secret-access-key AWS_SESSION_TOKEN=test-session-token src/PwrDrvr.LambdaDispatch.Router/bin/Release/net8.0/PwrDrvr.LambdaDispatch.Router > router.log 2>&1

AWS_LAMBDA_RUNTIME_API=host.docker.internal:5051 AWS_REGION=us-east-2 AWS_ACCESS_KEY_ID=test-access-key-id AWS_SECRET_ACCESS_KEY=test-secret-access-key AWS_SESSION_TOKEN=test-session-token bin/Release/net8.0/bootstrap > lambdalb.log 2>&1
```