# stress

curl -v http://localhost:5001/ping

TOKIO_WORKER_THREADS=1 oha -c 20 -z 60s http://127.0.0.1:5001/ping

TOKIO_WORKER_THREADS=1 oha -c 20 -z 60s -D target/release/extension -T application/octet-stream -m POST http://127.0.0.1:5001/echo

k6 run k6/ping-dispatch-local.js

curl -X POST -H "Content-Type: text/markdown" --data-binary @README.md http://localhost:5001/echo --compressed -o README2.md -v

# node

NUMBER_OF_WORKERS=4 node dist/app.cjs

# router

dotnet build -c Release src/PwrDrvr.LambdaDispatch.Router

BUILD_TIME=$(date) GIT_HASH=$(git rev-parse --short HEAD) LAMBDA_DISPATCH_MinWorkerThreads=1 LAMBDA_DISPATCH_MaxWorkerThreads=4 DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0 LAMBDA_DISPATCH_InstanceCountMultiplier=4 LAMBDA_DISPATCH_MaxConcurrentCount=20 LAMBDA_DISPATCH_AllowInsecureControlChannel=true LAMBDA_DISPATCH_PreferredControlChannelScheme=http LAMBDA_DISPATCH_FunctionName=dogs AWS_LAMBDA_SERVICE_URL=http://localhost:5051 AWS_REGION=us-east-2 AWS_ACCESS_KEY_ID=test-access-key-id AWS_SECRET_ACCESS_KEY=test-secret-access-key AWS_SESSION_TOKEN=test-session-token src/PwrDrvr.LambdaDispatch.Router/bin/Release/net8.0/PwrDrvr.LambdaDispatch.Router 2>&1 | tee router.log

# extension

LAMBDA_DISPATCH_PORT=3001 LAMBDA_DISPATCH_RUNTIME=current_thread LAMBDA_DISPATCH_FORCE_DEADLINE=60 AWS_LAMBDA_FUNCTION_VERSION=\$LATEST AWS_LAMBDA_FUNCTION_MEMORY_SIZE=512 AWS_LAMBDA_FUNCTION_NAME=dogs AWS_LAMBDA_RUNTIME_API=localhost:5051 AWS_REGION=us-east-2 AWS_ACCESS_KEY_ID=test-access-key-id AWS_SECRET_ACCESS_KEY=test-secret-access-key AWS_SESSION_TOKEN=test-session-token cargo run --release --bin extension 2>&1 | tee extension.log

# d-router

aws sso login

aws ecr get-login-password --region us-east-2 | docker login --username AWS --password-stdin 220761759939.dkr.ecr.us-east-2.amazonaws.com                

docker build --build-arg GIT_HASH=$(git rev-parse --short HEAD) --build-arg BUILD_TIME="$(date)" --file DockerfileRouter -t lambda-dispatch-router . && \
docker tag lambda-dispatch-router:latest 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-router:latest && \
docker push 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-router:latest && \
date

# d-router - fix-scale-down-timer branch

BUILD_TIME=$(date) GIT_HASH=$(git rev-parse --short HEAD) LAMBDA_DISPATCH_MaxWorkerThreads=2 DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0 LAMBDA_DISPATCH_InstanceCountMultiplier=2 LAMBDA_DISPATCH_MaxConcurrentCount=10 LAMBDA_DISPATCH_AllowInsecureControlChannel=true LAMBDA_DISPATCH_PreferredControlChannelScheme=http LAMBDA_DISPATCH_FunctionName=dogs AWS_LAMBDA_SERVICE_URL=http://localhost:5051 AWS_REGION=us-east-2 AWS_ACCESS_KEY_ID=test-access-key-id AWS_SECRET_ACCESS_KEY=test-secret-access-key AWS_SESSION_TOKEN=test-session-token src/PwrDrvr.LambdaDispatch.Router/bin/Release/net8.0/PwrDrvr.LambdaDispatch.Router 2>&1 > router.log

# d-demoapp

docker build --file DockerfileExtension -t lambda-dispatch-extension . && \
docker build --file DockerfileLambdaDemoApp -t lambda-dispatch-demo-app . && \
docker tag lambda-dispatch-demo-app:latest 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-demo-app:latest && \
docker push 220761759939.dkr.ecr.us-east-2.amazonaws.com/lambda-dispatch-demo-app:latest && \
date

# killall

killall -9 PwrDrvr.LambdaDispatch.Router
