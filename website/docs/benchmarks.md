---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-08 23:49 UTC from commit `65045096d2e1`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31284651718) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.73GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 187.183 μs | 42.2574 μs | 2.3163 μs |  1.00 |    0.02 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 190.743 μs | 14.4993 μs | 0.7948 μs |  1.02 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 191.799 μs | 42.4737 μs | 2.3281 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 192.130 μs | 27.9369 μs | 1.5313 μs |  1.00 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 174.004 μs | 16.2021 μs | 0.8881 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 172.165 μs | 42.4908 μs | 2.3291 μs |  0.99 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.192 μs |  1.1739 μs | 0.0643 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.797 μs |  1.0548 μs | 0.0578 μs |  0.92 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 193.355 μs | 15.9753 μs | 0.8757 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 191.280 μs |  4.3992 μs | 0.2411 μs |  0.99 |    0.00 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 190.766 μs | 24.5495 μs | 1.3456 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 192.539 μs |  6.3937 μs | 0.3505 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 189.909 μs | 25.3172 μs | 1.3877 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 189.894 μs | 25.6187 μs | 1.4042 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 370.254 μs | 36.5000 μs | 2.0007 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 369.785 μs | 20.0211 μs | 1.0974 μs |  1.00 |    0.01 |     576 B |        0.76 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 187.437 μs | 19.2321 μs | 1.0542 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 188.958 μs |  0.5992 μs | 0.0328 μs |  1.01 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 173.017 μs | 36.0363 μs | 1.9753 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 170.405 μs | 33.6267 μs | 1.8432 μs |  0.98 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 191.989 μs | 65.2953 μs | 3.5791 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 190.936 μs |  7.8650 μs | 0.4311 μs |  0.99 |    0.02 |      96 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 202.447 μs |  2.5108 μs | 0.1376 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 200.216 μs | 26.1778 μs | 1.4349 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 191.916 μs | 21.7902 μs | 1.1944 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 192.228 μs | 12.6497 μs | 0.6934 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 191.308 μs | 23.9620 μs | 1.3134 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 193.767 μs |  9.6910 μs | 0.5312 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 178.760 μs | 57.9793 μs | 3.1780 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 173.323 μs | 48.1191 μs | 2.6376 μs |  0.97 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 371.734 μs | 20.2721 μs | 1.1112 μs |  1.00 |    0.00 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 366.444 μs | 27.3463 μs | 1.4989 μs |  0.99 |    0.00 |     264 B |        0.41 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 189.529 μs | 10.1770 μs | 0.5578 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 190.769 μs |  7.5454 μs | 0.4136 μs |  1.01 |    0.00 |      32 B |        0.10 |

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
| StackExchange_Exists           | EXISTS               | 187.808 μs | 41.3653 μs | 2.2674 μs |  1.00 |    0.01 |     294 B |        1.00 |
| Respire_Exists                 | EXISTS               | 188.932 μs | 23.2437 μs | 1.2741 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 192.134 μs | 33.0357 μs | 1.8108 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 190.278 μs | 11.7051 μs | 0.6416 μs |  0.99 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 174.148 μs | 53.4580 μs | 2.9302 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 172.611 μs | 29.6125 μs | 1.6232 μs |  0.99 |    0.02 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.238 μs |  2.2389 μs | 0.1227 μs |  1.00 |    0.03 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.796 μs |  0.5871 μs | 0.0322 μs |  0.92 |    0.02 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 187.907 μs | 73.5012 μs | 4.0289 μs |  1.00 |    0.03 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 189.957 μs | 13.6423 μs | 0.7478 μs |  1.01 |    0.02 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 188.093 μs | 28.2863 μs | 1.5505 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 189.174 μs | 30.3853 μs | 1.6655 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 187.787 μs | 15.8751 μs | 0.8702 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 189.118 μs | 12.1369 μs | 0.6653 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 371.969 μs | 51.9298 μs | 2.8464 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 369.266 μs | 17.8761 μs | 0.9798 μs |  0.99 |    0.01 |     576 B |        0.76 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 186.086 μs | 14.9499 μs | 0.8195 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 186.277 μs |  8.0978 μs | 0.4439 μs |  1.00 |    0.00 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 171.560 μs | 10.6176 μs | 0.5820 μs |  1.00 |    0.00 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 172.589 μs | 30.3757 μs | 1.6650 μs |  1.01 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 188.328 μs | 21.0589 μs | 1.1543 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 190.237 μs | 19.9830 μs | 1.0953 μs |  1.01 |    0.01 |      96 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 198.201 μs |  9.4458 μs | 0.5178 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 200.062 μs | 18.6038 μs | 1.0197 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 189.786 μs | 28.1898 μs | 1.5452 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 190.063 μs |  4.8051 μs | 0.2634 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 187.886 μs |  8.2220 μs | 0.4507 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 185.904 μs | 86.3833 μs | 4.7350 μs |  0.99 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 175.028 μs | 46.4289 μs | 2.5449 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 175.426 μs | 42.9802 μs | 2.3559 μs |  1.00 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 369.063 μs | 27.7625 μs | 1.5218 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 364.187 μs | 54.2282 μs | 2.9724 μs |  0.99 |    0.01 |     264 B |        0.41 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 187.315 μs | 16.9894 μs | 0.9312 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 187.821 μs | 24.2354 μs | 1.3284 μs |  1.00 |    0.01 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
