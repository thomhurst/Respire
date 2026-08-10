---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 00:42 UTC from commit `ea35dcdcee10`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31344951418) for logs and downloadable artifacts.
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
| StackExchange_Exists           | EXISTS               | 186.634 μs | 29.5349 μs | 1.6189 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 188.601 μs | 45.2917 μs | 2.4826 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 189.690 μs | 25.6790 μs | 1.4076 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 187.841 μs | 21.3148 μs | 1.1683 μs |  0.99 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 173.920 μs | 46.6489 μs | 2.5570 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 171.230 μs |  7.5619 μs | 0.4145 μs |  0.98 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.408 μs |  0.7083 μs | 0.0388 μs |  1.00 |    0.01 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.345 μs |  1.9093 μs | 0.1047 μs |  0.99 |    0.02 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 189.676 μs |  7.4483 μs | 0.4083 μs |  1.00 |    0.00 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 190.871 μs |  4.2475 μs | 0.2328 μs |  1.01 |    0.00 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 190.250 μs | 19.1101 μs | 1.0475 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 191.257 μs | 15.5269 μs | 0.8511 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 187.824 μs |  7.1830 μs | 0.3937 μs |  1.00 |    0.00 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 188.399 μs | 12.5441 μs | 0.6876 μs |  1.00 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 368.601 μs | 18.6351 μs | 1.0215 μs |  1.00 |    0.00 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 364.083 μs | 22.2934 μs | 1.2220 μs |  0.99 |    0.00 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 185.874 μs | 17.9972 μs | 0.9865 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 187.555 μs | 36.6531 μs | 2.0091 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 168.700 μs | 32.0253 μs | 1.7554 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 170.259 μs | 56.0543 μs | 3.0725 μs |  1.01 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 187.427 μs |  7.7271 μs | 0.4235 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 189.055 μs | 31.7105 μs | 1.7382 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 198.716 μs | 27.7103 μs | 1.5189 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 203.295 μs | 37.8391 μs | 2.0741 μs |  1.02 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 190.641 μs |  4.8545 μs | 0.2661 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 189.985 μs | 23.5321 μs | 1.2899 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 191.910 μs |  4.2443 μs | 0.2326 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 189.677 μs | 33.4526 μs | 1.8336 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 173.113 μs | 78.9511 μs | 4.3276 μs |  1.00 |    0.03 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 171.275 μs | 15.3094 μs | 0.8392 μs |  0.99 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 361.869 μs | 53.3216 μs | 2.9227 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 364.205 μs | 46.3169 μs | 2.5388 μs |  1.01 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 188.146 μs | 18.5442 μs | 1.0165 μs |  1.00 |    0.01 |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 190.699 μs | 23.7094 μs | 1.2996 μs |  1.01 |    0.01 |      32 B |        0.10 |

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
| StackExchange_Exists           | EXISTS               | 188.548 μs | 77.5916 μs | 4.2531 μs |  1.00 |    0.03 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 191.624 μs | 44.8621 μs | 2.4590 μs |  1.02 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 196.451 μs | 30.4608 μs | 1.6697 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 191.203 μs | 18.4527 μs | 1.0115 μs |  0.97 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 177.811 μs | 54.9760 μs | 3.0134 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 176.748 μs | 27.4691 μs | 1.5057 μs |  0.99 |    0.02 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.491 μs |  1.2185 μs | 0.0668 μs |  1.00 |    0.01 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.316 μs |  1.1304 μs | 0.0620 μs |  0.97 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 195.941 μs | 32.2214 μs | 1.7662 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 192.886 μs | 35.4367 μs | 1.9424 μs |  0.98 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 191.142 μs | 30.2825 μs | 1.6599 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 193.617 μs | 17.8172 μs | 0.9766 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 192.102 μs | 15.9716 μs | 0.8755 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 192.496 μs |  5.3860 μs | 0.2952 μs |  1.00 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 377.038 μs | 97.7547 μs | 5.3583 μs |  1.00 |    0.02 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 378.448 μs | 11.8220 μs | 0.6480 μs |  1.00 |    0.01 |     255 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 189.374 μs | 11.4278 μs | 0.6264 μs |  1.00 |    0.00 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 191.422 μs | 14.6207 μs | 0.8014 μs |  1.01 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 176.458 μs | 11.2333 μs | 0.6157 μs |  1.00 |    0.00 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 174.558 μs |  9.3510 μs | 0.5126 μs |  0.99 |    0.00 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 191.986 μs |  0.8792 μs | 0.0482 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 192.541 μs |  9.3124 μs | 0.5104 μs |  1.00 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 202.477 μs | 24.3179 μs | 1.3329 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 209.545 μs | 21.9615 μs | 1.2038 μs |  1.03 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 192.816 μs |  4.8177 μs | 0.2641 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 195.058 μs |  4.5944 μs | 0.2518 μs |  1.01 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 194.472 μs | 38.4932 μs | 2.1099 μs |  1.00 |    0.01 |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 196.577 μs | 13.9305 μs | 0.7636 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 180.309 μs | 78.6804 μs | 4.3127 μs |  1.00 |    0.03 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 177.563 μs | 93.5538 μs | 5.1280 μs |  0.99 |    0.03 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 378.737 μs | 63.6351 μs | 3.4881 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 369.679 μs | 92.0815 μs | 5.0473 μs |  0.98 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 179.971 μs | 72.8573 μs | 3.9936 μs |  1.00 |    0.03 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 192.327 μs | 43.3540 μs | 2.3764 μs |  1.07 |    0.02 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
