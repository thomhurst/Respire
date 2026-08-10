---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 20:45 UTC from commit `9c86e4a041aa`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31428446846) for logs and downloadable artifacts.
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
| StackExchange_Exists           | EXISTS               | 194.535 μs | 0.6728 μs | 1.0070 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 194.958 μs | 0.7054 μs | 1.0117 μs |  1.00 | Same            |    0.01 |      - |     288 B |        0.97 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 196.738 μs | 0.7206 μs | 1.0785 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 195.510 μs | 0.6449 μs | 0.9652 μs |  0.99 | Same            |    0.01 |      - |     336 B |        0.67 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 178.726 μs | 1.6713 μs | 2.5015 μs |  1.00 | Baseline        |    0.02 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 178.024 μs | 1.1533 μs | 1.6904 μs |  1.00 | Same            |    0.02 |      - |     339 B |        1.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.456 μs | 0.0209 μs | 0.0313 μs |  1.00 | Baseline        |    0.02 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.558 μs | 0.0134 μs | 0.0196 μs |  1.04 | Same            |    0.02 | 0.0293 |     561 B |        1.94 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.437 μs | 0.0387 μs | 0.0567 μs |  1.00 | Baseline        |    0.01 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.931 μs | 0.0516 μs | 0.0757 μs |  1.09 | Slower          |    0.02 | 0.0195 |     564 B |        1.94 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 198.148 μs | 0.7960 μs | 1.1914 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 196.089 μs | 0.6794 μs | 1.0169 μs |  0.99 | Same            |    0.01 |      - |     336 B |        0.65 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 196.973 μs | 0.5131 μs | 0.7681 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 198.252 μs | 0.8374 μs | 1.2009 μs |  1.01 | Same            |    0.01 |      - |     288 B |        0.88 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 194.992 μs | 0.6462 μs | 0.9672 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 195.277 μs | 0.8168 μs | 1.1973 μs |  1.00 | Same            |    0.01 |      - |     288 B |        0.97 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 382.343 μs | 1.0647 μs | 1.5606 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 380.794 μs | 0.9502 μs | 1.4222 μs |  1.00 | Same            |    0.01 |      - |    1125 B |        1.48 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 191.273 μs | 1.2219 μs | 1.8288 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 193.152 μs | 1.0252 μs | 1.5345 μs |  1.01 | Same            |    0.01 |      - |     288 B |        0.95 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 177.248 μs | 0.8684 μs | 1.2997 μs |  1.00 | Baseline        |    0.01 |      - |     242 B |        1.00 |
| Respire_Ping_SteadyState       | PING x100 sequential | 176.624 μs | 1.0157 μs | 1.5203 μs |  1.00 | Same            |    0.01 |      - |     292 B |        1.21 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 195.163 μs | 0.7430 μs | 1.0655 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 194.379 μs | 0.9507 μs | 1.3935 μs |  1.00 | Same            |    0.01 |      - |     288 B |        0.92 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 205.957 μs | 0.7451 μs | 1.0921 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 210.338 μs | 0.7630 μs | 1.1420 μs |  1.02 | Same            |    0.01 |      - |     288 B |        0.92 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 195.124 μs | 0.5479 μs | 0.7858 μs |  1.00 | Baseline        |    0.01 |      - |     311 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 198.131 μs | 1.5713 μs | 2.3518 μs |  1.02 | Same            |    0.01 |      - |     288 B |        0.93 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 198.285 μs | 0.6620 μs | 0.9908 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 200.548 μs | 0.8786 μs | 1.2879 μs |  1.01 | Same            |    0.01 |      - |     288 B |        0.92 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 179.929 μs | 0.8602 μs | 1.2875 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |        1.00 |
| Respire_Set_SteadyState        | SET x100 sequential  | 179.632 μs | 0.9857 μs | 1.4448 μs |  1.00 | Same            |    0.01 |      - |     292 B |        1.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 376.381 μs | 1.1050 μs | 1.6540 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 378.303 μs | 1.3991 μs | 2.0942 μs |  1.01 | Same            |    0.01 |      - |    1071 B |        1.65 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 194.877 μs | 0.7709 μs | 1.1055 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 194.623 μs | 1.0538 μs | 1.5773 μs |  1.00 | Same            |    0.01 |      - |     288 B |        0.92 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 3.52GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  99.416 μs | 1.7765 μs | 2.6040 μs |  1.00 | Baseline        |    0.04 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               |  96.345 μs | 1.0343 μs | 1.5481 μs |  0.97 | Same            |    0.03 |      - |     288 B |        0.97 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 100.409 μs | 0.9627 μs | 1.4409 μs |  1.00 | Baseline        |    0.02 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  |  95.997 μs | 1.2801 μs | 1.8764 μs |  0.96 | Same            |    0.02 |      - |     336 B |        0.67 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  81.362 μs | 1.1706 μs | 1.7158 μs |  1.00 | Baseline        |    0.03 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  81.813 μs | 1.4685 μs | 2.1526 μs |  1.01 | Same            |    0.03 |      - |     339 B |        1.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.587 μs | 0.1036 μs | 0.1550 μs |  1.01 | Baseline        |    0.14 |      - |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.760 μs | 0.0785 μs | 0.1150 μs |  1.12 | Same            |    0.13 | 0.0049 |     561 B |        1.94 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.067 μs | 0.0808 μs | 0.1210 μs |  1.00 | Baseline        |    0.05 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.302 μs | 0.0549 μs | 0.0821 μs |  1.08 | Same            |    0.05 | 0.0049 |     563 B |        1.93 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 100.709 μs | 0.8424 μs | 1.2609 μs |  1.00 | Baseline        |    0.02 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  98.279 μs | 0.9556 μs | 1.3705 μs |  0.98 | Same            |    0.02 |      - |     336 B |        0.65 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 101.537 μs | 0.9817 μs | 1.4694 μs |  1.00 | Baseline        |    0.02 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 |  98.819 μs | 0.7881 μs | 1.1552 μs |  0.97 | Same            |    0.02 |      - |     288 B |        0.88 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 |  98.985 μs | 1.4770 μs | 2.2107 μs |  1.00 | Baseline        |    0.03 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  95.947 μs | 1.5163 μs | 2.1746 μs |  0.97 | Same            |    0.03 |      - |     288 B |        0.97 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 190.205 μs | 2.0027 μs | 2.9975 μs |  1.00 | Baseline        |    0.02 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 184.137 μs | 1.8990 μs | 2.7835 μs |  0.97 | Same            |    0.02 |      - |    1143 B |        1.50 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 |  97.635 μs | 1.2781 μs | 1.9129 μs |  1.00 | Baseline        |    0.03 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 |  90.304 μs | 4.4046 μs | 6.5926 μs |  0.93 | Same            |    0.07 |      - |     288 B |        0.95 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  81.358 μs | 0.8185 μs | 1.2250 μs |  1.00 | Baseline        |    0.02 |      - |     242 B |        1.00 |
| Respire_Ping_SteadyState       | PING x100 sequential |  80.745 μs | 1.0806 μs | 1.5839 μs |  0.99 | Same            |    0.02 |      - |     291 B |        1.20 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 100.148 μs | 1.0994 μs | 1.6456 μs |  1.00 | Baseline        |    0.02 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 |  96.254 μs | 0.7913 μs | 1.1349 μs |  0.96 | Same            |    0.02 |      - |     288 B |        0.92 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 107.448 μs | 1.2423 μs | 1.8594 μs |  1.00 | Baseline        |    0.02 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 101.559 μs | 0.6902 μs | 1.0330 μs |  0.95 | Same            |    0.02 |      - |     288 B |        0.92 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 102.270 μs | 0.8172 μs | 1.1719 μs |  1.00 | Baseline        |    0.02 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  98.454 μs | 1.0899 μs | 1.5976 μs |  0.96 | Same            |    0.02 |      - |     288 B |        0.92 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 103.318 μs | 1.3026 μs | 1.8681 μs |  1.00 | Baseline        |    0.03 |      - |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 100.796 μs | 0.7513 μs | 1.1245 μs |  0.98 | Same            |    0.02 |      - |     288 B |        0.93 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  86.164 μs | 1.0453 μs | 1.4992 μs |  1.00 | Baseline        |    0.02 |      - |     250 B |        1.00 |
| Respire_Set_SteadyState        | SET x100 sequential  |  85.507 μs | 1.1368 μs | 1.5937 μs |  0.99 | Same            |    0.02 |      - |     292 B |        1.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 191.388 μs | 2.1486 μs | 3.2160 μs |  1.00 | Baseline        |    0.02 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 188.868 μs | 2.3210 μs | 3.3286 μs |  0.99 | Same            |    0.02 |      - |    1083 B |        1.67 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  98.956 μs | 0.9815 μs | 1.4691 μs |  1.00 | Baseline        |    0.02 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  97.006 μs | 1.5560 μs | 2.2808 μs |  0.98 | Same            |    0.03 |      - |     288 B |        0.92 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
