---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 09:38 UTC from commit `6891bfbb1f63`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31374352143) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.30GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 108.353 μs | 29.407 μs | 1.6119 μs |  1.00 |    0.02 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 101.632 μs | 78.246 μs | 4.2889 μs |  0.94 |    0.04 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Get              | GET                  | 110.681 μs | 11.855 μs | 0.6498 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 104.476 μs |  4.829 μs | 0.2647 μs |  0.94 |    0.01 |      48 B |        0.10 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  96.186 μs | 22.924 μs | 1.2565 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  90.760 μs | 22.203 μs | 1.2170 μs |  0.94 |    0.02 |      50 B |        0.15 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.464 μs |  1.216 μs | 0.0666 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.146 μs |  1.307 μs | 0.0717 μs |  0.91 |    0.02 |      52 B |        0.18 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 110.410 μs | 25.810 μs | 1.4147 μs |  1.00 |    0.02 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 105.433 μs | 32.063 μs | 1.7575 μs |  0.96 |    0.02 |      48 B |        0.09 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 111.240 μs |  8.372 μs | 0.4589 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 106.581 μs |  6.983 μs | 0.3828 μs |  0.96 |    0.00 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 110.246 μs | 35.414 μs | 1.9412 μs |  1.00 |    0.02 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 106.624 μs |  7.449 μs | 0.4083 μs |  0.97 |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 214.765 μs | 15.673 μs | 0.8591 μs |  1.00 |    0.00 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 209.317 μs | 16.286 μs | 0.8927 μs |  0.97 |    0.00 |     256 B |        0.34 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 109.043 μs | 15.884 μs | 0.8707 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 104.946 μs | 28.232 μs | 1.5475 μs |  0.96 |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  95.783 μs | 31.659 μs | 1.7354 μs |  1.00 |    0.02 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  93.029 μs |  7.513 μs | 0.4118 μs |  0.97 |    0.02 |       2 B |       0.008 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 109.633 μs | 32.764 μs | 1.7959 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 105.205 μs | 12.861 μs | 0.7049 μs |  0.96 |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 115.392 μs |  7.775 μs | 0.4262 μs |  1.00 |    0.00 |     311 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 109.211 μs | 21.431 μs | 1.1747 μs |  0.95 |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 110.666 μs | 13.859 μs | 0.7596 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 105.205 μs | 23.868 μs | 1.3083 μs |  0.95 |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 111.132 μs | 18.142 μs | 0.9944 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 104.970 μs | 31.603 μs | 1.7323 μs |  0.94 |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  99.376 μs |  8.745 μs | 0.4793 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  93.441 μs | 17.281 μs | 0.9472 μs |  0.94 |    0.01 |       2 B |       0.008 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 207.428 μs | 47.841 μs | 2.6223 μs |  1.00 |    0.02 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 208.933 μs | 21.342 μs | 1.1698 μs |  1.01 |    0.01 |     200 B |        0.31 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 110.618 μs | 16.640 μs | 0.9121 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 102.815 μs | 35.074 μs | 1.9225 μs |  0.93 |    0.02 |         - |        0.00 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.30GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  89.806 μs | 28.498 μs | 1.5621 μs |  1.00 |    0.02 |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               |  82.603 μs | 43.281 μs | 2.3724 μs |  0.92 |    0.03 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Get              | GET                  |  92.798 μs |  9.684 μs | 0.5308 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  |  89.223 μs |  4.305 μs | 0.2360 μs |  0.96 |    0.01 |      48 B |        0.10 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  75.402 μs | 18.421 μs | 1.0097 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  73.158 μs | 91.703 μs | 5.0265 μs |  0.97 |    0.06 |      50 B |        0.15 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   2.981 μs |  3.654 μs | 0.2003 μs |  1.00 |    0.08 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.780 μs |  1.722 μs | 0.0944 μs |  0.94 |    0.06 |      52 B |        0.18 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_HGet             | HGET                 |  94.965 μs | 11.459 μs | 0.6281 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  90.489 μs | 19.296 μs | 1.0577 μs |  0.95 |    0.01 |      48 B |        0.09 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_HSet             | HSET                 |  90.039 μs |  6.004 μs | 0.3291 μs |  1.00 |    0.00 |     326 B |        1.00 |
| Respire_HSet                   | HSET                 |  79.500 μs | 52.712 μs | 2.8893 μs |  0.88 |    0.03 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Incr             | INCR                 |  89.928 μs |  4.349 μs | 0.2384 μs |  1.00 |    0.00 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  90.004 μs |  2.924 μs | 0.1603 μs |  1.00 |    0.00 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 171.778 μs | 36.736 μs | 2.0136 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 164.453 μs | 33.478 μs | 1.8350 μs |  0.96 |    0.01 |     256 B |        0.34 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Ping             | PING                 |  88.950 μs | 43.164 μs | 2.3660 μs |  1.00 |    0.03 |     304 B |        1.00 |
| Respire_Ping                   | PING                 |  86.478 μs | 15.738 μs | 0.8626 μs |  0.97 |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  76.768 μs | 15.576 μs | 0.8538 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  74.485 μs | 15.681 μs | 0.8596 μs |  0.97 |    0.01 |       2 B |       0.008 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 |  92.618 μs | 39.389 μs | 2.1590 μs |  1.00 |    0.03 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 |  88.362 μs | 14.470 μs | 0.7931 μs |  0.95 |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             |  97.969 μs |  6.275 μs | 0.3439 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  94.601 μs |  9.188 μs | 0.5036 μs |  0.97 |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              |  93.244 μs | 23.318 μs | 1.2781 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  93.035 μs | 11.614 μs | 0.6366 μs |  1.00 |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  94.760 μs | 37.410 μs | 2.0506 μs |  1.00 |    0.03 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  90.503 μs | 22.447 μs | 1.2304 μs |  0.96 |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  79.391 μs |  6.935 μs | 0.3801 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  76.201 μs |  2.137 μs | 0.1172 μs |  0.96 |    0.00 |       2 B |       0.008 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 174.218 μs | 63.684 μs | 3.4908 μs |  1.00 |    0.02 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 164.799 μs | 60.012 μs | 3.2895 μs |  0.95 |    0.02 |     199 B |        0.31 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  90.969 μs | 34.110 μs | 1.8697 μs |  1.00 |    0.03 |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  83.342 μs | 30.484 μs | 1.6709 μs |  0.92 |    0.02 |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
