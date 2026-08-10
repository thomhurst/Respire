---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 10:14 UTC from commit `bbff474237a7`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31376468419) for logs and downloadable artifacts.
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
| StackExchange_Exists           | EXISTS               | 184.981 μs | 0.5225 μs | 0.7659 μs |  1.00 | Baseline        |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 184.498 μs | 1.3001 μs | 1.9459 μs |  1.00 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get              | GET                  | 184.887 μs | 1.4661 μs | 2.0552 μs |  1.00 | Baseline        |    0.02 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 182.194 μs | 0.8136 μs | 1.1926 μs |  0.99 | Same            |    0.01 |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 170.437 μs | 1.0281 μs | 1.5388 μs |  1.00 | Baseline        |    0.01 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 167.975 μs | 1.3609 μs | 2.0369 μs |  0.99 | Same            |    0.01 |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.135 μs | 0.0474 μs | 0.0679 μs |  1.00 | Baseline        |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.136 μs | 0.0255 μs | 0.0358 μs |  1.00 | Same            |    0.01 |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HGet             | HGET                 | 186.262 μs | 0.5206 μs | 0.7631 μs |  1.00 | Baseline        |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 183.545 μs | 0.8378 μs | 1.2280 μs |  0.99 | Same            |    0.01 |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HSet             | HSET                 | 186.295 μs | 0.7571 μs | 1.1332 μs |  1.00 | Baseline        |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 185.193 μs | 0.7132 μs | 1.0675 μs |  0.99 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Incr             | INCR                 | 183.368 μs | 0.5556 μs | 0.8317 μs |  1.00 | Baseline        |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 183.312 μs | 0.7261 μs | 1.0867 μs |  1.00 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 365.849 μs | 1.8540 μs | 2.7175 μs |  1.00 | Baseline        |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 360.360 μs | 1.2577 μs | 1.8435 μs |  0.99 | Same            |    0.01 |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping             | PING                 | 183.338 μs | 1.0121 μs | 1.5149 μs |  1.00 | Baseline        |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 183.159 μs | 0.9888 μs | 1.4494 μs |  1.00 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 168.856 μs | 2.0615 μs | 3.0855 μs |  1.00 | Baseline        |    0.03 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 166.001 μs | 1.5465 μs | 2.3147 μs |  0.98 | Same            |    0.02 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SAdd             | SADD                 | 184.150 μs | 1.2158 μs | 1.7821 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 184.835 μs | 0.6646 μs | 0.9948 μs |  1.00 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 197.047 μs | 0.5540 μs | 0.8121 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 197.460 μs | 0.8723 μs | 1.2785 μs |  1.00 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 187.167 μs | 0.9833 μs | 1.4717 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 185.493 μs | 1.6388 μs | 2.4529 μs |  0.99 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 188.126 μs | 0.8182 μs | 1.1993 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 185.532 μs | 0.9063 μs | 1.2997 μs |  0.99 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 172.444 μs | 1.0036 μs | 1.5021 μs |  1.00 | Baseline        |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 168.937 μs | 1.6653 μs | 2.4926 μs |  0.98 | Same            |    0.02 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 360.085 μs | 1.3351 μs | 1.9983 μs |  1.00 | Baseline        |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 357.730 μs | 1.2180 μs | 1.8230 μs |  0.99 | Same            |    0.01 |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 184.812 μs | 0.8035 μs | 1.2026 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 184.586 μs | 0.9346 μs | 1.3989 μs |  1.00 | Same            |    0.01 |         - |        0.00 |

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
| StackExchange_Exists           | EXISTS               | 178.025 μs | 0.7341 μs | 1.0760 μs |  1.00 | Baseline        |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 173.882 μs | 0.5016 μs | 0.7194 μs |  0.98 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get              | GET                  | 182.567 μs | 0.8088 μs | 1.2106 μs |  1.00 | Baseline        |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 174.372 μs | 0.9252 μs | 1.3561 μs |  0.96 | Same            |    0.01 |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 164.873 μs | 1.2959 μs | 1.8996 μs |  1.00 | Baseline        |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 162.113 μs | 1.6558 μs | 2.4270 μs |  0.98 | Same            |    0.02 |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.100 μs | 0.0482 μs | 0.0706 μs |  1.00 | Baseline        |    0.02 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.111 μs | 0.0291 μs | 0.0427 μs |  1.00 | Same            |    0.02 |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HGet             | HGET                 | 183.519 μs | 0.9161 μs | 1.3711 μs |  1.00 | Baseline        |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 175.522 μs | 0.7039 μs | 1.0318 μs |  0.96 | Same            |    0.01 |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HSet             | HSET                 | 181.258 μs | 0.9030 μs | 1.3516 μs |  1.00 | Baseline        |    0.01 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 176.011 μs | 0.8668 μs | 1.2974 μs |  0.97 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Incr             | INCR                 | 179.122 μs | 1.0713 μs | 1.5703 μs |  1.00 | Baseline        |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 174.715 μs | 0.6690 μs | 0.9806 μs |  0.98 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 356.331 μs | 1.3048 μs | 1.9125 μs |  1.00 | Baseline        |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 349.656 μs | 0.9670 μs | 1.3869 μs |  0.98 | Same            |    0.01 |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping             | PING                 | 176.688 μs | 0.9460 μs | 1.4159 μs |  1.00 | Baseline        |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 172.448 μs | 0.5418 μs | 0.8109 μs |  0.98 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 163.366 μs | 1.8967 μs | 2.8388 μs |  1.00 | Baseline        |    0.02 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 160.621 μs | 1.2327 μs | 1.8450 μs |  0.98 | Same            |    0.02 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SAdd             | SADD                 | 179.719 μs | 0.8911 μs | 1.3338 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 174.702 μs | 0.5781 μs | 0.8652 μs |  0.97 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 191.586 μs | 0.7471 μs | 1.1182 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 186.794 μs | 0.7817 μs | 1.1700 μs |  0.98 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 181.593 μs | 0.9260 μs | 1.3859 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 176.770 μs | 0.6425 μs | 0.9617 μs |  0.97 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 182.950 μs | 1.0708 μs | 1.6028 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 177.076 μs | 0.4837 μs | 0.7240 μs |  0.97 | Same            |    0.01 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 167.073 μs | 0.8322 μs | 1.2198 μs |  1.00 | Baseline        |    0.01 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 164.617 μs | 1.3780 μs | 2.0626 μs |  0.99 | Same            |    0.01 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 352.638 μs | 1.2465 μs | 1.7877 μs |  1.00 | Baseline        |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 346.822 μs | 1.1635 μs | 1.7055 μs |  0.98 | Same            |    0.01 |     202 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 179.813 μs | 0.5348 μs | 0.7839 μs |  1.00 | Baseline        |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 174.908 μs | 0.7479 μs | 1.1194 μs |  0.97 | Same            |    0.01 |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
