## Overview

Captures performance comparisons between direct invoked lambdas and the lambda dispatcher.

## GET: Text/Plain Ping

- Lambda Dispatcher:
  - 20 instances
  - 2740 RPS
  - 34 ms avg
- DirectLambda:
  - 100 instances
  - 5552 RPS
  - 17 ms avg
- Take aways
  - Cost 20% as much, plus the ECS container
  - Direct Lambda is faster, in the steady state, because each request gets its own Lambda, but it costs 5x more and scale ups cause an 8 second delay
- Both Lambdas configured with 512 MB
- ECS container configured with 1 CPU / 2 GB
- Run from CloudShell in us-east-2

### Commands

```sh
./hey_linux_amd64 -h2 -c 100 -n 10000 https://lambdadispatch.ghpublic.pwrdrvr.com/ping

./hey_linux_amd64 -h2 -c 100 -n 10000 https://directlambda.ghpublic.pwrdrvr.com/ping
```

### Results

#### Lambda Dispatcher

```sh
./hey_linux_amd64 -h2 -c 100 -n 10000 https://lambdadispatch.ghpublic.pwrdrvr.com/ping

Summary:
  Total:        3.6488 secs
  Slowest:      0.2343 secs
  Fastest:      0.0025 secs
  Average:      0.0343 secs
  Requests/sec: 2740.6270
  
  Total data:   40000 bytes
  Size/request: 4 bytes

Response time histogram:
  0.003 [1]     |
  0.026 [5740]  |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.049 [1042]  |■■■■■■■
  0.072 [1985]  |■■■■■■■■■■■■■■
  0.095 [1076]  |■■■■■■■
  0.118 [119]   |■
  0.142 [15]    |
  0.165 [14]    |
  0.188 [6]     |
  0.211 [0]     |
  0.234 [2]     |


Latency distribution:
  10% in 0.0094 secs
  25% in 0.0127 secs
  50% in 0.0211 secs
  75% in 0.0595 secs
  90% in 0.0745 secs
  95% in 0.0830 secs
  99% in 0.0999 secs

Details (average, fastest, slowest):
  DNS+dialup:   0.0000 secs, 0.0025 secs, 0.2343 secs
  DNS-lookup:   0.0000 secs, 0.0000 secs, 0.0089 secs
  req write:    0.0000 secs, 0.0000 secs, 0.0123 secs
  resp wait:    0.0326 secs, 0.0024 secs, 0.2335 secs
  resp read:    0.0010 secs, 0.0000 secs, 0.0337 secs

Status code distribution:
  [200] 10000 responses
```

#### Direct Lambda

```sh
./hey_linux_amd64 -h2 -c 100 -n 10000 https://directlambda.ghpublic.pwrdrvr.com/ping

Summary:
  Total:        1.8009 secs
  Slowest:      0.1930 secs
  Fastest:      0.0085 secs
  Average:      0.0173 secs
  Requests/sec: 5552.6414
  
  Total data:   40000 bytes
  Size/request: 4 bytes

Response time histogram:
  0.009 [1]     |
  0.027 [9382]  |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.045 [467]   |■■
  0.064 [28]    |
  0.082 [21]    |
  0.101 [5]     |
  0.119 [2]     |
  0.138 [16]    |
  0.156 [30]    |
  0.175 [46]    |
  0.193 [2]     |


Latency distribution:
  10% in 0.0113 secs
  25% in 0.0125 secs
  50% in 0.0142 secs
  75% in 0.0176 secs
  90% in 0.0225 secs
  95% in 0.0298 secs
  99% in 0.0917 secs

Details (average, fastest, slowest):
  DNS+dialup:   0.0000 secs, 0.0085 secs, 0.1930 secs
  DNS-lookup:   0.0003 secs, 0.0000 secs, 0.0659 secs
  req write:    0.0000 secs, 0.0000 secs, 0.0216 secs
  resp wait:    0.0154 secs, 0.0021 secs, 0.1351 secs
  resp read:    0.0010 secs, 0.0000 secs, 0.0258 secs

Status code distribution:
  [200] 10000 responses
```

## GET: Image/Jpeg

- Lambda Dispatcher:
  - 20 instances
  - 644 RPS
  - 133 ms avg
- DirectLambda:
  - 100 instances
  - 479 RPS
  - 197 ms avg
- Take aways
  - Cost 20% as much, plus the ECS container
  - Faster because of the removal of the base64 encoding happening within the nodejs lambda
  - Removes payload size restrictions: the lambda dispatcher can echo a 9 MB body but the direct lambda can only echo a 6 MB body
- Both Lambdas configured with 512 MB
- ECS container configured with 1 CPU / 2 GB
- Run from CloudShell in us-east-2

### Commands
```sh
./hey_linux_amd64 -h2 -c 100 -n 1000 https://lambdadispatch.ghpublic.pwrdrvr.com/public/silly-test-image.jpg

./hey_linux_amd64 -h2 -c 100 -n 1000 https://directlambda.ghpublic.pwrdrvr.com/public/silly-test-image.jpg
```

### Results

#### Lambda Dispatcher

```
./hey_linux_amd64 -h2 -c 100 -n 1000 https://lambdadispatch.ghpublic.pwrdrvr.com/public/silly-test-image.jpg

Summary:
  Total:        1.5525 secs
  Slowest:      0.3954 secs
  Fastest:      0.0054 secs
  Average:      0.1329 secs
  Requests/sec: 644.1276
  
  Total data:   168161000 bytes
  Size/request: 168161 bytes

Response time histogram:
  0.005 [1]     |
  0.044 [70]    |■■■■■■■■■■
  0.083 [128]   |■■■■■■■■■■■■■■■■■■■
  0.122 [274]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.161 [213]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.200 [198]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.239 [74]    |■■■■■■■■■■■
  0.278 [28]    |■■■■
  0.317 [4]     |■
  0.356 [2]     |
  0.395 [8]     |■


Latency distribution:
  10% in 0.0560 secs
  25% in 0.0901 secs
  50% in 0.1266 secs
  75% in 0.1723 secs
  90% in 0.2096 secs
  95% in 0.2301 secs
  99% in 0.3325 secs

Details (average, fastest, slowest):
  DNS+dialup:   0.0001 secs, 0.0054 secs, 0.3954 secs
  DNS-lookup:   0.0015 secs, 0.0000 secs, 0.0227 secs
  req write:    0.0000 secs, 0.0000 secs, 0.0035 secs
  resp wait:    0.0930 secs, 0.0046 secs, 0.3081 secs
  resp read:    0.0278 secs, 0.0002 secs, 0.3202 secs

Status code distribution:
  [200] 1000 responses
```

#### Direct Lambda

```
./hey_linux_amd64 -h2 -c 100 -n 1000 https://directlambda.ghpublic.pwrdrvr.com/public/silly-test-image.jpg

Summary:
  Total:        2.0834 secs
  Slowest:      0.4313 secs
  Fastest:      0.0287 secs
  Average:      0.1966 secs
  Requests/sec: 479.9916
  
  Total data:   168161000 bytes
  Size/request: 168161 bytes

Response time histogram:
  0.029 [1]     |
  0.069 [6]     |■
  0.109 [32]    |■■■
  0.150 [104]   |■■■■■■■■■■
  0.190 [410]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.230 [289]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.270 [50]    |■■■■■
  0.311 [24]    |■■
  0.351 [35]    |■■■
  0.391 [31]    |■■■
  0.431 [18]    |■■


Latency distribution:
  10% in 0.1383 secs
  25% in 0.1644 secs
  50% in 0.1854 secs
  75% in 0.2080 secs
  90% in 0.2739 secs
  95% in 0.3505 secs
  99% in 0.4057 secs

Details (average, fastest, slowest):
  DNS+dialup:   0.0000 secs, 0.0287 secs, 0.4313 secs
  DNS-lookup:   0.0004 secs, 0.0000 secs, 0.0051 secs
  req write:    0.0000 secs, 0.0000 secs, 0.0036 secs
  resp wait:    0.1818 secs, 0.0281 secs, 0.3951 secs
  resp read:    0.0068 secs, 0.0002 secs, 0.1180 secs

Status code distribution:
  [200] 1000 responses
```


## POST: Image/Jpeg Echo

- Lambda Dispatcher:
  - 20 instances
  - 320 RPS
  - 285 ms avg
- DirectLambda:
  - 100 instances
  - 241 RPS
  - 396 ms avg
- Take aways
  - Cost 20% as much, plus the ECS container
  - Faster because of the removal of the base64 encoding/decoding happening within the nodejs lambda
  - Faster because the lambda dispatcher can have a near-zero TTFB to the ALB and can utilize the response bandwidth sooner
  - Removes payload size restrictions: the lambda dispatcher can echo a 9 MB body but the direct lambda can only echo a 6 MB body
- Both Lambdas configured with 512 MB
- ECS container configured with 1 CPU / 2 GB
- Run from CloudShell in us-east-2

### Commands

It is critical to set the `Content-Type` header to `image/jpeg` so that `serverless-adapter` will base64 encode the body in the response.

```sh
./hey_linux_amd64 -h2 -T "image/jpeg" -D ./silly-test-image.jpg -m POST -c 100 -n 1000 https://lambdadispatch.ghpublic.pwrdrvr.com/echo

./hey_linux_amd64 -h2 -T "image/jpeg" -D ./silly-test-image.jpg -m POST -c 100 -n 1000 https://directlambda.ghpublic.pwrdrvr.com/echo
```

### Results

#### Lambda Dispatcher

```
./hey_linux_amd64 -h2 -T "image/jpeg" -D ./silly-test-image.jpg -m POST -c 100 -n 1000 https://lambdadispatch.ghpublic.pwrdrvr.com/echo

Summary:
  Total:        3.1221 secs
  Slowest:      0.8441 secs
  Fastest:      0.0157 secs
  Average:      0.2858 secs
  Requests/sec: 320.2933
  
  Total data:   168161000 bytes
  Size/request: 168161 bytes

Response time histogram:
  0.016 [1]     |
  0.099 [71]    |■■■■■■■■■■■■
  0.181 [155]   |■■■■■■■■■■■■■■■■■■■■■■■■■
  0.264 [244]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.347 [243]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.430 [134]   |■■■■■■■■■■■■■■■■■■■■■■
  0.513 [80]    |■■■■■■■■■■■■■
  0.596 [38]    |■■■■■■
  0.678 [16]    |■■■
  0.761 [11]    |■■
  0.844 [7]     |■


Latency distribution:
  10% in 0.1119 secs
  25% in 0.1892 secs
  50% in 0.2719 secs
  75% in 0.3732 secs
  90% in 0.4744 secs
  95% in 0.5283 secs
  99% in 0.7443 secs

Details (average, fastest, slowest):
  DNS+dialup:   0.0000 secs, 0.0157 secs, 0.8441 secs
  DNS-lookup:   0.0004 secs, 0.0000 secs, 0.0055 secs
  req write:    0.0089 secs, 0.0003 secs, 0.0586 secs
  resp wait:    0.2257 secs, 0.0098 secs, 0.7531 secs
  resp read:    0.0306 secs, 0.0002 secs, 0.3125 secs

Status code distribution:
  [200] 1000 responses
```

#### Direct Lambda

```
./hey_linux_amd64 -h2 -T "image/jpeg" -D ./silly-test-image.jpg -m POST -c 100 -n 1000 https://directlambda.ghpublic.pwrdrvr.com/echo

Summary:
  Total:        4.1395 secs
  Slowest:      0.7677 secs
  Fastest:      0.0480 secs
  Average:      0.3965 secs
  Requests/sec: 241.5765
  
  Total data:   168161000 bytes
  Size/request: 168161 bytes

Response time histogram:
  0.048 [1]     |
  0.120 [1]     |
  0.192 [37]    |■■■
  0.264 [31]    |■■■
  0.336 [117]   |■■■■■■■■■■
  0.408 [491]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.480 [199]   |■■■■■■■■■■■■■■■■
  0.552 [28]    |■■
  0.624 [24]    |■■
  0.696 [31]    |■■■
  0.768 [40]    |■■■


Latency distribution:
  10% in 0.2895 secs
  25% in 0.3457 secs
  50% in 0.3786 secs
  75% in 0.4316 secs
  90% in 0.5259 secs
  95% in 0.6741 secs
  99% in 0.7607 secs

Details (average, fastest, slowest):
  DNS+dialup:   0.0000 secs, 0.0480 secs, 0.7677 secs
  DNS-lookup:   0.0012 secs, 0.0000 secs, 0.0178 secs
  req write:    0.1219 secs, 0.0040 secs, 0.2589 secs
  resp wait:    0.2315 secs, 0.0228 secs, 0.3577 secs
  resp read:    0.0250 secs, 0.0002 secs, 0.3340 secs

Status code distribution:
  [200] 1000 responses
```

## POST: 9 MB Binary Echo

- Lambda Dispatcher:
  - 2 instances
  - 6 RPS
  - 1,530 ms avg
- DirectLambda:
  - Not possible
- Take aways
  - Faster because of the removal of the base64 encoding happening within the nodejs lambda
  - Removes payload size restrictions: the lambda dispatcher can echo this payload but the direct lambda cannot
- Both Lambdas configured with 512 MB
- ECS container configured with 1 CPU / 2 GB
- Run from CloudShell in us-east-2

### Commands

```sh
./hey_linux_amd64 -h2 -T "image/jpeg" -D ./hey_linux_amd64 -m POST -c 10 -n 60 https://lambdadispatch.ghpublic.pwrdrvr.com/echo
```

### Results

#### Lambda Dispatcher

```
./hey_linux_amd64 -h2 -T "image/jpeg" -D ./hey_linux_amd64 -m POST -c 10 -n 60 https://lambdadispatch.ghpublic.pwrdrvr.com/echo

Summary:
  Total:        9.6185 secs
  Slowest:      2.5793 secs
  Fastest:      0.6273 secs
  Average:      1.5322 secs
  Requests/sec: 6.2380
  
  Total data:   561904560 bytes
  Size/request: 9365076 bytes

Response time histogram:
  0.627 [1]     |■■
  0.823 [3]     |■■■■■■■
  1.018 [8]     |■■■■■■■■■■■■■■■■■■■
  1.213 [5]     |■■■■■■■■■■■■
  1.408 [6]     |■■■■■■■■■■■■■■
  1.603 [4]     |■■■■■■■■■
  1.799 [17]    |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  1.994 [7]     |■■■■■■■■■■■■■■■■
  2.189 [3]     |■■■■■■■
  2.384 [2]     |■■■■■
  2.579 [4]     |■■■■■■■■■


Latency distribution:
  10% in 0.8625 secs
  25% in 1.0965 secs
  50% in 1.6546 secs
  75% in 1.8163 secs
  90% in 2.2062 secs
  95% in 2.5140 secs
  0% in 0.0000 secs

Details (average, fastest, slowest):
  DNS+dialup:   0.0003 secs, 0.6273 secs, 2.5793 secs
  DNS-lookup:   0.0004 secs, 0.0000 secs, 0.0030 secs
  req write:    0.1380 secs, 0.0149 secs, 0.6860 secs
  resp wait:    0.5802 secs, 0.1478 secs, 1.1258 secs
  resp read:    0.8095 secs, 0.2276 secs, 1.4222 secs

Status code distribution:
  [200] 60 responses
```
