---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 08:17 UTC from commit `3568cd80b53a`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31302935726) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 3.59GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error       | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  93.317 μs |  28.3374 μs | 1.5533 μs |  1.00 |    0.02 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               |  88.545 μs |  17.2454 μs | 0.9453 μs |  0.95 |    0.02 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get              | GET                  |  88.991 μs | 128.1238 μs | 7.0229 μs |  1.00 |    0.10 |     502 B |        1.00 |
| Respire_Get                    | GET                  |  91.305 μs |  48.1348 μs | 2.6384 μs |  1.03 |    0.08 |      80 B |        0.16 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  78.663 μs |  38.0225 μs | 2.0841 μs |  1.00 |    0.03 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  76.604 μs |  41.5889 μs | 2.2796 μs |  0.97 |    0.03 |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   2.950 μs |   0.3655 μs | 0.0200 μs |  1.00 |    0.01 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.564 μs |   0.4152 μs | 0.0228 μs |  0.87 |    0.01 |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HGet             | HGET                 |  94.596 μs |  23.8431 μs | 1.3069 μs |  1.00 |    0.02 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  91.821 μs |  11.9405 μs | 0.6545 μs |  0.97 |    0.01 |      80 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HSet             | HSET                 |  90.627 μs |  37.5393 μs | 2.0577 μs |  1.00 |    0.03 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 |  93.083 μs |   6.1860 μs | 0.3391 μs |  1.03 |    0.02 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Incr             | INCR                 |  91.541 μs |  50.1903 μs | 2.7511 μs |  1.00 |    0.04 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  89.722 μs |   5.9172 μs | 0.3243 μs |  0.98 |    0.03 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 177.752 μs |  34.3774 μs | 1.8843 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 173.456 μs |  29.5042 μs | 1.6172 μs |  0.98 |    0.01 |     256 B |        0.34 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping             | PING                 |  91.418 μs |   9.5236 μs | 0.5220 μs |  1.00 |    0.01 |     303 B |        1.00 |
| Respire_Ping                   | PING                 |  85.486 μs |  88.9547 μs | 4.8759 μs |  0.94 |    0.05 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  72.736 μs |  25.6727 μs | 1.4072 μs |  1.00 |    0.02 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  74.991 μs |  15.8644 μs | 0.8696 μs |  1.03 |    0.02 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 |  93.883 μs |  20.6443 μs | 1.1316 μs |  1.00 |    0.01 |     310 B |        1.00 |
| Respire_SAdd                   | SADD                 |  92.856 μs |  21.3138 μs | 1.1683 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             |  99.451 μs |  25.5168 μs | 1.3987 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  99.602 μs |  33.2502 μs | 1.8226 μs |  1.00 |    0.02 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              |  93.538 μs |   6.5454 μs | 0.3588 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  93.353 μs |  36.0989 μs | 1.9787 μs |  1.00 |    0.02 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  95.291 μs |   7.5241 μs | 0.4124 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  93.919 μs |  15.6245 μs | 0.8564 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  78.113 μs |  16.2234 μs | 0.8893 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  75.017 μs |  40.8628 μs | 2.2398 μs |  0.96 |    0.03 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 178.563 μs |  36.7524 μs | 2.0145 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 176.343 μs |   8.3239 μs | 0.4563 μs |  0.99 |    0.01 |     200 B |        0.31 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  90.553 μs |   9.2067 μs | 0.5046 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  92.890 μs |  20.3724 μs | 1.1167 μs |  1.03 |    0.01 |      32 B |        0.10 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 187.279 μs |  33.242 μs | 1.8221 μs |  1.00 |    0.01 |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               | 189.783 μs |  17.948 μs | 0.9838 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 191.259 μs |  47.241 μs | 2.5894 μs |  1.00 |    0.02 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 189.529 μs |  17.061 μs | 0.9352 μs |  0.99 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 174.469 μs |  68.816 μs | 3.7720 μs |  1.00 |    0.03 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 175.656 μs |  25.342 μs | 1.3891 μs |  1.01 |    0.02 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.463 μs |   1.690 μs | 0.0926 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.906 μs |   2.481 μs | 0.1360 μs |  0.90 |    0.03 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 193.685 μs |  16.506 μs | 0.9048 μs |  1.00 |    0.01 |     518 B |        1.00 |
| Respire_HGet                   | HGET                 | 187.048 μs |  56.006 μs | 3.0699 μs |  0.97 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 190.041 μs |  10.571 μs | 0.5794 μs |  1.00 |    0.00 |     327 B |        1.00 |
| Respire_HSet                   | HSET                 | 189.177 μs |  26.765 μs | 1.4671 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 189.839 μs |  20.267 μs | 1.1109 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 188.331 μs |  32.313 μs | 1.7712 μs |  0.99 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 375.320 μs |  31.924 μs | 1.7499 μs |  1.00 |    0.01 |     759 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 368.506 μs |  30.112 μs | 1.6505 μs |  0.98 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 187.259 μs |  10.019 μs | 0.5492 μs |  1.00 |    0.00 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 186.776 μs |  19.676 μs | 1.0785 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 173.915 μs |  17.889 μs | 0.9806 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 172.863 μs |  22.226 μs | 1.2183 μs |  0.99 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 189.770 μs |   8.500 μs | 0.4659 μs |  1.00 |    0.00 |     310 B |        1.00 |
| Respire_SAdd                   | SADD                 | 188.396 μs |  23.722 μs | 1.3003 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 201.404 μs |  14.742 μs | 0.8081 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 202.296 μs |  15.320 μs | 0.8397 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 187.827 μs |  86.284 μs | 4.7295 μs |  1.00 |    0.03 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 182.827 μs | 101.237 μs | 5.5492 μs |  0.97 |    0.03 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 183.165 μs | 156.311 μs | 8.5679 μs |  1.00 |    0.06 |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 192.453 μs |   5.149 μs | 0.2823 μs |  1.05 |    0.04 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 177.074 μs |  57.899 μs | 3.1736 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 176.295 μs |  18.917 μs | 1.0369 μs |  1.00 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 367.823 μs |  55.694 μs | 3.0528 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 363.492 μs |  27.344 μs | 1.4988 μs |  0.99 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 187.538 μs |  23.706 μs | 1.2994 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 189.867 μs |   8.532 μs | 0.4677 μs |  1.01 |    0.01 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
