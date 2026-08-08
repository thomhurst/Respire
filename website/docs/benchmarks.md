---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-08 21:59 UTC from commit `4c499e68b001`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31280499225) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.87GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                       | Categories         | Mean       | Error      | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------- |------------------- |-----------:|-----------:|-----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists         | EXISTS             | 120.016 μs |  67.684 μs |  3.7100 μs |  1.00 |    0.04 |      - |     386 B |        1.00 |
| Respire_Exists               | EXISTS             | 100.493 μs | 136.354 μs |  7.4740 μs |  0.84 |    0.06 |      - |    1022 B |        2.65 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Get            | GET                | 118.205 μs | 148.277 μs |  8.1275 μs |  1.00 |    0.09 |      - |     492 B |        1.00 |
| Respire_Get                  | GET                | 121.659 μs |  81.086 μs |  4.4446 μs |  1.03 |    0.07 |      - |    1073 B |        2.18 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Get_Concurrent | GET x50 concurrent |   3.978 μs |   1.098 μs |  0.0602 μs |  1.00 |    0.02 | 0.0195 |     333 B |        1.00 |
| Respire_Get_Concurrent       | GET x50 concurrent |   4.549 μs |   5.297 μs |  0.2903 μs |  1.14 |    0.07 | 0.0586 |    1028 B |        3.09 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_HGet           | HGET               | 113.325 μs |  65.138 μs |  3.5705 μs |  1.00 |    0.04 |      - |     496 B |        1.00 |
| Respire_HGet                 | HGET               | 101.924 μs |  71.103 μs |  3.8974 μs |  0.90 |    0.04 |      - |    1200 B |        2.42 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_HSet           | HSET               | 119.406 μs | 142.457 μs |  7.8085 μs |  1.00 |    0.08 |      - |     423 B |        1.00 |
| Respire_HSet                 | HSET               | 112.314 μs | 123.562 μs |  6.7728 μs |  0.94 |    0.07 |      - |    1223 B |        2.89 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Incr           | INCR               | 114.751 μs |  67.249 μs |  3.6862 μs |  1.00 |    0.04 |      - |     389 B |        1.00 |
| Respire_Incr                 | INCR               | 111.301 μs |  47.250 μs |  2.5899 μs |  0.97 |    0.03 |      - |    1036 B |        2.66 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_LPushLPop      | LPUSH+LPOP         | 235.471 μs | 138.221 μs |  7.5763 μs |  1.00 |    0.04 |      - |     758 B |        1.00 |
| Respire_LPushLPop            | LPUSH+LPOP         | 226.470 μs | 221.626 μs | 12.1481 μs |  0.96 |    0.05 |      - |    1801 B |        2.38 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Ping           | PING               |  94.068 μs | 159.160 μs |  8.7241 μs |  1.01 |    0.11 |      - |     388 B |        1.00 |
| Respire_Ping                 | PING               | 104.943 μs | 171.402 μs |  9.3951 μs |  1.12 |    0.12 |      - |     866 B |        2.23 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_SAdd           | SADD               | 114.357 μs |  85.012 μs |  4.6598 μs |  1.00 |    0.05 |      - |     394 B |        1.00 |
| Respire_SAdd                 | SADD               | 111.278 μs |  93.681 μs |  5.1350 μs |  0.97 |    0.05 |      - |    1072 B |        2.72 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Set_10KB       | SET 10KB           | 150.154 μs |  35.710 μs |  1.9574 μs |  1.00 |    0.02 |      - |     413 B |        1.00 |
| Respire_Set_10KB             | SET 10KB           | 148.904 μs |  86.484 μs |  4.7405 μs |  0.99 |    0.03 |      - |    1144 B |        2.77 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Set_Small      | SET 13B            | 113.196 μs |  93.775 μs |  5.1401 μs |  1.00 |    0.06 |      - |     412 B |        1.00 |
| Respire_Set_Small            | SET 13B            | 108.440 μs |  59.932 μs |  3.2851 μs |  0.96 |    0.05 |      - |    1148 B |        2.79 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Set_1KB        | SET 1KB            | 126.544 μs | 142.772 μs |  7.8258 μs |  1.00 |    0.08 |      - |     414 B |        1.00 |
| Respire_Set_1KB              | SET 1KB            | 110.645 μs | 107.748 μs |  5.9060 μs |  0.88 |    0.06 |      - |    1120 B |        2.71 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_SetDel         | SET+DEL            | 229.930 μs | 144.589 μs |  7.9254 μs |  1.00 |    0.04 |      - |     644 B |        1.00 |
| Respire_SetDel               | SET+DEL            | 232.846 μs | 120.395 μs |  6.5993 μs |  1.01 |    0.04 |      - |    1583 B |        2.46 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_SIsMember      | SISMEMBER          | 123.420 μs | 126.927 μs |  6.9573 μs |  1.00 |    0.07 |      - |     411 B |        1.00 |
| Respire_SIsMember            | SISMEMBER          | 104.669 μs | 132.362 μs |  7.2552 μs |  0.85 |    0.07 |      - |    1100 B |        2.68 |

## net9.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 9.0.18 (9.0.18, 9.0.1826.31522), X64 RyuJIT x86-64-v3
  ShortRun : .NET 9.0.18 (9.0.18, 9.0.1826.31522), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                       | Categories         | Mean       | Error      | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------- |------------------- |-----------:|-----------:|-----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists         | EXISTS             | 140.607 μs |  27.541 μs |  1.5096 μs |  1.00 |    0.01 |      - |     399 B |        1.00 |
| Respire_Exists               | EXISTS             | 142.008 μs |  38.592 μs |  2.1153 μs |  1.01 |    0.02 |      - |    1032 B |        2.59 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Get            | GET                | 140.614 μs | 177.490 μs |  9.7288 μs |  1.00 |    0.09 |      - |     502 B |        1.00 |
| Respire_Get                  | GET                | 144.643 μs |  51.892 μs |  2.8444 μs |  1.03 |    0.07 |      - |    1093 B |        2.18 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Get_Concurrent | GET x50 concurrent |   4.180 μs |   1.018 μs |  0.0558 μs |  1.00 |    0.02 | 0.0195 |     333 B |        1.00 |
| Respire_Get_Concurrent       | GET x50 concurrent |   4.657 μs |   1.019 μs |  0.0558 μs |  1.11 |    0.02 | 0.0586 |    1029 B |        3.09 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_HGet           | HGET               | 146.574 μs |  33.549 μs |  1.8389 μs |  1.00 |    0.02 |      - |     520 B |        1.00 |
| Respire_HGet                 | HGET               | 144.943 μs |  44.327 μs |  2.4297 μs |  0.99 |    0.02 |      - |    1215 B |        2.34 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_HSet           | HSET               | 140.445 μs | 151.615 μs |  8.3105 μs |  1.00 |    0.07 |      - |     432 B |        1.00 |
| Respire_HSet                 | HSET               | 143.207 μs |  75.694 μs |  4.1490 μs |  1.02 |    0.06 |      - |    1267 B |        2.93 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Incr           | INCR               | 144.071 μs |  27.581 μs |  1.5118 μs |  1.00 |    0.01 |      - |     400 B |        1.00 |
| Respire_Incr                 | INCR               | 144.721 μs |  57.562 μs |  3.1551 μs |  1.00 |    0.02 |      - |    1119 B |        2.80 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_LPushLPop      | LPUSH+LPOP         | 289.736 μs |   6.848 μs |  0.3754 μs |  1.00 |    0.00 |      - |     758 B |        1.00 |
| Respire_LPushLPop            | LPUSH+LPOP         | 289.729 μs | 130.579 μs |  7.1575 μs |  1.00 |    0.02 |      - |    1840 B |        2.43 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Ping           | PING               | 141.638 μs |  11.775 μs |  0.6454 μs |  1.00 |    0.01 |      - |     407 B |        1.00 |
| Respire_Ping                 | PING               | 137.242 μs |  35.943 μs |  1.9701 μs |  0.97 |    0.01 |      - |     903 B |        2.22 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_SAdd           | SADD               | 141.049 μs |  60.054 μs |  3.2917 μs |  1.00 |    0.03 |      - |     416 B |        1.00 |
| Respire_SAdd                 | SADD               | 139.547 μs | 188.991 μs | 10.3592 μs |  0.99 |    0.07 |      - |    1131 B |        2.72 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Set_10KB       | SET 10KB           | 170.114 μs |   1.304 μs |  0.0715 μs |  1.00 |    0.00 |      - |     416 B |        1.00 |
| Respire_Set_10KB             | SET 10KB           | 163.869 μs |  52.697 μs |  2.8885 μs |  0.96 |    0.01 |      - |    1167 B |        2.81 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Set_Small      | SET 13B            | 147.311 μs |  33.545 μs |  1.8387 μs |  1.00 |    0.02 |      - |     416 B |        1.00 |
| Respire_Set_Small            | SET 13B            | 145.713 μs |  88.661 μs |  4.8598 μs |  0.99 |    0.03 |      - |    1167 B |        2.81 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Set_1KB        | SET 1KB            | 146.063 μs | 156.880 μs |  8.5991 μs |  1.00 |    0.07 |      - |     416 B |        1.00 |
| Respire_Set_1KB              | SET 1KB            | 149.740 μs |  47.922 μs |  2.6268 μs |  1.03 |    0.06 |      - |    1174 B |        2.82 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_SetDel         | SET+DEL            | 282.525 μs |  17.898 μs |  0.9811 μs |  1.00 |    0.00 |      - |     647 B |        1.00 |
| Respire_SetDel               | SET+DEL            | 284.215 μs | 196.483 μs | 10.7699 μs |  1.01 |    0.03 |      - |    1578 B |        2.44 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_SIsMember      | SISMEMBER          | 144.798 μs |  20.320 μs |  1.1138 μs |  1.00 |    0.01 |      - |     415 B |        1.00 |
| Respire_SIsMember            | SISMEMBER          | 145.028 μs |  75.332 μs |  4.1292 μs |  1.00 |    0.03 |      - |    1138 B |        2.74 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
