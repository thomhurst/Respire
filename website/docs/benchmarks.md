---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 22:59 UTC from commit `ca7ca63d81fe`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31340577036) for logs and downloadable artifacts.
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
| Method                         | Categories           | Mean       | Error       | StdDev     | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|-----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 194.767 μs |  15.9559 μs |  0.8746 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 193.766 μs |  12.8464 μs |  0.7042 μs |  0.99 |    0.00 |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Get              | GET                  | 197.413 μs |  11.7772 μs |  0.6455 μs |  1.00 |    0.00 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 195.984 μs |  11.0468 μs |  0.6055 μs |  0.99 |    0.00 |      80 B |        0.16 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 178.504 μs |  37.1194 μs |  2.0346 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 168.476 μs | 291.3010 μs | 15.9672 μs |  0.94 |    0.08 |      50 B |        0.15 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.612 μs |   1.0117 μs |  0.0555 μs |  1.00 |    0.01 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.007 μs |   1.3720 μs |  0.0752 μs |  0.89 |    0.01 |      52 B |        0.18 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_HGet             | HGET                 | 194.653 μs |  29.4658 μs |  1.6151 μs |  1.00 |    0.01 |     519 B |        1.00 |
| Respire_HGet                   | HGET                 | 194.123 μs |  17.0669 μs |  0.9355 μs |  1.00 |    0.01 |      80 B |        0.15 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_HSet             | HSET                 | 197.517 μs |  29.2421 μs |  1.6029 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 196.248 μs |  14.9986 μs |  0.8221 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Incr             | INCR                 | 194.693 μs |  26.1914 μs |  1.4356 μs |  1.00 |    0.01 |     295 B |        1.00 |
| Respire_Incr                   | INCR                 | 195.212 μs |  22.2776 μs |  1.2211 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 385.181 μs |  17.1590 μs |  0.9405 μs |  1.00 |    0.00 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 377.168 μs |   5.7918 μs |  0.3175 μs |  0.98 |    0.00 |     256 B |        0.34 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Ping             | PING                 | 192.705 μs |  11.6139 μs |  0.6366 μs |  1.00 |    0.00 |     303 B |        1.00 |
| Respire_Ping                   | PING                 | 192.260 μs |   0.8920 μs |  0.0489 μs |  1.00 |    0.00 |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 176.335 μs |  42.0492 μs |  2.3049 μs |  1.00 |    0.02 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 176.632 μs |  35.3416 μs |  1.9372 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 194.649 μs |  13.2979 μs |  0.7289 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 193.488 μs |  22.4152 μs |  1.2287 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 206.169 μs |  18.7082 μs |  1.0255 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 208.135 μs |  27.6464 μs |  1.5154 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 194.823 μs |  39.9726 μs |  2.1910 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 195.819 μs |   1.7784 μs |  0.0975 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 197.250 μs |  26.2113 μs |  1.4367 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 195.800 μs |  19.0763 μs |  1.0456 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 178.472 μs |  28.7162 μs |  1.5740 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 179.960 μs |  10.7664 μs |  0.5901 μs |  1.01 |    0.01 |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 378.682 μs |  23.5460 μs |  1.2906 μs |  1.00 |    0.00 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 374.268 μs |  55.5219 μs |  3.0433 μs |  0.99 |    0.01 |     200 B |        0.31 |
|                                |                      |            |             |            |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 195.199 μs |  12.4279 μs |  0.6812 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 193.554 μs |  10.6876 μs |  0.5858 μs |  0.99 |    0.00 |      32 B |        0.10 |

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
| Method                         | Categories           | Mean       | Error       | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  88.281 μs |  39.7106 μs | 2.1767 μs |  1.00 |    0.03 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               |  85.413 μs |  34.3094 μs | 1.8806 μs |  0.97 |    0.03 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get              | GET                  |  80.908 μs | 112.3396 μs | 6.1577 μs |  1.00 |    0.09 |     504 B |        1.00 |
| Respire_Get                    | GET                  |  86.694 μs |  58.4271 μs | 3.2026 μs |  1.08 |    0.08 |      80 B |        0.16 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  74.315 μs |  23.3581 μs | 1.2803 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  73.903 μs |  17.2386 μs | 0.9449 μs |  0.99 |    0.02 |      50 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   2.872 μs |   2.5199 μs | 0.1381 μs |  1.00 |    0.06 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.596 μs |   0.2611 μs | 0.0143 μs |  0.91 |    0.04 |      52 B |        0.18 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HGet             | HGET                 |  89.700 μs |   9.0945 μs | 0.4985 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  88.167 μs |   7.3910 μs | 0.4051 μs |  0.98 |    0.01 |      80 B |        0.15 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_HSet             | HSET                 |  89.148 μs |   8.5777 μs | 0.4702 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 |  88.532 μs |  43.6147 μs | 2.3907 μs |  0.99 |    0.02 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Incr             | INCR                 |  86.516 μs |  24.8415 μs | 1.3616 μs |  1.00 |    0.02 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  86.781 μs |  16.1522 μs | 0.8854 μs |  1.00 |    0.02 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 168.852 μs |  39.6515 μs | 2.1734 μs |  1.00 |    0.02 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 167.662 μs |   8.6949 μs | 0.4766 μs |  0.99 |    0.01 |     256 B |        0.34 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping             | PING                 |  86.515 μs |   2.2709 μs | 0.1245 μs |  1.00 |    0.00 |     303 B |        1.00 |
| Respire_Ping                   | PING                 |  85.749 μs |  19.5320 μs | 1.0706 μs |  0.99 |    0.01 |      32 B |        0.11 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  73.696 μs |   2.3336 μs | 0.1279 μs |  1.00 |    0.00 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  72.797 μs |  13.6678 μs | 0.7492 μs |  0.99 |    0.01 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 |  88.601 μs |  17.7171 μs | 0.9711 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 |  87.703 μs |   1.7848 μs | 0.0978 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             |  94.278 μs |   7.9054 μs | 0.4333 μs |  1.00 |    0.01 |     311 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  96.076 μs |  12.0764 μs | 0.6619 μs |  1.02 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              |  89.015 μs |  18.3316 μs | 1.0048 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  88.666 μs |  11.2064 μs | 0.6143 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  89.972 μs |  10.3875 μs | 0.5694 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  90.134 μs |  14.1420 μs | 0.7752 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  76.838 μs |   5.3784 μs | 0.2948 μs |  1.00 |    0.00 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  72.928 μs |  30.9507 μs | 1.6965 μs |  0.95 |    0.02 |       2 B |       0.008 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 167.571 μs |  36.2056 μs | 1.9846 μs |  1.00 |    0.01 |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 161.916 μs |  19.0671 μs | 1.0451 μs |  0.97 |    0.01 |     200 B |        0.31 |
|                                |                      |            |             |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            |  85.657 μs |  12.9781 μs | 0.7114 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  88.383 μs |   9.7468 μs | 0.5343 μs |  1.03 |    0.01 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
