# Use the official .NET 8 SDK image as the build environment
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env

# Set the working directory
WORKDIR /app

# Copy everything from the current directory to the working directory in the image
COPY . ./

# Restore dependencies and build the project
RUN dotnet restore
RUN dotnet publish src/PwrDrvr.LambdaDispatch.Router/PwrDrvr.LambdaDispatch.Router.csproj -c Release --self-contained true --runtime linux-arm64 -o out

# Use the Amazon Linux 2023 image as the runtime environment
FROM amazonlinux:2023

# Set the working directory
WORKDIR /app

# Install the ICU libraries
RUN yum install -y libicu

# Copy the build output from the build environment
COPY --from=build-env /app/out .

# Set the entrypoint
ENTRYPOINT ["./PwrDrvr.LambdaDispatch.Router"]