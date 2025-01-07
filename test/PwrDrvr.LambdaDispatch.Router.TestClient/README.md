## Overview

This tool is used to confirm that full duplex (receiving while sending) is possible with HTTP/1.1 chunked request/responses in the Lambda Dispatch Router.

## Build

```shell
## Local usage
dotnet build -c Release test/PwrDrvr.LambdaDispatch.Router.TestClient/PwrDrvr.LambdaDispatch.Router.TestClient.csproj 

## Self-contained
dotnet publish -c Release test/PwrDrvr.LambdaDispatch.Router.TestClient/PwrDrvr.LambdaDispatch.Router.TestClient.csproj  --self-contained true --runtime linux-amd64 /p:NativeAot=true
```

## Usage

```shell
test/PwrDrvr.LambdaDispatch.Router.TestClient/bin/Release/net8.0/PwrDrvr.LambdaDispatch.Router.TestClient "http://localhost:5001/echo-local?foo=bar" ~/bin/helm

test/PwrDrvr.LambdaDispatch.Router.TestClient/bin/Release/net8.0/PwrDrvr.LambdaDispatch.Router.TestClient "http://localhost:5001/echo?foo=bar" ~/bin/helm
```

## Package for CloudShell

```shell
rm testclient.zip && zip -j -r testclient.zip test/PwrDrvr.LambdaDispatch.Router.TestClient/bin/Release/net8.0/linux-x64
```

## Unpackage in CloudShell

```shell
rm -rf testclient && unzip testclient.zip -d testclient && chmod +x testclient/bootstrap
```
