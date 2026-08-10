---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 00:14 UTC from commit `230548aca019`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31343696324) for logs and downloadable artifacts.
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
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 184.356 μs | 28.3709 μs | 1.5551 μs |  1.00 |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 186.842 μs |  9.1001 μs | 0.4988 μs |  1.01 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 187.611 μs | 10.4008 μs | 0.5701 μs |  1.00 |    0.00 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 185.704 μs | 17.1714 μs | 0.9412 μs |  0.99 |    0.01 |      - |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 172.572 μs | 23.6780 μs | 1.2979 μs |  1.00 |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 168.664 μs | 13.6678 μs | 0.7492 μs |  0.98 |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.219 μs |  0.6968 μs | 0.0382 μs |  1.00 |    0.01 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.176 μs |  1.0041 μs | 0.0550 μs |  0.99 |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 189.281 μs | 12.4330 μs | 0.6815 μs |  1.00 |    0.00 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 182.953 μs | 21.3438 μs | 1.1699 μs |  0.97 |    0.01 |      - |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 187.802 μs | 35.2291 μs | 1.9310 μs |  1.00 |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 190.159 μs | 17.5369 μs | 0.9613 μs |  1.01 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 186.020 μs | 19.5725 μs | 1.0728 μs |  1.00 |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 188.305 μs | 10.2048 μs | 0.5594 μs |  1.01 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 365.876 μs | 27.9290 μs | 1.5309 μs |  1.00 |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 362.407 μs | 11.7023 μs | 0.6414 μs |  0.99 |    0.00 |      - |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 184.395 μs |  7.6571 μs | 0.4197 μs |  1.00 |    0.00 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 187.685 μs | 10.9242 μs | 0.5988 μs |  1.02 |    0.00 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 170.804 μs | 15.5589 μs | 0.8528 μs |  1.00 |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 168.841 μs | 17.2249 μs | 0.9442 μs |  0.99 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 186.166 μs | 13.5457 μs | 0.7425 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 186.861 μs | 17.0237 μs | 0.9331 μs |  1.00 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 198.002 μs | 23.3557 μs | 1.2802 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 201.911 μs | 16.4355 μs | 0.9009 μs |  1.02 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 186.659 μs | 52.4469 μs | 2.8748 μs |  1.00 |    0.02 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 187.823 μs |  8.1706 μs | 0.4479 μs |  1.01 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 190.123 μs | 25.6702 μs | 1.4071 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 187.216 μs | 25.8240 μs | 1.4155 μs |  0.98 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 173.643 μs | 21.3949 μs | 1.1727 μs |  1.00 |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 171.476 μs | 17.5289 μs | 0.9608 μs |  0.99 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 364.408 μs | 42.1155 μs | 2.3085 μs |  1.00 |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 360.194 μs | 12.7593 μs | 0.6994 μs |  0.99 |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 186.692 μs |  5.0034 μs | 0.2743 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 189.108 μs |  7.5376 μs | 0.4132 μs |  1.01 |    0.00 |      - |      32 B |        0.10 |

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
| StackExchange_Exists           | EXISTS               | 191.096 μs | 39.1916 μs | 2.1482 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 191.155 μs | 32.5498 μs | 1.7842 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 195.059 μs |  2.3503 μs | 0.1288 μs |  1.00 |    0.00 |     501 B |        1.00 |
| Respire_Get                    | GET                  | 188.872 μs |  4.5818 μs | 0.2511 μs |  0.97 |    0.00 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 174.875 μs | 74.2620 μs | 4.0706 μs |  1.00 |    0.03 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 177.671 μs | 14.3306 μs | 0.7855 μs |  1.02 |    0.02 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.645 μs |  2.8998 μs | 0.1589 μs |  1.00 |    0.03 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.524 μs |  0.9416 μs | 0.0516 μs |  0.98 |    0.02 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 191.006 μs |  9.1360 μs | 0.5008 μs |  1.00 |    0.00 |     519 B |        1.00 |
| Respire_HGet                   | HGET                 | 191.426 μs | 35.7957 μs | 1.9621 μs |  1.00 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 193.937 μs | 26.5154 μs | 1.4534 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 193.597 μs | 34.3414 μs | 1.8824 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 193.674 μs | 16.0602 μs | 0.8803 μs |  1.00 |    0.01 |     295 B |        1.00 |
| Respire_Incr                   | INCR                 | 193.357 μs | 13.4187 μs | 0.7355 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 385.752 μs | 73.6570 μs | 4.0374 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 377.743 μs | 43.5028 μs | 2.3845 μs |  0.98 |    0.01 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 187.777 μs | 98.7131 μs | 5.4108 μs |  1.00 |    0.04 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 191.157 μs |  2.4174 μs | 0.1325 μs |  1.02 |    0.03 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 177.636 μs | 25.6371 μs | 1.4053 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 177.124 μs | 48.9853 μs | 2.6851 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 192.538 μs | 39.5745 μs | 2.1692 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 193.082 μs | 11.4147 μs | 0.6257 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 205.380 μs |  6.5668 μs | 0.3599 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 207.888 μs | 19.1532 μs | 1.0499 μs |  1.01 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 194.936 μs |  5.8889 μs | 0.3228 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 195.126 μs | 31.0829 μs | 1.7038 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 197.273 μs | 19.3204 μs | 1.0590 μs |  1.00 |    0.01 |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 194.868 μs | 30.8745 μs | 1.6923 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 180.160 μs | 58.4244 μs | 3.2024 μs |  1.00 |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 180.003 μs | 29.3018 μs | 1.6061 μs |  1.00 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 378.307 μs | 23.4392 μs | 1.2848 μs |  1.00 |    0.00 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 368.609 μs | 44.5849 μs | 2.4439 μs |  0.97 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 189.672 μs | 48.3095 μs | 2.6480 μs |  1.00 |    0.02 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 190.669 μs | 39.6975 μs | 2.1760 μs |  1.01 |    0.02 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
