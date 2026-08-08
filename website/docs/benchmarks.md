---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-08 23:55 UTC from commit `5b2302d0fed0`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31284867844) for logs and downloadable artifacts.
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
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 176.487 μs | 23.0732 μs | 1.2647 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 177.105 μs | 10.5378 μs | 0.5776 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 178.657 μs | 18.3662 μs | 1.0067 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 177.581 μs | 21.9491 μs | 1.2031 μs |  0.99 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 160.363 μs | 23.6663 μs | 1.2972 μs |  1.00 |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 155.866 μs |  6.5888 μs | 0.3612 μs |  0.97 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.826 μs |  2.3871 μs | 0.1308 μs |  1.00 |    0.03 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.379 μs |  0.6502 μs | 0.0356 μs |  0.91 |    0.02 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 179.391 μs | 10.0138 μs | 0.5489 μs |  1.00 |    0.00 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 178.710 μs | 18.5429 μs | 1.0164 μs |  1.00 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 179.940 μs |  7.6093 μs | 0.4171 μs |  1.00 |    0.00 |     327 B |        1.00 |
| Respire_HSet                   | HSET                 | 180.420 μs | 64.8161 μs | 3.5528 μs |  1.00 |    0.02 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 177.743 μs | 17.6929 μs | 0.9698 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 177.699 μs | 15.4947 μs | 0.8493 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 347.265 μs | 30.8547 μs | 1.6913 μs |  1.00 |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 343.339 μs | 38.1544 μs | 2.0914 μs |  0.99 |    0.01 |     576 B |        0.76 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 176.185 μs | 16.2362 μs | 0.8900 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 176.461 μs |  9.4402 μs | 0.5175 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 157.588 μs | 23.8910 μs | 1.3095 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 157.282 μs |  1.5337 μs | 0.0841 μs |  1.00 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 177.812 μs |  5.8694 μs | 0.3217 μs |  1.00 |    0.00 |     310 B |        1.00 |
| Respire_SAdd                   | SADD                 | 177.297 μs | 35.6357 μs | 1.9533 μs |  1.00 |    0.01 |      96 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 188.004 μs | 13.6781 μs | 0.7497 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 187.770 μs | 26.5185 μs | 1.4536 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 179.456 μs | 31.8172 μs | 1.7440 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 178.395 μs | 30.6865 μs | 1.6820 μs |  0.99 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 180.809 μs |  4.2874 μs | 0.2350 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 181.130 μs |  5.4043 μs | 0.2962 μs |  1.00 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 160.640 μs | 20.2314 μs | 1.1090 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 159.522 μs | 15.1939 μs | 0.8328 μs |  0.99 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 342.362 μs | 30.8156 μs | 1.6891 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 338.986 μs | 15.0037 μs | 0.8224 μs |  0.99 |    0.00 |     264 B |        0.41 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 178.916 μs | 21.2448 μs | 1.1645 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 179.052 μs | 14.6145 μs | 0.8011 μs |  1.00 |    0.01 |      32 B |        0.10 |

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
| Method                         | Categories           | Mean       | Error       | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|-----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 189.449 μs |  19.8033 μs |  1.0855 μs |  1.00 |    0.01 |      - |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               | 189.860 μs |  29.5434 μs |  1.6194 μs |  1.00 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get              | GET                  | 192.316 μs |  30.5835 μs |  1.6764 μs |  1.00 |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 192.395 μs |  32.1213 μs |  1.7607 μs |  1.00 |    0.01 |      - |      80 B |        0.16 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 175.868 μs |  34.8398 μs |  1.9097 μs |  1.00 |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 175.942 μs |  11.1854 μs |  0.6131 μs |  1.00 |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.196 μs |   0.4894 μs |  0.0268 μs |  1.00 |    0.01 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.916 μs |   1.6749 μs |  0.0918 μs |  0.95 |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 195.942 μs |  34.8035 μs |  1.9077 μs |  1.00 |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 191.186 μs |   9.3744 μs |  0.5138 μs |  0.98 |    0.01 |      - |      80 B |        0.15 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 192.032 μs |  37.2404 μs |  2.0413 μs |  1.00 |    0.01 |      - |     327 B |        1.00 |
| Respire_HSet                   | HSET                 | 191.866 μs |   7.2999 μs |  0.4001 μs |  1.00 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 192.151 μs |  14.1545 μs |  0.7759 μs |  1.00 |    0.00 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 190.867 μs |  16.4742 μs |  0.9030 μs |  0.99 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 378.544 μs |  70.2210 μs |  3.8491 μs |  1.00 |    0.01 |      - |     759 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 369.983 μs |  50.2872 μs |  2.7564 μs |  0.98 |    0.01 |      - |     576 B |        0.76 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 188.510 μs |   5.7312 μs |  0.3141 μs |  1.00 |    0.00 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 188.800 μs |  17.1004 μs |  0.9373 μs |  1.00 |    0.00 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 170.528 μs |  55.7663 μs |  3.0567 μs |  1.00 |    0.02 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 172.749 μs |  40.4559 μs |  2.2175 μs |  1.01 |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 189.907 μs |   3.8294 μs |  0.2099 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 191.909 μs |  29.4276 μs |  1.6130 μs |  1.01 |    0.01 |      - |      96 B |        0.31 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 202.755 μs |  22.1913 μs |  1.2164 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 206.049 μs |  16.6076 μs |  0.9103 μs |  1.02 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 192.769 μs |   4.9926 μs |  0.2737 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 192.671 μs |  11.6977 μs |  0.6412 μs |  1.00 |    0.00 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 193.245 μs |  27.2806 μs |  1.4953 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 195.376 μs |  33.3946 μs |  1.8305 μs |  1.01 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 177.562 μs |  25.1108 μs |  1.3764 μs |  1.00 |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 179.873 μs |  31.3610 μs |  1.7190 μs |  1.01 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 375.427 μs |  24.3490 μs |  1.3346 μs |  1.00 |    0.00 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 366.352 μs | 174.2291 μs |  9.5501 μs |  0.98 |    0.02 |      - |     264 B |        0.41 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 189.776 μs |  36.4128 μs |  1.9959 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 178.094 μs | 224.6342 μs | 12.3130 μs |  0.94 |    0.06 |      - |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
