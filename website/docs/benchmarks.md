---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 00:17 UTC from commit `dd0113f4fb0b`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31343810486) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
Intel Xeon 6973P-C 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v4

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               |  94.073 μs |  3.4020 μs | 0.1865 μs |  1.00 |    0.00 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               |  87.638 μs | 15.8611 μs | 0.8694 μs |  0.93 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  |  93.956 μs | 21.3709 μs | 1.1714 μs |  1.00 |    0.02 |     504 B |        1.00 |
| Respire_Get                    | GET                  |  88.056 μs | 25.0589 μs | 1.3736 μs |  0.94 |    0.02 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  76.924 μs | 23.8194 μs | 1.3056 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  77.530 μs |  2.6261 μs | 0.1439 μs |  1.01 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   2.603 μs |  0.6660 μs | 0.0365 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   2.405 μs |  0.3011 μs | 0.0165 μs |  0.92 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 |  98.234 μs |  6.8214 μs | 0.3739 μs |  1.00 |    0.00 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 |  91.782 μs |  6.3559 μs | 0.3484 μs |  0.93 |    0.00 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 |  95.693 μs | 51.7836 μs | 2.8384 μs |  1.00 |    0.04 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 |  88.535 μs | 50.3464 μs | 2.7597 μs |  0.93 |    0.03 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 |  94.505 μs | 36.0254 μs | 1.9747 μs |  1.00 |    0.03 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 |  89.007 μs | 17.6665 μs | 0.9684 μs |  0.94 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 178.794 μs | 53.7705 μs | 2.9473 μs |  1.00 |    0.02 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 180.354 μs | 35.5044 μs | 1.9461 μs |  1.01 |    0.02 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 |  96.661 μs | 54.0006 μs | 2.9600 μs |  1.00 |    0.04 |     304 B |        1.00 |
| Respire_Ping                   | PING                 |  91.766 μs | 32.4121 μs | 1.7766 μs |  0.95 |    0.03 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  80.426 μs | 53.7237 μs | 2.9448 μs |  1.00 |    0.04 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  77.636 μs | 15.7675 μs | 0.8643 μs |  0.97 |    0.03 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 |  93.225 μs | 51.1926 μs | 2.8060 μs |  1.00 |    0.04 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 |  90.310 μs | 25.2360 μs | 1.3833 μs |  0.97 |    0.03 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             |  99.367 μs | 18.9671 μs | 1.0396 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             |  92.439 μs |  0.7618 μs | 0.0418 μs |  0.93 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              |  92.130 μs | 22.9112 μs | 1.2558 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              |  88.446 μs | 20.9824 μs | 1.1501 μs |  0.96 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              |  94.216 μs | 33.5577 μs | 1.8394 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              |  90.865 μs | 41.8733 μs | 2.2952 μs |  0.96 |    0.03 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  80.872 μs | 22.9252 μs | 1.2566 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  78.931 μs | 21.4406 μs | 1.1752 μs |  0.98 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 182.450 μs |  6.8509 μs | 0.3755 μs |  1.00 |    0.00 |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 173.229 μs | 39.4979 μs | 2.1650 μs |  0.95 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 100.317 μs | 39.0163 μs | 2.1386 μs |  1.00 |    0.03 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            |  90.192 μs |  8.9460 μs | 0.4904 μs |  0.90 |    0.02 |      32 B |        0.10 |

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
| StackExchange_Exists           | EXISTS               | 194.014 μs | 16.862 μs | 0.9243 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 195.446 μs | 20.410 μs | 1.1188 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Get              | GET                  | 198.610 μs |  5.848 μs | 0.3206 μs |  1.00 |    0.00 |     503 B |        1.00 |
| Respire_Get                    | GET                  | 198.703 μs |  6.434 μs | 0.3527 μs |  1.00 |    0.00 |      80 B |        0.16 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 181.242 μs | 14.028 μs | 0.7689 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 183.043 μs | 16.109 μs | 0.8830 μs |  1.01 |    0.01 |      50 B |        0.15 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.667 μs |  1.364 μs | 0.0747 μs |  1.00 |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.576 μs |  1.407 μs | 0.0771 μs |  0.98 |    0.02 |      52 B |        0.18 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 202.056 μs | 33.185 μs | 1.8190 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 199.388 μs | 13.927 μs | 0.7634 μs |  0.99 |    0.01 |      80 B |        0.15 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 197.032 μs | 46.526 μs | 2.5503 μs |  1.00 |    0.02 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 198.872 μs | 12.228 μs | 0.6703 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 194.719 μs |  5.566 μs | 0.3051 μs |  1.00 |    0.00 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 198.392 μs |  5.935 μs | 0.3253 μs |  1.02 |    0.00 |      32 B |        0.11 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 394.888 μs | 79.565 μs | 4.3612 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 387.508 μs | 65.550 μs | 3.5930 μs |  0.98 |    0.01 |     256 B |        0.34 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 192.723 μs | 12.442 μs | 0.6820 μs |  1.00 |    0.00 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 194.737 μs | 44.997 μs | 2.4664 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 178.863 μs | 47.813 μs | 2.6208 μs |  1.00 |    0.02 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 178.935 μs | 10.629 μs | 0.5826 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 192.899 μs | 16.199 μs | 0.8879 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 198.437 μs | 11.646 μs | 0.6384 μs |  1.03 |    0.01 |      32 B |        0.10 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 207.726 μs | 17.811 μs | 0.9763 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 213.890 μs |  2.046 μs | 0.1121 μs |  1.03 |    0.00 |      32 B |        0.10 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 195.989 μs | 12.917 μs | 0.7080 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 199.774 μs | 14.690 μs | 0.8052 μs |  1.02 |    0.00 |      32 B |        0.10 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 198.629 μs | 21.354 μs | 1.1705 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 199.754 μs | 44.597 μs | 2.4445 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 181.015 μs | 58.403 μs | 3.2013 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 182.979 μs | 25.154 μs | 1.3788 μs |  1.01 |    0.02 |       2 B |       0.008 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 385.146 μs | 75.789 μs | 4.1542 μs |  1.00 |    0.01 |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 383.623 μs | 28.525 μs | 1.5636 μs |  1.00 |    0.01 |     200 B |        0.31 |
|                                |                      |            |           |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 193.600 μs |  8.690 μs | 0.4763 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 196.864 μs | 20.922 μs | 1.1468 μs |  1.02 |    0.01 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
