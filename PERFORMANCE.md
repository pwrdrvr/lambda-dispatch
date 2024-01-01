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

```log
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

## GET: Image/Jpeg S3

### Commands

```sh
```

### Results

#### Lambda Dispatcher - 20 Warm / 20 Needed / 100 Concurrent

```log
./hey_linux_amd64 -h2 -c 100 -n 1000 https://lambdadispatch.ghpublic.pwrdrvr.com/read-s3

Summary:
  Total:        2.2130 secs
  Slowest:      0.4467 secs
  Fastest:      0.0212 secs
  Average:      0.1884 secs
  Requests/sec: 451.8819
  

Response time histogram:
  0.021 [1]     |
  0.064 [28]    |■■■■■
  0.106 [106]   |■■■■■■■■■■■■■■■■■■
  0.149 [186]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.191 [193]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.234 [234]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.276 [125]   |■■■■■■■■■■■■■■■■■■■■■
  0.319 [66]    |■■■■■■■■■■■
  0.362 [42]    |■■■■■■■
  0.404 [13]    |■■
  0.447 [6]     |■


Latency distribution:
  10% in 0.0925 secs
  25% in 0.1328 secs
  50% in 0.1889 secs
  75% in 0.2345 secs
  90% in 0.2950 secs
  95% in 0.3248 secs
  99% in 0.3916 secs

Details (average, fastest, slowest):
  DNS+dialup:   0.0000 secs, 0.0212 secs, 0.4467 secs
  DNS-lookup:   0.0006 secs, 0.0000 secs, 0.0110 secs
  req write:    0.0000 secs, 0.0000 secs, 0.0035 secs
  resp wait:    0.1551 secs, 0.0187 secs, 0.4389 secs
  resp read:    0.0284 secs, 0.0003 secs, 0.2090 secs

Status code distribution:
  [200] 1000 responses
```

#### Lambda Dispatcher - 2 Warm / 20 Needed / 100 Concurrent

```log
./hey_linux_amd64 -h2 -c 100 -n 1000 https://lambdadispatch.ghpublic.pwrdrvr.com/read-s3

Summary:
  Total:        12.2727 secs
  Slowest:      2.7098 secs
  Fastest:      0.0212 secs
  Average:      1.1674 secs
  Requests/sec: 81.4816
  

Response time histogram:
  0.021 [1]     |
  0.290 [218]   |■■■■■■■■■■■■■■■■■■■■■■■■■
  0.559 [25]    |■■■
  0.828 [24]    |■■■
  1.097 [46]    |■■■■■
  1.365 [157]   |■■■■■■■■■■■■■■■■■■
  1.634 [342]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  1.903 [109]   |■■■■■■■■■■■■■
  2.172 [34]    |■■■■
  2.441 [36]    |■■■■
  2.710 [8]     |■


Latency distribution:
  10% in 0.0997 secs
  25% in 0.6455 secs
  50% in 1.3940 secs
  75% in 1.5698 secs
  90% in 1.7841 secs
  95% in 2.1048 secs
  99% in 2.4179 secs

Details (average, fastest, slowest):
  DNS+dialup:   0.0001 secs, 0.0212 secs, 2.7098 secs
  DNS-lookup:   0.0010 secs, 0.0000 secs, 0.0249 secs
  req write:    0.0000 secs, 0.0000 secs, 0.0049 secs
  resp wait:    1.1077 secs, 0.0188 secs, 2.6167 secs
  resp read:    0.0511 secs, 0.0003 secs, 0.5346 secs

Status code distribution:
  [200] 1000 responses
```

#### Lambda Dispatcher - 2 Warm / 2 Needed / 10 Concurrent

```log
./hey_linux_amd64 -h2 -c 10 -n 1000 https://lambdadispatch.ghpublic.pwrdrvr.com/read-s3

Summary:
  Total:        19.0802 secs
  Slowest:      0.9052 secs
  Fastest:      0.0214 secs
  Average:      0.1851 secs
  Requests/sec: 52.4103
  

Response time histogram:
  0.021 [1]     |
  0.110 [188]   |■■■■■■■■■■■■■■■■■
  0.198 [437]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.287 [278]   |■■■■■■■■■■■■■■■■■■■■■■■■■
  0.375 [64]    |■■■■■■
  0.463 [16]    |■
  0.552 [4]     |
  0.640 [2]     |
  0.728 [2]     |
  0.817 [1]     |
  0.905 [7]     |■


Latency distribution:
  10% in 0.0855 secs
  25% in 0.1210 secs
  50% in 0.1639 secs
  75% in 0.2208 secs
  90% in 0.2832 secs
  95% in 0.3425 secs
  99% in 0.6615 secs

Details (average, fastest, slowest):
  DNS+dialup:   0.0000 secs, 0.0214 secs, 0.9052 secs
  DNS-lookup:   0.0000 secs, 0.0000 secs, 0.0020 secs
  req write:    0.0000 secs, 0.0000 secs, 0.0009 secs
  resp wait:    0.1580 secs, 0.0188 secs, 0.7852 secs
  resp read:    0.0269 secs, 0.0003 secs, 0.3014 secs

Status code distribution:
  [200] 1000 responses
```

#### Direct Lambda - 100 Warm / 100 Concurrent

```log
./hey_linux_amd64 -h2 -c 100 -n 1000 https://directlambda.ghpublic.pwrdrvr.com/read-s3

Summary:
  Total:        2.1875 secs
  Slowest:      0.4558 secs
  Fastest:      0.0414 secs
  Average:      0.2023 secs
  Requests/sec: 457.1376
  
  Total data:   168161000 bytes
  Size/request: 168161 bytes

Response time histogram:
  0.041 [1]     |
  0.083 [5]     |
  0.124 [13]    |■
  0.166 [177]   |■■■■■■■■■■■■■■
  0.207 [506]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.249 [174]   |■■■■■■■■■■■■■■
  0.290 [44]    |■■■
  0.331 [34]    |■■■
  0.373 [23]    |■■
  0.414 [13]    |■
  0.456 [10]    |■


Latency distribution:
  10% in 0.1534 secs
  25% in 0.1712 secs
  50% in 0.1901 secs
  75% in 0.2153 secs
  90% in 0.2742 secs
  95% in 0.3271 secs
  99% in 0.4317 secs

Details (average, fastest, slowest):
  DNS+dialup:   0.0001 secs, 0.0414 secs, 0.4558 secs
  DNS-lookup:   0.0007 secs, 0.0000 secs, 0.0137 secs
  req write:    0.0000 secs, 0.0000 secs, 0.0063 secs
  resp wait:    0.1875 secs, 0.0407 secs, 0.3844 secs
  resp read:    0.0011 secs, 0.0002 secs, 0.0198 secs

Status code distribution:
  [200] 1000 responses
```

#### Direct Lambda - 10 Warm / 100 Concurrent

```log
./hey_linux_amd64 -h2 -c 100 -n 1000 https://directlambda.ghpublic.pwrdrvr.com/read-s3

Summary:
  Total:        10.1771 secs
  Slowest:      8.7855 secs
  Fastest:      0.0281 secs
  Average:      0.8729 secs
  Requests/sec: 98.2594
  
  Total data:   168161000 bytes
  Size/request: 168161 bytes

Response time histogram:
  0.028 [1]     |
  0.904 [912]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  1.780 [0]     |
  2.655 [0]     |
  3.531 [0]     |
  4.407 [0]     |
  5.283 [0]     |
  6.158 [0]     |
  7.034 [0]     |
  7.910 [0]     |
  8.786 [87]    |■■■■


Latency distribution:
  10% in 0.0483 secs
  25% in 0.1129 secs
  50% in 0.1525 secs
  75% in 0.1861 secs
  90% in 0.3415 secs
  95% in 8.4830 secs
  99% in 8.7091 secs

Details (average, fastest, slowest):
  DNS+dialup:   0.0001 secs, 0.0281 secs, 8.7855 secs
  DNS-lookup:   0.0004 secs, 0.0000 secs, 0.0053 secs
  req write:    0.0000 secs, 0.0000 secs, 0.0011 secs
  resp wait:    0.8646 secs, 0.0278 secs, 8.7125 secs
  resp read:    0.0008 secs, 0.0002 secs, 0.0234 secs

Status code distribution:
  [200] 1000 responses
```

#### Direct Lambda - 10 Warm / 10 Concurrent

```log
./hey_linux_amd64 -h2 -c 10 -n 1000 https://directlambda.ghpublic.pwrdrvr.com/read-s3

Summary:
  Total:        5.1925 secs
  Slowest:      0.3337 secs
  Fastest:      0.0293 secs
  Average:      0.0496 secs
  Requests/sec: 192.5849
  
  Total data:   168161000 bytes
  Size/request: 168161 bytes

Response time histogram:
  0.029 [1]     |
  0.060 [874]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  0.090 [71]    |■■■
  0.121 [16]    |■
  0.151 [21]    |■
  0.182 [5]     |
  0.212 [1]     |
  0.242 [2]     |
  0.273 [0]     |
  0.303 [5]     |
  0.334 [4]     |


Latency distribution:
  10% in 0.0348 secs
  25% in 0.0371 secs
  50% in 0.0409 secs
  75% in 0.0471 secs
  90% in 0.0661 secs
  95% in 0.0969 secs
  99% in 0.2416 secs

Details (average, fastest, slowest):
  DNS+dialup:   0.0000 secs, 0.0293 secs, 0.3337 secs
  DNS-lookup:   0.0001 secs, 0.0000 secs, 0.0070 secs
  req write:    0.0000 secs, 0.0000 secs, 0.0006 secs
  resp wait:    0.0487 secs, 0.0290 secs, 0.3058 secs
  resp read:    0.0006 secs, 0.0002 secs, 0.0101 secs

Status code distribution:
  [200] 1000 responses
```

#### Direct Lambda - 1 Warm / 10 Concurrent

```log
./hey_linux_amd64 -h2 -c 10 -n 1000 https://directlambda.ghpublic.pwrdrvr.com/read-s3

Summary:
  Total:        13.8925 secs
  Slowest:      8.5822 secs
  Fastest:      0.0299 secs
  Average:      0.1343 secs
  Requests/sec: 71.9813
  
  Total data:   168161000 bytes
  Size/request: 168161 bytes

Response time histogram:
  0.030 [1]     |
  0.885 [989]   |■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■■
  1.740 [0]     |
  2.596 [0]     |
  3.451 [0]     |
  4.306 [0]     |
  5.161 [0]     |
  6.017 [0]     |
  6.872 [1]     |
  7.727 [0]     |
  8.582 [9]     |


Latency distribution:
  10% in 0.0362 secs
  25% in 0.0390 secs
  50% in 0.0443 secs
  75% in 0.0543 secs
  90% in 0.0771 secs
  95% in 0.1139 secs
  99% in 6.6765 secs

Details (average, fastest, slowest):
  DNS+dialup:   0.0000 secs, 0.0299 secs, 8.5822 secs
  DNS-lookup:   0.0000 secs, 0.0000 secs, 0.0030 secs
  req write:    0.0001 secs, 0.0000 secs, 0.0046 secs
  resp wait:    0.1331 secs, 0.0295 secs, 8.5364 secs
  resp read:    0.0007 secs, 0.0002 secs, 0.0139 secs

Status code distribution:
  [200] 1000 responses
```

## GET: Image/Jpeg Local

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
