---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 08:16 UTC from commit `237163dcc18d`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31302933788) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.63GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 194.416 μs | 32.8551 μs | 1.8009 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 193.419 μs | 21.3140 μs | 1.1683 μs |  0.99 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 197.002 μs | 31.2584 μs | 1.7134 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 193.538 μs | 22.3721 μs | 1.2263 μs |  0.98 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 177.628 μs | 41.4608 μs | 2.2726 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 177.018 μs | 27.4990 μs | 1.5073 μs |  1.00 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.497 μs |  1.8108 μs | 0.0993 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.970 μs |  0.5643 μs | 0.0309 μs |  0.90 |    0.02 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 196.150 μs | 17.1470 μs | 0.9399 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 193.575 μs | 21.4235 μs | 1.1743 μs |  0.99 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 195.520 μs | 17.6992 μs | 0.9702 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 195.646 μs | 44.9161 μs | 2.4620 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 193.544 μs | 19.7873 μs | 1.0846 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 194.588 μs | 14.1734 μs | 0.7769 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 379.724 μs | 19.7818 μs | 1.0843 μs |  1.00 |    0.00 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 374.228 μs | 88.7660 μs | 4.8656 μs |  0.99 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 192.239 μs | 33.8205 μs | 1.8538 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 190.842 μs | 17.8029 μs | 0.9758 μs |  0.99 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 176.385 μs | 27.4409 μs | 1.5041 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 175.338 μs |  0.8425 μs | 0.0462 μs |  0.99 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 193.753 μs | 24.0118 μs | 1.3162 μs |  1.00 |    0.01 |     311 B |        1.00 |
| Respire_SAdd                   | SADD                 | 194.301 μs |  5.9957 μs | 0.3286 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 205.547 μs | 22.8041 μs | 1.2500 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 207.370 μs | 16.3119 μs | 0.8941 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 195.027 μs | 24.4246 μs | 1.3388 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 196.399 μs |  9.0554 μs | 0.4964 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 196.014 μs | 15.1910 μs | 0.8327 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 195.961 μs | 23.9516 μs | 1.3129 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 179.262 μs | 28.0202 μs | 1.5359 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 177.219 μs | 53.1798 μs | 2.9150 μs |  0.99 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 375.033 μs | 11.2924 μs | 0.6190 μs |  1.00 |    0.00 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 371.783 μs | 66.6330 μs | 3.6524 μs |  0.99 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 193.975 μs |  3.1019 μs | 0.1700 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 193.172 μs | 11.1880 μs | 0.6133 μs |  1.00 |    0.00 |      32 B |        0.10 |

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
| Method                         | Categories           | Mean       | Error       | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 188.560 μs |  39.9063 μs | 2.1874 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 187.327 μs |  17.6902 μs | 0.9697 μs |  0.99 |    0.01 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get              | GET                  | 189.785 μs |  28.2245 μs | 1.5471 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 189.735 μs |   7.7370 μs | 0.4241 μs |  1.00 |    0.01 |      80 B |        0.16 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 171.916 μs | 104.2225 μs | 5.7128 μs |  1.00 |    0.04 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 170.760 μs |  29.2051 μs | 1.6008 μs |  0.99 |    0.03 |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.393 μs |   0.3201 μs | 0.0175 μs |  1.00 |    0.00 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.958 μs |   1.0954 μs | 0.0600 μs |  0.92 |    0.01 |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 192.156 μs |  37.1433 μs | 2.0359 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 190.412 μs |  11.5009 μs | 0.6304 μs |  0.99 |    0.01 |      80 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 187.365 μs |  31.5062 μs | 1.7270 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 190.182 μs |   8.9932 μs | 0.4929 μs |  1.02 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 188.146 μs |  11.4546 μs | 0.6279 μs |  1.00 |    0.00 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 188.981 μs |  13.9261 μs | 0.7633 μs |  1.00 |    0.00 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 375.672 μs |  41.1014 μs | 2.2529 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 370.171 μs |  21.2512 μs | 1.1649 μs |  0.99 |    0.01 |     256 B |        0.34 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 185.408 μs |  17.5899 μs | 0.9642 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 186.466 μs |   4.6782 μs | 0.2564 μs |  1.01 |    0.00 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 172.812 μs |  34.8071 μs | 1.9079 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 173.043 μs |  24.4746 μs | 1.3415 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 187.285 μs |  26.0572 μs | 1.4283 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 189.325 μs |   3.9286 μs | 0.2153 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 198.841 μs |  34.4126 μs | 1.8863 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 201.284 μs |  35.5286 μs | 1.9474 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 187.891 μs |  67.0313 μs | 3.6742 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 190.490 μs |  19.8874 μs | 1.0901 μs |  1.01 |    0.02 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 190.045 μs |  32.6051 μs | 1.7872 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 191.806 μs |  17.2232 μs | 0.9441 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 176.808 μs |  19.0563 μs | 1.0445 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 174.277 μs |  39.6412 μs | 2.1729 μs |  0.99 |    0.01 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 371.930 μs |  32.1442 μs | 1.7619 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 364.448 μs |  16.0712 μs | 0.8809 μs |  0.98 |    0.00 |     200 B |        0.31 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 188.580 μs |   9.0565 μs | 0.4964 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 185.121 μs | 113.2322 μs | 6.2066 μs |  0.98 |    0.03 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
