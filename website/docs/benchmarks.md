---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 17:00 UTC from commit `b0934cc5d5fa`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31409678100) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 184.499 μs | 0.7186 μs | 1.0073 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 183.671 μs | 0.5903 μs | 0.8653 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 186.253 μs | 0.5928 μs | 0.8690 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 184.388 μs | 0.6575 μs | 0.9637 μs |  0.99 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 171.116 μs | 1.9348 μs | 2.8959 μs |  1.00 | Baseline        |    0.02 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 168.008 μs | 1.2886 μs | 1.8480 μs |  0.98 | Same            |    0.02 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.316 μs | 0.0798 μs | 0.1194 μs |  1.00 | Baseline        |    0.07 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.022 μs | 0.0070 μs | 0.0098 μs |  0.88 | Faster          |    0.05 |      - |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.158 μs | 0.0389 μs | 0.0558 μs |  1.00 | Baseline        |    0.02 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.041 μs | 0.0208 μs | 0.0291 μs |  0.98 | Same            |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 186.988 μs | 0.4489 μs | 0.6580 μs |  1.00 | Baseline        |    0.00 |      - |     519 B |        1.00 |
| Respire_HGet                   | HGET                 | 184.964 μs | 0.7126 μs | 1.0667 μs |  0.99 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 186.651 μs | 0.5357 μs | 0.7852 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 185.955 μs | 1.3691 μs | 1.9635 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 185.182 μs | 0.8049 μs | 1.2047 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 184.380 μs | 0.7337 μs | 1.0755 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 363.874 μs | 1.2244 μs | 1.8327 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 359.734 μs | 0.8576 μs | 1.2836 μs |  0.99 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 183.966 μs | 0.6656 μs | 0.9756 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 182.809 μs | 0.7141 μs | 1.0688 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 169.510 μs | 1.1160 μs | 1.6358 μs |  1.00 | Baseline        |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 166.506 μs | 1.4962 μs | 2.2395 μs |  0.98 | Same            |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 185.645 μs | 0.5396 μs | 0.8077 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 183.691 μs | 0.8387 μs | 1.2294 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 198.121 μs | 1.2672 μs | 1.8967 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 199.273 μs | 0.9371 μs | 1.3439 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 186.279 μs | 0.5837 μs | 0.8555 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 184.069 μs | 0.6991 μs | 0.9800 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 187.999 μs | 0.5024 μs | 0.7519 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 184.732 μs | 0.4913 μs | 0.7201 μs |  0.98 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 172.416 μs | 0.7890 μs | 1.1809 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 169.950 μs | 1.0674 μs | 1.5976 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 360.458 μs | 1.0349 μs | 1.5489 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 356.068 μs | 0.7548 μs | 1.0825 μs |  0.99 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 184.995 μs | 0.8840 μs | 1.2958 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 184.531 μs | 0.4504 μs | 0.6459 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  98.518 μs | 2.6691 μs | 3.9951 μs |  1.00 | Baseline        |    0.06 |      - |     292 B |        1.00 |
| Respire_Exists                 | EXISTS               | 105.359 μs | 2.5869 μs | 3.8719 μs |  1.07 | Same            |    0.06 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  |  93.184 μs | 4.6316 μs | 6.9323 μs |  1.01 | Baseline        |    0.11 |      - |     483 B |        1.00 |
| Respire_Get                    | GET                  | 108.994 μs | 1.8227 μs | 2.7282 μs |  1.18 | Slower          |    0.09 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  90.338 μs | 2.4167 μs | 3.5424 μs |  1.00 | Baseline        |    0.05 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  85.444 μs | 2.0655 μs | 3.0915 μs |  0.95 | Same            |    0.05 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.616 μs | 0.0644 μs | 0.0964 μs |  1.00 | Baseline        |    0.08 | 0.0146 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.437 μs | 0.0065 μs | 0.0091 μs |  0.89 | Faster          |    0.05 | 0.0024 |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.416 μs | 0.0677 μs | 0.0992 μs |  1.00 | Baseline        |    0.04 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.259 μs | 0.0646 μs | 0.0947 μs |  0.96 | Same            |    0.04 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 107.146 μs | 3.0965 μs | 4.6347 μs |  1.00 | Baseline        |    0.06 |      - |     516 B |        1.00 |
| Respire_HGet                   | HGET                 | 108.156 μs | 2.4584 μs | 3.6796 μs |  1.01 | Same            |    0.06 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 103.224 μs | 2.8206 μs | 4.2217 μs |  1.00 | Baseline        |    0.06 |      - |     320 B |        1.00 |
| Respire_HSet                   | HSET                 | 111.595 μs | 1.3808 μs | 2.0667 μs |  1.08 | Same            |    0.05 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 104.379 μs | 2.4970 μs | 3.6601 μs |  1.00 | Baseline        |    0.05 |      - |     295 B |        1.00 |
| Respire_Incr                   | INCR                 | 102.635 μs | 3.1942 μs | 4.6820 μs |  0.98 | Same            |    0.06 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 199.355 μs | 4.0780 μs | 6.1037 μs |  1.00 | Baseline        |    0.04 |      - |     759 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 208.954 μs | 2.9587 μs | 4.4284 μs |  1.05 | Same            |    0.04 |      - |     253 B |        0.33 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 |  95.795 μs | 2.1782 μs | 3.2603 μs |  1.00 | Baseline        |    0.05 |      - |     299 B |        1.00 |
| Respire_Ping                   | PING                 | 104.084 μs | 2.2840 μs | 3.4186 μs |  1.09 | Same            |    0.05 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  84.313 μs | 2.2997 μs | 3.4421 μs |  1.00 | Baseline        |    0.06 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  83.092 μs | 1.3262 μs | 1.9850 μs |  0.99 | Same            |    0.05 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 103.005 μs | 1.8739 μs | 2.8047 μs |  1.00 | Baseline        |    0.04 |      - |     306 B |        1.00 |
| Respire_SAdd                   | SADD                 | 109.006 μs | 1.9588 μs | 2.9319 μs |  1.06 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 121.255 μs | 2.6869 μs | 4.0216 μs |  1.00 | Baseline        |    0.05 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 120.684 μs | 2.2868 μs | 3.4227 μs |  1.00 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 102.628 μs | 1.2046 μs | 1.8030 μs |  1.00 | Baseline        |    0.02 |      - |     310 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 106.379 μs | 3.2746 μs | 4.9013 μs |  1.04 | Same            |    0.05 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 107.048 μs | 1.7637 μs | 2.6399 μs |  1.00 | Baseline        |    0.03 |      - |     310 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 110.621 μs | 1.7743 μs | 2.6557 μs |  1.03 | Same            |    0.04 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  91.147 μs | 1.6810 μs | 2.5161 μs |  1.00 | Baseline        |    0.04 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  87.058 μs | 2.0527 μs | 3.0088 μs |  0.96 | Same            |    0.04 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 193.123 μs | 5.1644 μs | 7.7298 μs |  1.00 | Baseline        |    0.06 |      - |     643 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 204.915 μs | 4.0796 μs | 6.1062 μs |  1.06 | Same            |    0.05 |      - |     198 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 103.131 μs | 1.8164 μs | 2.7187 μs |  1.00 | Baseline        |    0.04 |      - |     307 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 104.678 μs | 2.3778 μs | 3.5589 μs |  1.02 | Same            |    0.04 |      - |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
