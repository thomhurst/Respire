---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 01:49 UTC from commit `cb3abd764ead`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31347869502) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 181.933 μs | 52.1988 μs | 2.8612 μs |  1.00 |    0.02 |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               | 184.514 μs |  1.7770 μs | 0.0974 μs |  1.01 |    0.01 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 187.316 μs |  9.7565 μs | 0.5348 μs |  1.00 |    0.00 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 186.753 μs | 18.0566 μs | 0.9897 μs |  1.00 |    0.01 |      48 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 171.145 μs | 32.5541 μs | 1.7844 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 171.280 μs | 14.9955 μs | 0.8220 μs |  1.00 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.299 μs |  1.3770 μs | 0.0755 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.264 μs |  0.8214 μs | 0.0450 μs |  0.99 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 188.992 μs |  9.9976 μs | 0.5480 μs |  1.00 |    0.00 |     518 B |        1.00 |
| Respire_HGet                   | HGET                 | 187.046 μs | 12.7076 μs | 0.6965 μs |  0.99 |    0.00 |      48 B |        0.09 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 187.802 μs | 25.2684 μs | 1.3850 μs |  1.00 |    0.01 |     327 B |        1.00 |
| Respire_HSet                   | HSET                 | 188.031 μs | 29.2664 μs | 1.6042 μs |  1.00 |    0.01 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 188.062 μs | 22.5538 μs | 1.2363 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 187.606 μs | 20.9220 μs | 1.1468 μs |  1.00 |    0.01 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 370.201 μs | 20.9713 μs | 1.1495 μs |  1.00 |    0.00 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 364.348 μs | 14.0404 μs | 0.7696 μs |  0.98 |    0.00 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 185.375 μs | 11.7949 μs | 0.6465 μs |  1.00 |    0.00 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 184.974 μs | 24.2948 μs | 1.3317 μs |  1.00 |    0.01 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 170.318 μs | 15.1274 μs | 0.8292 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 169.088 μs |  4.8922 μs | 0.2682 μs |  0.99 |    0.00 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 186.572 μs | 10.7102 μs | 0.5871 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 185.059 μs | 27.3354 μs | 1.4983 μs |  0.99 |    0.01 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 198.073 μs | 14.2174 μs | 0.7793 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 202.456 μs | 13.0449 μs | 0.7150 μs |  1.02 |    0.00 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 188.992 μs |  8.9739 μs | 0.4919 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 185.572 μs | 21.7404 μs | 1.1917 μs |  0.98 |    0.01 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 190.148 μs |  4.0373 μs | 0.2213 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 186.242 μs | 17.2558 μs | 0.9458 μs |  0.98 |    0.00 |         - |        0.00 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 171.145 μs | 52.0241 μs | 2.8516 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 171.657 μs |  6.4389 μs | 0.3529 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 361.801 μs | 12.1075 μs | 0.6637 μs |  1.00 |    0.00 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 360.089 μs | 45.0464 μs | 2.4691 μs |  1.00 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 186.011 μs | 26.2760 μs | 1.4403 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 187.918 μs | 16.3138 μs | 0.8942 μs |  1.01 |    0.01 |         - |        0.00 |

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
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 188.011 μs | 35.609 μs | 1.9519 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 189.298 μs | 36.431 μs | 1.9969 μs |  1.01 |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Get              | GET                  | 193.622 μs | 24.600 μs | 1.3484 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 187.486 μs |  6.609 μs | 0.3622 μs |  0.97 |    0.01 |      48 B |        0.10 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 175.353 μs | 38.974 μs | 2.1363 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 172.407 μs | 69.091 μs | 3.7871 μs |  0.98 |    0.02 |      50 B |        0.15 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.451 μs |  1.171 μs | 0.0642 μs |  1.00 |    0.01 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.441 μs |  1.180 μs | 0.0647 μs |  1.00 |    0.01 |      52 B |        0.18 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 193.268 μs | 32.924 μs | 1.8047 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 189.252 μs | 12.946 μs | 0.7096 μs |  0.98 |    0.01 |      48 B |        0.09 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 189.269 μs | 40.212 μs | 2.2041 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 189.891 μs | 15.281 μs | 0.8376 μs |  1.00 |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 189.737 μs | 15.324 μs | 0.8399 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 189.354 μs | 11.033 μs | 0.6048 μs |  1.00 |    0.00 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 376.181 μs | 70.699 μs | 3.8752 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 375.156 μs | 26.099 μs | 1.4306 μs |  1.00 |    0.01 |     256 B |        0.34 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 186.221 μs | 19.280 μs | 1.0568 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 187.464 μs | 29.814 μs | 1.6342 μs |  1.01 |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 173.250 μs | 12.338 μs | 0.6763 μs |  1.00 |    0.00 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 168.563 μs | 37.331 μs | 2.0463 μs |  0.97 |    0.01 |       2 B |       0.008 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 190.531 μs | 43.317 μs | 2.3744 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 188.666 μs | 15.979 μs | 0.8759 μs |  0.99 |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 207.210 μs | 12.053 μs | 0.6607 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 207.365 μs | 16.922 μs | 0.9276 μs |  1.00 |    0.00 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 191.274 μs | 13.725 μs | 0.7523 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 190.387 μs | 32.684 μs | 1.7915 μs |  1.00 |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 191.996 μs | 15.003 μs | 0.8224 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 192.409 μs | 18.364 μs | 1.0066 μs |  1.00 |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 176.126 μs | 35.867 μs | 1.9660 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 175.971 μs |  9.840 μs | 0.5394 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 373.876 μs | 65.673 μs | 3.5998 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 367.564 μs | 22.093 μs | 1.2110 μs |  0.98 |    0.01 |     200 B |        0.31 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 185.786 μs | 80.553 μs | 4.4154 μs |  1.00 |    0.03 |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 187.167 μs | 16.677 μs | 0.9141 μs |  1.01 |    0.02 |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
