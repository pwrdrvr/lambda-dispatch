## Overview

Captures performance comparisons between direct invoked lambdas and the lambda dispatcher.

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
  - 298 RPS
  - 314 ms avg
- DirectLambda:
  - 100 instances
  - 241 RPS
  - 396 ms avg
- Take aways
  - Cost 20% as much, plus the ECS container
  - Faster because of the removal of the base64 encoding/decoding happening within the nodejs lambda
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
  Total:        3.3571 secs
  Slowest:      0.7796 secs
  Fastest:      0.0088 secs
  Average:      0.3144 secs
  Requests/sec: 297.8726
  
  Total data:   168161000 bytes
  Size/request: 168161 bytes

Response time histogram:
  0.009 [1]     |
  0.086 [13]    |■■
  0.163 [55]    |■■■■■■■■■
  0.240 [248]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.317 [236]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.394 [188]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.471 [120]   |■■■■■■■■■■■■■■■■■■■
  0.548 [105]   |■■■■■■■■■■■■■■■■■
  0.625 [29]    |■■■■■
  0.702 [2]     |
  0.780 [3]     |


Latency distribution:
  10% in 0.1780 secs
  25% in 0.2137 secs
  50% in 0.3038 secs
  75% in 0.3973 secs
  90% in 0.4930 secs
  95% in 0.5304 secs
  99% in 0.6138 secs

Details (average, fastest, slowest):
  DNS+dialup:   0.0000 secs, 0.0088 secs, 0.7796 secs
  DNS-lookup:   0.0015 secs, 0.0000 secs, 0.0992 secs
  req write:    0.0061 secs, 0.0003 secs, 0.0847 secs
  resp wait:    0.2684 secs, 0.0074 secs, 0.7716 secs
  resp read:    0.0238 secs, 0.0002 secs, 0.2742 secs

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