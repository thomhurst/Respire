---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 22:53 UTC from commit `78221273dfe9`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31340310157) for logs and downloadable artifacts.
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
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 192.094 μs | 23.2254 μs | 1.2731 μs |  1.00 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 195.058 μs | 55.4569 μs | 3.0398 μs |  1.02 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_Get              | GET                  | 194.232 μs | 14.5679 μs | 0.7985 μs |  1.00 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 190.572 μs | 48.1084 μs | 2.6370 μs |  0.98 |      - |      80 B |        0.16 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 177.423 μs | 12.2892 μs | 0.6736 μs |  1.00 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 174.962 μs | 10.3274 μs | 0.5661 μs |  0.99 |      - |      50 B |        0.15 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.325 μs |  0.7673 μs | 0.0421 μs |  1.00 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.917 μs |  1.3614 μs | 0.0746 μs |  0.92 |      - |      52 B |        0.18 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_HGet             | HGET                 | 195.462 μs |  7.3450 μs | 0.4026 μs |  1.00 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 191.950 μs |  3.1060 μs | 0.1703 μs |  0.98 |      - |      80 B |        0.15 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_HSet             | HSET                 | 191.795 μs |  5.3896 μs | 0.2954 μs |  1.00 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 192.387 μs | 18.7418 μs | 1.0273 μs |  1.00 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_Incr             | INCR                 | 190.408 μs | 25.0440 μs | 1.3727 μs |  1.00 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 188.645 μs | 11.4229 μs | 0.6261 μs |  0.99 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 372.582 μs | 62.1612 μs | 3.4073 μs |  1.00 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 369.558 μs | 20.6920 μs | 1.1342 μs |  0.99 |      - |     256 B |        0.34 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_Ping             | PING                 | 188.518 μs | 38.5621 μs | 2.1137 μs |  1.00 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 189.224 μs | 19.1171 μs | 1.0479 μs |  1.00 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 171.525 μs | 30.2263 μs | 1.6568 μs |  1.00 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 172.867 μs |  8.5788 μs | 0.4702 μs |  1.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_SAdd             | SADD                 | 188.381 μs | 12.0041 μs | 0.6580 μs |  1.00 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 189.289 μs | 44.2724 μs | 2.4267 μs |  1.00 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 201.409 μs | 15.8014 μs | 0.8661 μs |  1.00 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 201.006 μs |  7.8318 μs | 0.4293 μs |  1.00 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 191.980 μs | 25.7523 μs | 1.4116 μs |  1.00 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 193.834 μs | 32.2987 μs | 1.7704 μs |  1.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 193.941 μs | 20.9239 μs | 1.1469 μs |  1.00 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 194.478 μs |  0.4882 μs | 0.0268 μs |  1.00 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 177.306 μs | 19.6063 μs | 1.0747 μs |  1.00 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 175.653 μs |  4.0966 μs | 0.2246 μs |  0.99 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 368.655 μs | 41.6535 μs | 2.2832 μs |  1.00 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 365.069 μs | 18.9684 μs | 1.0397 μs |  0.99 |      - |     200 B |        0.31 |
|                                |                      |            |            |           |       |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 190.395 μs |  7.5199 μs | 0.4122 μs |  1.00 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 188.966 μs | 25.1892 μs | 1.3807 μs |  0.99 |      - |      32 B |        0.10 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V45 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  59.700 μs | 22.1340 μs | 1.2132 μs |  1.00 |    0.02 |      - |     277 B |        1.00 |
| Respire_Exists                 | EXISTS               |  56.907 μs | 49.2464 μs | 2.6994 μs |  0.95 |    0.04 |      - |      32 B |        0.12 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get              | GET                  |  61.684 μs | 75.8160 μs | 4.1557 μs |  1.00 |    0.08 |      - |     487 B |        1.00 |
| Respire_Get                    | GET                  |  63.822 μs | 38.2613 μs | 2.0972 μs |  1.04 |    0.07 |      - |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  57.751 μs |  4.1462 μs | 0.2273 μs |  1.00 |    0.00 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  57.367 μs | 65.0973 μs | 3.5682 μs |  0.99 |    0.05 |      - |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   2.401 μs |  0.7578 μs | 0.0415 μs |  1.00 |    0.02 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.216 μs |  1.1409 μs | 0.0625 μs |  0.92 |    0.03 |      - |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 |  61.509 μs | 29.4264 μs | 1.6130 μs |  1.00 |    0.03 |      - |     498 B |        1.00 |
| Respire_HGet                   | HGET                 |  62.879 μs | 48.0763 μs | 2.6352 μs |  1.02 |    0.04 |      - |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 |  65.488 μs | 42.3399 μs | 2.3208 μs |  1.00 |    0.04 |      - |     319 B |        1.00 |
| Respire_HSet                   | HSET                 |  63.714 μs | 49.9370 μs | 2.7372 μs |  0.97 |    0.05 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 |  64.866 μs | 40.1112 μs | 2.1986 μs |  1.00 |    0.04 |      - |     293 B |        1.00 |
| Respire_Incr                   | INCR                 |  63.368 μs | 37.0046 μs | 2.0283 μs |  0.98 |    0.04 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 122.223 μs | 16.6621 μs | 0.9133 μs |  1.00 |    0.01 |      - |     758 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 123.533 μs | 66.7705 μs | 3.6599 μs |  1.01 |    0.03 |      - |     250 B |        0.33 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 |  65.107 μs | 30.9319 μs | 1.6955 μs |  1.00 |    0.03 |      - |     283 B |        1.00 |
| Respire_Ping                   | PING                 |  62.376 μs | 35.8173 μs | 1.9633 μs |  0.96 |    0.03 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  55.076 μs | 23.0670 μs | 1.2644 μs |  1.00 |    0.03 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  54.582 μs | 14.3487 μs | 0.7865 μs |  0.99 |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 |  66.815 μs | 72.6842 μs | 3.9841 μs |  1.00 |    0.07 |      - |     308 B |        1.00 |
| Respire_SAdd                   | SADD                 |  61.120 μs | 50.3178 μs | 2.7581 μs |  0.92 |    0.06 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             |  67.669 μs | 44.1595 μs | 2.4205 μs |  1.00 |    0.04 |      - |     301 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  64.389 μs | 34.7254 μs | 1.9034 μs |  0.95 |    0.04 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              |  65.512 μs | 29.0227 μs | 1.5908 μs |  1.00 |    0.03 |      - |     291 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  59.735 μs | 16.2749 μs | 0.8921 μs |  0.91 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  64.362 μs | 14.1650 μs | 0.7764 μs |  1.00 |    0.01 |      - |     307 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  60.464 μs | 21.0038 μs | 1.1513 μs |  0.94 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  56.535 μs | 10.5049 μs | 0.5758 μs |  1.00 |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  54.753 μs |  5.9784 μs | 0.3277 μs |  0.97 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 125.185 μs | 61.5831 μs | 3.3756 μs |  1.00 |    0.03 |      - |     636 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 119.035 μs | 43.2467 μs | 2.3705 μs |  0.95 |    0.03 |      - |     197 B |        0.31 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  64.620 μs | 33.4993 μs | 1.8362 μs |  1.00 |    0.03 |      - |     288 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  61.707 μs | 19.1522 μs | 1.0498 μs |  0.96 |    0.03 |      - |      32 B |        0.11 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
