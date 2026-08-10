---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 11:13 UTC from commit `40370c1f635d`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31380671034) for logs and downloadable artifacts.
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
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 189.606 μs | 0.9039 μs | 1.3249 μs |  1.00 | Baseline        |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 190.889 μs | 0.7889 μs | 1.1564 μs |  1.01 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get              | GET                  | 192.419 μs | 0.7520 μs | 1.1023 μs |  1.00 | Baseline        |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 191.247 μs | 0.6898 μs | 1.0324 μs |  0.99 | Same            |    0.01 |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 175.060 μs | 0.8560 μs | 1.2811 μs |  1.00 | Baseline        |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 172.587 μs | 1.3222 μs | 1.9789 μs |  0.99 | Same            |    0.01 |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.290 μs | 0.0432 μs | 0.0633 μs |  1.00 | Baseline        |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.261 μs | 0.0273 μs | 0.0401 μs |  0.99 | Same            |    0.01 |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HGet             | HGET                 | 192.415 μs | 0.6701 μs | 0.9610 μs |  1.00 | Baseline        |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 190.590 μs | 0.7017 μs | 1.0503 μs |  0.99 | Same            |    0.01 |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HSet             | HSET                 | 191.901 μs | 0.6721 μs | 1.0060 μs |  1.00 | Baseline        |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 191.493 μs | 0.8010 μs | 1.1990 μs |  1.00 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Incr             | INCR                 | 189.870 μs | 0.7433 μs | 1.0896 μs |  1.00 | Baseline        |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 190.858 μs | 0.5940 μs | 0.8890 μs |  1.01 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 371.897 μs | 1.0585 μs | 1.5516 μs |  1.00 | Baseline        |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 369.295 μs | 1.1172 μs | 1.6375 μs |  0.99 | Same            |    0.01 |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping             | PING                 | 188.205 μs | 0.9051 μs | 1.3266 μs |  1.00 | Baseline        |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 190.194 μs | 0.7702 μs | 1.1046 μs |  1.01 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 173.671 μs | 1.1470 μs | 1.7167 μs |  1.00 | Baseline        |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 170.768 μs | 0.8982 μs | 1.3443 μs |  0.98 | Same            |    0.01 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SAdd             | SADD                 | 190.222 μs | 0.6408 μs | 0.9190 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 191.260 μs | 0.7334 μs | 1.0749 μs |  1.01 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 202.495 μs | 0.7277 μs | 1.0667 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 204.824 μs | 0.5520 μs | 0.7739 μs |  1.01 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 192.005 μs | 0.5498 μs | 0.8230 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 190.503 μs | 0.6267 μs | 0.9380 μs |  0.99 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 192.821 μs | 0.5518 μs | 0.7914 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 191.087 μs | 0.6895 μs | 1.0321 μs |  0.99 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 177.388 μs | 0.7340 μs | 1.0289 μs |  1.00 | Baseline        |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 174.416 μs | 1.7518 μs | 2.6221 μs |  0.98 | Same            |    0.02 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 368.966 μs | 0.8628 μs | 1.2374 μs |  1.00 | Baseline        |    0.00 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 367.195 μs | 1.1998 μs | 1.7587 μs |  1.00 | Same            |    0.01 |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 189.753 μs | 0.4791 μs | 0.7170 μs |  1.00 | Baseline        |    0.01 |     311 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 190.929 μs | 0.8413 μs | 1.2593 μs |  1.01 | Same            |    0.01 |         - |        0.00 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3
  Job-IDGKZI : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 194.437 μs | 0.9253 μs | 1.3563 μs |  1.00 | Baseline        |    0.01 |     293 B |        1.00 |
| Respire_Exists                 | EXISTS               | 195.927 μs | 0.7933 μs | 1.1874 μs |  1.01 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get              | GET                  | 199.704 μs | 0.7322 μs | 1.0959 μs |  1.00 | Baseline        |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 197.051 μs | 0.7500 μs | 1.0993 μs |  0.99 | Same            |    0.01 |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 180.240 μs | 1.3823 μs | 2.0689 μs |  1.00 | Baseline        |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 180.489 μs | 1.5462 μs | 2.3143 μs |  1.00 | Same            |    0.02 |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.599 μs | 0.0470 μs | 0.0703 μs |  1.00 | Baseline        |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.585 μs | 0.0472 μs | 0.0646 μs |  1.00 | Same            |    0.02 |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HGet             | HGET                 | 200.526 μs | 1.2788 μs | 1.8340 μs |  1.00 | Baseline        |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 198.891 μs | 0.8919 μs | 1.3350 μs |  0.99 | Same            |    0.01 |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HSet             | HSET                 | 197.454 μs | 0.8265 μs | 1.2114 μs |  1.00 | Baseline        |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 199.232 μs | 0.7278 μs | 1.0667 μs |  1.01 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Incr             | INCR                 | 194.200 μs | 0.8393 μs | 1.2037 μs |  1.00 | Baseline        |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 197.869 μs | 0.8600 μs | 1.2872 μs |  1.02 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 389.778 μs | 1.8342 μs | 2.7453 μs |  1.00 | Baseline        |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 386.319 μs | 1.8230 μs | 2.6721 μs |  0.99 | Same            |    0.01 |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping             | PING                 | 191.783 μs | 0.9992 μs | 1.4956 μs |  1.00 | Baseline        |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 193.830 μs | 0.7804 μs | 1.1681 μs |  1.01 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 181.010 μs | 1.1313 μs | 1.6932 μs |  1.00 | Baseline        |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 177.466 μs | 1.1827 μs | 1.7702 μs |  0.98 | Same            |    0.01 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SAdd             | SADD                 | 194.384 μs | 1.1112 μs | 1.6631 μs |  1.00 | Baseline        |    0.01 |     311 B |        1.00 |
| Respire_SAdd                   | SADD                 | 195.425 μs | 1.1968 μs | 1.7913 μs |  1.01 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 206.305 μs | 0.6606 μs | 0.9887 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 213.171 μs | 0.7712 μs | 1.1542 μs |  1.03 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 195.917 μs | 0.8656 μs | 1.2956 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 198.068 μs | 0.8569 μs | 1.2825 μs |  1.01 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 196.610 μs | 0.7597 μs | 1.1371 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 198.573 μs | 1.0167 μs | 1.5217 μs |  1.01 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 183.125 μs | 0.9218 μs | 1.3797 μs |  1.00 | Baseline        |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 183.286 μs | 1.2069 μs | 1.8065 μs |  1.00 | Same            |    0.01 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 382.264 μs | 1.6747 μs | 2.5066 μs |  1.00 | Baseline        |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 380.689 μs | 1.0315 μs | 1.5120 μs |  1.00 | Same            |    0.01 |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 193.616 μs | 1.3121 μs | 1.9639 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 196.400 μs | 0.6957 μs | 1.0413 μs |  1.01 | Same            |    0.01 |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
