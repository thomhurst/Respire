---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-09 09:28 UTC from commit `8d548eff9a44`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31305790440) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net10.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3
  ShortRun : .NET 10.0.10 (10.0.10, 10.0.1026.32716), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error       | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|------------:|-----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 131.544 μs |  36.4998 μs |  2.0007 μs |  1.00 |    0.02 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 122.822 μs |  50.0453 μs |  2.7432 μs |  0.93 |    0.02 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get              | GET                  | 132.471 μs |  98.2852 μs |  5.3873 μs |  1.00 |    0.05 |      - |     502 B |        1.00 |
| Respire_Get                    | GET                  | 131.943 μs |  22.5595 μs |  1.2366 μs |  1.00 |    0.04 |      - |      80 B |        0.16 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 113.322 μs |  17.1715 μs |  0.9412 μs |  1.00 |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 111.311 μs |   3.1006 μs |  0.1700 μs |  0.98 |    0.01 |      - |      51 B |        0.15 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   4.535 μs |   0.5404 μs |  0.0296 μs |  1.00 |    0.01 | 0.0146 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   4.272 μs |   0.7411 μs |  0.0406 μs |  0.94 |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 137.702 μs | 114.1822 μs |  6.2587 μs |  1.00 |    0.06 |      - |     517 B |        1.00 |
| Respire_HGet                   | HGET                 | 123.122 μs |  48.1430 μs |  2.6389 μs |  0.90 |    0.04 |      - |      80 B |        0.15 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 128.468 μs |  61.0897 μs |  3.3485 μs |  1.00 |    0.03 |      - |     319 B |        1.00 |
| Respire_HSet                   | HSET                 | 131.295 μs |  65.9633 μs |  3.6157 μs |  1.02 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 130.712 μs |  13.4935 μs |  0.7396 μs |  1.00 |    0.01 |      - |     294 B |        1.00 |
| Respire_Incr                   | INCR                 | 133.758 μs |  11.3183 μs |  0.6204 μs |  1.02 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 279.060 μs |   3.8357 μs |  0.2103 μs |  1.00 |    0.00 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 271.366 μs |  31.2583 μs |  1.7134 μs |  0.97 |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 129.397 μs |  46.2417 μs |  2.5347 μs |  1.00 |    0.02 |      - |     303 B |        1.00 |
| Respire_Ping                   | PING                 | 127.776 μs |  91.8652 μs |  5.0354 μs |  0.99 |    0.04 |      - |      32 B |        0.11 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 112.023 μs |   9.8767 μs |  0.5414 μs |  1.00 |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 107.917 μs |  39.0743 μs |  2.1418 μs |  0.96 |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 130.532 μs |  37.1348 μs |  2.0355 μs |  1.00 |    0.02 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 130.895 μs |  68.6857 μs |  3.7649 μs |  1.00 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 150.825 μs |  66.2727 μs |  3.6326 μs |  1.00 |    0.03 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 153.358 μs |   7.6079 μs |  0.4170 μs |  1.02 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 131.966 μs |  37.2842 μs |  2.0437 μs |  1.00 |    0.02 |      - |     310 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 132.251 μs |  71.0656 μs |  3.8953 μs |  1.00 |    0.03 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 135.818 μs |  31.5703 μs |  1.7305 μs |  1.00 |    0.02 |      - |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 135.688 μs |  50.7169 μs |  2.7800 μs |  1.00 |    0.02 |      - |      32 B |        0.10 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 114.816 μs |   1.8297 μs |  0.1003 μs |  1.00 |    0.00 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 114.668 μs |  12.8211 μs |  0.7028 μs |  1.00 |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 266.179 μs |  55.5288 μs |  3.0437 μs |  1.00 |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 269.180 μs | 193.8741 μs | 10.6269 μs |  1.01 |    0.04 |      - |     199 B |        0.31 |
|                                |                      |            |             |            |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 129.150 μs |  52.5218 μs |  2.8789 μs |  1.00 |    0.03 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 126.600 μs | 118.3398 μs |  6.4866 μs |  0.98 |    0.05 |      - |      32 B |        0.10 |

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
| Method                         | Categories           | Mean       | Error      | StdDev     | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|-----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 193.426 μs |  53.361 μs |  2.9249 μs |  1.00 |    0.02 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 193.197 μs |  41.086 μs |  2.2520 μs |  1.00 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Get              | GET                  | 195.730 μs |  55.735 μs |  3.0550 μs |  1.00 |    0.02 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 194.282 μs |   6.396 μs |  0.3506 μs |  0.99 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 179.482 μs |  40.093 μs |  2.1976 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 179.771 μs |  39.412 μs |  2.1603 μs |  1.00 |    0.01 |      50 B |        0.15 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.639 μs |   2.205 μs |  0.1209 μs |  1.00 |    0.03 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.129 μs |   1.239 μs |  0.0679 μs |  0.91 |    0.02 |      52 B |        0.18 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_HGet             | HGET                 | 199.751 μs |  21.291 μs |  1.1670 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 196.161 μs |  20.194 μs |  1.1069 μs |  0.98 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_HSet             | HSET                 | 194.643 μs |  42.044 μs |  2.3046 μs |  1.00 |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 195.617 μs |  16.889 μs |  0.9257 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Incr             | INCR                 | 194.211 μs |  16.614 μs |  0.9107 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 192.873 μs |  39.608 μs |  2.1711 μs |  0.99 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 378.311 μs | 230.478 μs | 12.6333 μs |  1.00 |    0.04 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 361.123 μs | 120.084 μs |  6.5822 μs |  0.96 |    0.03 |     255 B |        0.34 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Ping             | PING                 | 190.419 μs |  53.905 μs |  2.9547 μs |  1.00 |    0.02 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 190.425 μs |  32.776 μs |  1.7966 μs |  1.00 |    0.02 |      32 B |        0.11 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 178.170 μs |  21.753 μs |  1.1923 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 177.075 μs |  11.619 μs |  0.6369 μs |  0.99 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 192.576 μs |  41.114 μs |  2.2536 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 194.203 μs |  11.300 μs |  0.6194 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 205.869 μs |  24.151 μs |  1.3238 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 205.785 μs |  12.845 μs |  0.7041 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 194.311 μs |  41.410 μs |  2.2698 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 195.749 μs |  11.468 μs |  0.6286 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 196.582 μs |  30.197 μs |  1.6552 μs |  1.00 |    0.01 |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 197.040 μs |  18.509 μs |  1.0145 μs |  1.00 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 180.324 μs |  17.852 μs |  0.9785 μs |  1.00 |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 181.608 μs |  63.339 μs |  3.4718 μs |  1.01 |    0.02 |       2 B |       0.008 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 379.087 μs |  10.422 μs |  0.5713 μs |  1.00 |    0.00 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 377.719 μs |  87.986 μs |  4.8228 μs |  1.00 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |            |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 194.547 μs |   3.386 μs |  0.1856 μs |  1.00 |    0.00 |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 192.661 μs |  38.873 μs |  2.1308 μs |  0.99 |    0.01 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
