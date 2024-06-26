

# Testing Lambda Locally

https://github.com/aws/aws-lambda-dotnet/tree/master/Tools/LambdaTestTool

# Developer Setup

## Prerequisites

* [AWS CLI](https://aws.amazon.com/cli/)
* [DotNet 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
* [LambdaTestTool for DotNet 8](https://github.com/aws/aws-lambda-dotnet/tree/master/Tools/LambdaTestTool)
  * `dotnet tool install -g Amazon.Lambda.TestTool-8.0`
  * `dotnet lambda-test-tool-8.0 --help`
* [Rust](https://www.rust-lang.org/tools/install)

## Install Lambda Templates

```sh
dotnet new -i Amazon.Lambda.Templates
```

## Building and Running Unit Tests

### Router - DotNet

```sh
# Build
dotnet build

# Run Unit Tests without Coverage Report
dotnet test

# One Time Install of ReportGenerator
dotnet tool install --global dotnet-reportgenerator-globaltool

# Run Unit Tests with HTML Coverage Report
dotnet test --collect:"XPlat Code Coverage" && reportgenerator "-reports:test/coverage/projects/PwrDrvr.LambdaDispatch.Router.Tests/coverage.opencover.xml" "-targetdir:test/coverage/html_report" -reporttypes:Html
```

## Extension - Rust

```sh
# Build
cargo build

# Run Unit Tests
cargo test

# One Time Install of llvm-cov
cargo install cargo-llvm-cov

# Run Unit Tests with Coverage Report
cargo llvm-cov --all-features --workspace --html
```

## Running Locally

See [Development Current Commands](./DEVELOPMENT-CURRENT-COMMANDS.md) for command lines to bring everything up locally.


# Appendix

## Packaging DotNet 8 for Lambda

https://coderjony.com/blogs/running-net-8-lambda-functions-on-aws-using-custom-runtime-and-lambda-internals-for-net-developers?sc_channel=sm&sc_campaign=Developer_Campaigns&sc_publisher=TWITTER&sc_geo=GLOBAL&sc_outcome=awareness&trk=Developer_Campaigns&linkId=250770379

## Rust Unit Test Notes

- https://github.com/mozilla/grcov
  - Runs into a problem with zip not being supported by the toolchain
- https://github.com/taiki-e/cargo-llvm-cov
  - 825 stars on github
  - Can be run in CI
- https://github.com/lee-orr/rusty-dev-containers
  - `ghcr.io/lee-orr/rusty-dev-containers/cargo-llvm-cov:0`

```sh
# Need the Toolchain to build llvm-cov
rustup toolchain install stable-aarch64-unknown-linux-gnu

# Install llvm-cov from source
cargo +stable install cargo-llvm-cov --locked

# Run the tests
cargo llvm-cov --all-features --workspace --html
```

## Running Unit Tests

https://medium.com/@nocgod/how-to-setup-your-dotnet-project-with-a-test-coverage-reporting-6ff1903f7240

```sh
dotnet test

# DotNet Coverage Report with HTML
dotnet test --collect:"XPlat Code Coverage"

# Convert DotNet Coverage Report to HTML
reportgenerator "-reports:/Users/huntharo/pwrdrvr/lambda-dispatch/test/coverage/projects/PwrDrvr.LambdaDispatch.Router.Tests/coverage.opencover.xml" "-targetdir:/Users/huntharo/pwrdrvr/lambda-dispatch/test/coverage/html_report" -reporttypes:Html

dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=lcov /p:CoverletOutput=./lcov.info /p:Include="[src.PwrDrvr.LambdaDispatch.Router.PwrDrvr.LambdaDispatch.Router]*"

dotnet tool install --global dotnet-reportgenerator-globaltool

reportgenerator "-reports:test/PwrDrvr.LambdaDispatch.Router.Tests/lcov.info" "-targetdir:coveragereport" -reporttypes:Html

dotnet test --filter FullyQualifiedName~PwrDrvr.LambdaDispatch.Router.Tests.PoolOptionsTests
```

## Start the Lambda Test Tool

```sh
dotnet-lambda-test-tool-8.0
```

## Build for Deploy as NativeAoT Lambda

```sh
dotnet build -c Release --sc true --arch arm64
```

## Send an HTTP Request to the Router

```sh
curl http://localhost:5001/fact
```

## Deploy the ECR Template

```sh
aws cloudformation create-stack --stack-name lambda-dispatch-ecr --template-body file://ecr.template.yaml

aws cloudformation update-stack --stack-name lambda-dispatch-ecr --template-body file://ecr.template.yaml
```

## Deploy the Public ECR Template to US East 1

```sh
aws cloudformation create-stack --stack-name lambda-dispatch-ecr-public --template-body file://ecr.public.template.yaml --region us-east-1

aws cloudformation update-stack --stack-name lambda-dispatch-ecr-public --template-body file://ecr.public.template.yaml --region us-east-1
```

## Publish the Docker Image - Router

```sh
aws ecr get-login-password --region us-east-2 | docker login --username AWS --password-stdin 220761759939.dkr.ecr.us-east-2.amazonaws.com

docker build --build-arg GIT_HASH=$(git rev-parse --short HEAD) --build-arg BUILD_TIME="$(date)" --file DockerfileRouter -t lambda-dispatch-router . && \
docker tag lambda-dispatch-router:latest 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-router:latest && \
docker push 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-router:latest
```

## Publish the Docker Image - Lambda Demo App from Public Image

```sh
docker pull public.ecr.aws/pwrdrvr/lambda-dispatch-demo-app:main && \
docker tag public.ecr.aws/pwrdrvr/lambda-dispatch-demo-app:main 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-demo-app:latest && \
docker push 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-demo-app:latest
```

## Publish the Docker Image - Lambda Demo App from Local Code

```sh
docker build --file DockerfileExtension -t lambda-dispatch-extension . && \
docker build --file DockerfileLambdaDemoApp -t lambda-dispatch-demo-app . && \
docker tag lambda-dispatch-demo-app:latest 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-demo-app:latest && \
docker push 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-demo-app:latest
```

## Publish the Docker Image - DirectLambda

```sh
docker build --file DockerfileDirectLambda -t lambda-dispatch-directlambda . && \
docker tag lambda-dispatch-directlambda:latest 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-directlambda:latest && \
docker push 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-directlambda:latest
```

## Deploy the Fargate and Lambda Template

```sh
aws cloudformation create-stack --stack-name lambda-dispatch-fargate --template-body file://fargate.template.yaml --capabilities CAPABILITY_IAM

aws cloudformation update-stack --stack-name lambda-dispatch-fargate --template-body file://fargate.template.yaml --capabilities CAPABILITY_IAM
```

## Enable or Disable ECS Application Auto Scaling

```sh
aws application-autoscaling describe-scalable-targets --service-namespace ecs

aws application-autoscaling register-scalable-target --service-namespace ecs --resource-id service/lambda-dispatch-fargate-ECSCluster-JBQe7CKkf78S/lambda-dispatch-fargate-ECSFargateService-N3d0hinPR3Ps --scalable-dimension ecs:service:DesiredCount --suspended-state file://config.json

aws application-autoscaling register-scalable-target --service-namespace ecs --resource-id service/lambda-dispatch-fargate-ECSCluster-JBQe7CKkf78S/lambda-dispatch-fargate-ECSFargateService-N3d0hinPR3Ps --scalable-dimension ecs:service:DesiredCount --min-capacity 1 --max-capacity 1
```

## curl the Router

```
curl http://lambda-ECSFa-99YoLua7GcRe-1054486381.us-east-2.elb.amazonaws.com/fact
```

## Count Lines of Code

```sh
npm i -g cloc
cloc --exclude-dir=bin,obj,captures,node_modules,dist --exclude-ext=csproj,sln,json,md,log,pcapng .
```

## Performance Analysis

Install PerfView to analyze the `.nettrace` files: https://github.com/microsoft/perfview

https://www.speedscope.app/

```sh
dotnet tool install --global dotnet-trace

dotnet-trace collect -p <PID> --providers Microsoft-Windows-DotNETRuntime

dotnet-trace collect -p <PID> --providers Microsoft-DotNETCore-SampleProfiler

dotnet-trace convert --format speedscope trace.nettrace
```

## Memory Profiling with Son of Strike and lldb

```sh
dotnet tool install --global dotnet-sos
dotnet-sos install

dotnet tool install --global dotnet-dump
dotnet-dump collect -p 39725

dotnet tool install --global dotnet-gcdump
dotnet-gcdump collect -p 39725


# https://learn.microsoft.com/en-us/dotnet/core/diagnostics/sos-debugging-extension
# https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-dump
lldb process attach pid -p <PID>
plugin load /usr/local/share/dotnet/shared/Microsoft.NETCore.App/<version>/libsosplugin.dylib
plugin load /Users/huntharo/.dotnet/tools/.store/dotnet-sos/8.0.452401/dotnet-sos/8.0.452401/tools/net6.0/any/osx-arm64/libsosplugin.dylib 

## Commands
~sosCommand (null)
~sosCommand (null)
~ExtensionCommand analyzeoom
~sosCommand bpmd
~ExtensionCommand assemblies
~ExtensionCommand clrmodules
~sosCommand ClrStack
~sosCommand Threads
~sosCommand u
~ExtensionCommand crashinfo
~sosCommand dbgout
~sosCommand DumpALC
~sosCommand DumpArray
~ExtensionCommand dumpasync
~sosCommand DumpAssembly
~sosCommand DumpClass
~sosCommand DumpDelegate
~sosCommand DumpDomain
~sosCommand DumpGCData
~ExtensionCommand dumpheap
~sosCommand DumpIL
~sosCommand DumpLog
~sosCommand DumpMD
~sosCommand DumpModule
~sosCommand DumpMT
~sosCommand DumpObj
~ExtensionCommand dumpruntimetypes
~sosCommand DumpSig
~sosCommand DumpSigElem
~sosCommand DumpStack
~ExtensionCommand dumpstackobjects
~ExtensionCommand dso
~sosCommand DumpVC
~ExtensionCommand eeheap
~sosCommand EEStack
~sosCommand EEVersion
~sosCommand EHInfo
~ExtensionCommand finalizequeue
~sosCommand FindAppDomain
~sosCommand FindRoots
~sosCommand GCHandles
~ExtensionCommand gcheapstat
~sosCommand GCInfo
~ExtensionCommand gcroot
~ExtensionCommand gcwhere
~sosCommand HistClear
~sosCommand HistInit
~sosCommand HistObj
~sosCommand HistObjFind
~sosCommand HistRoot
~sosCommand HistStats
~sosCommand IP2MD
~ExtensionCommand listnearobj
~ExtensionCommand loadsymbols
~ExtensionCommand logging
~sosCommand Name2EE
~ExtensionCommand objsize
~ExtensionCommand pathto
~sosCommand PrintException
~sosCommand PrintException
~sosCommand runtimes
~sosCommand StopOnCatch
~sosCommand SetClrPath
~ExtensionCommand setsymbolserver
~sosCommand Help
~sosCommand SOSStatus
~sosCommand SOSFlush
~sosCommand SyncBlk
~ExtensionCommand threadpool
~sosCommand ThreadState
~sosCommand token2ee
~ExtensionCommand verifyheap
~ExtensionCommand verifyobj
~ExtensionCommand traverseheap

sudo apt-get install lldb

AWS_LAMBDA_SERVICE_URL=http://host.docker.internal:5051 AWS_ACCESS_KEY_ID=test-access-key-id AWS_SECRET_ACCESS_KEY=test-secret-access-key AWS_SESSION_TOKEN=test-session-token src/PwrDrvr.LambdaDispatch.Router/bin/Release/net8.0/PwrDrvr.LambdaDispatch.Router 2>&1 | tee router.log

AWS_LAMBDA_RUNTIME_API=host.docker.internal:5051 AWS_REGION=us-east-2 AWS_ACCESS_KEY_ID=test-access-key-id AWS_SECRET_ACCESS_KEY=test-secret-access-key AWS_SESSION_TOKEN=test-session-token src/PwrDrvr.LambdaDispatch.Extension/bin/Release/net8.0/bootstrap 2>&1 | tee extension.log

# Running the Native version under dev container
export LD_LIBRARY_PATH=$LD_LIBRARY_PATH:/workspaces/lambda-dispatch/src/PwrDrvr.LambdaDispatch.Extension/bin/Release/net8.0/linux-arm64/
AWS_LAMBDA_RUNTIME_API=host.docker.internal:5051 AWS_REGION=us-east-2 AWS_ACCESS_KEY_ID=test-access-key-id AWS_SECRET_ACCESS_KEY=test-secret-access-key AWS_SESSION_TOKEN=test-session-token bin/Release/net8.0/linux-arm64/native/bootstrap
```

## Capturing Packets with tshark

- Overall:
  - We have to use an insecure cipher that does not use a Diffie-Hellman key exchange because HttpClient does not write the key exchange to a file that WireShark can use to decrypt
  - We cannot capture directly in Wireshark because the UI becomes unresponsive when given several GB of data
  - Instead we capture with the CLI tools then open a portion of the data with the error in Wireshark
- Define `USE_SOCKETS_HTTP_HANDLER` in ()[src/PwrDrvr.LambdaDispatch.Extension/HttpReverseRequester.cs]
- Define `USE_INSECURE_CIPHER_FOR_WIRESHARK` in ()[src/PwrDrvr.LambdaDispatch.Extension/HttpReverseRequester.cs]
- `dotnet build -c Release`
- Start the Router and Lambda following instructions above
- Run a million requests at a time until `hey` reports that some of the requests timed out
  - `hey -n 1000000 -c 1000000 http://localhost:5001/fact`
- After capturing the error, stop `tshark` with Ctrl-C then stop the Lambda and the Router with Ctrl-C
- Run the commands below to capture packets
- Run the command below to split the capture into multiple files:
  - `editcap -c 1000000 proto-error.pcapng proto-error-split.pcapng`
- Find the file with the first timestamp before the error happened
- Open the file in Wireshark
- Scroll down to the timestamp of the error
- If the packets at that time to do not have the protocol as HTTP2 then it means that the beginning of that HTTP2 socket was not in that capture file and you need to adjust the splits to a larger or smaller number of packets to shift where the files start
  - One simple approach is to just double the size of the splits and try again

```sh
mkdir captures

cd captures

# This will capture and decrypt the packets
# The saved file will be enormous (it takes millions of requests to capture the error)
# The log file will take several minutes to finish writing after the capture is complete
tshark -i lo0 -f "tcp port 5004" -o "tls.keys_list:0.0.0.0,5004,http,../certs/lambdadispatch.local.key" -w proto-error.pcapng -P > protoerror.log
```
