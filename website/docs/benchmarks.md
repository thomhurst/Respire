---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-08 21:09 UTC from commit `23cf6cb5cc95`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31278450480) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                       | Categories         | Mean       | Error      | StdDev     | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------- |------------------- |-----------:|-----------:|-----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists         | EXISTS             | 124.288 μs |  26.166 μs |  1.4343 μs |  1.00 |    0.01 |      - |     388 B |        1.00 |
| Respire_Exists               | EXISTS             | 111.633 μs | 152.574 μs |  8.3631 μs |  0.90 |    0.06 |      - |     998 B |        2.57 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Get            | GET                | 119.062 μs |  64.829 μs |  3.5535 μs |  1.00 |    0.04 |      - |     498 B |        1.00 |
| Respire_Get                  | GET                | 112.861 μs |  46.892 μs |  2.5703 μs |  0.95 |    0.03 |      - |    1052 B |        2.11 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Get_Concurrent | GET x50 concurrent |   4.043 μs |   2.551 μs |  0.1398 μs |  1.00 |    0.04 | 0.0195 |     333 B |        1.00 |
| Respire_Get_Concurrent       | GET x50 concurrent |   4.690 μs |   1.139 μs |  0.0624 μs |  1.16 |    0.04 | 0.0586 |    1030 B |        3.09 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_HGet           | HGET               | 125.243 μs | 157.218 μs |  8.6176 μs |  1.00 |    0.08 |      - |     507 B |        1.00 |
| Respire_HGet                 | HGET               | 108.986 μs | 133.501 μs |  7.3176 μs |  0.87 |    0.07 |      - |    1205 B |        2.38 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_HSet           | HSET               | 125.740 μs | 103.095 μs |  5.6510 μs |  1.00 |    0.06 |      - |     427 B |        1.00 |
| Respire_HSet                 | HSET               | 112.657 μs |  95.716 μs |  5.2465 μs |  0.90 |    0.05 |      - |    1269 B |        2.97 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Incr           | INCR               | 123.673 μs |  24.784 μs |  1.3585 μs |  1.00 |    0.01 |      - |     378 B |        1.00 |
| Respire_Incr                 | INCR               | 106.358 μs | 154.833 μs |  8.4869 μs |  0.86 |    0.06 |      - |    1084 B |        2.87 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_LPushLPop      | LPUSH+LPOP         | 252.669 μs | 184.451 μs | 10.1104 μs |  1.00 |    0.05 |      - |     756 B |        1.00 |
| Respire_LPushLPop            | LPUSH+LPOP         | 257.324 μs | 110.604 μs |  6.0626 μs |  1.02 |    0.04 |      - |    1765 B |        2.33 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Ping           | PING               | 112.470 μs |  69.142 μs |  3.7899 μs |  1.00 |    0.04 |      - |     399 B |        1.00 |
| Respire_Ping                 | PING               | 110.197 μs |  55.003 μs |  3.0149 μs |  0.98 |    0.04 |      - |     867 B |        2.17 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_SAdd           | SADD               | 122.244 μs |  78.134 μs |  4.2828 μs |  1.00 |    0.04 |      - |     410 B |        1.00 |
| Respire_SAdd                 | SADD               | 107.869 μs | 268.144 μs | 14.6979 μs |  0.88 |    0.11 |      - |    1124 B |        2.74 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Set_10KB       | SET 10KB           | 148.372 μs |  17.452 μs |  0.9566 μs |  1.00 |    0.01 |      - |     413 B |        1.00 |
| Respire_Set_10KB             | SET 10KB           | 152.924 μs | 110.484 μs |  6.0560 μs |  1.03 |    0.04 |      - |    1154 B |        2.79 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Set_Small      | SET 13B            | 116.075 μs | 232.725 μs | 12.7564 μs |  1.01 |    0.14 |      - |     406 B |        1.00 |
| Respire_Set_Small            | SET 13B            | 123.166 μs |  85.128 μs |  4.6661 μs |  1.07 |    0.11 |      - |    1157 B |        2.85 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_Set_1KB        | SET 1KB            | 129.145 μs | 134.199 μs |  7.3559 μs |  1.00 |    0.07 |      - |     412 B |        1.00 |
| Respire_Set_1KB              | SET 1KB            | 125.081 μs | 123.976 μs |  6.7956 μs |  0.97 |    0.07 |      - |    1117 B |        2.71 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_SetDel         | SET+DEL            | 245.687 μs | 261.447 μs | 14.3308 μs |  1.00 |    0.07 |      - |     646 B |        1.00 |
| Respire_SetDel               | SET+DEL            | 250.707 μs | 170.860 μs |  9.3654 μs |  1.02 |    0.06 |      - |    1570 B |        2.43 |
|                              |                    |            |            |            |       |         |        |           |             |
| StackExchange_SIsMember      | SISMEMBER          | 119.740 μs | 134.025 μs |  7.3464 μs |  1.00 |    0.07 |      - |     404 B |        1.00 |
| Respire_SIsMember            | SISMEMBER          | 106.373 μs |  39.546 μs |  2.1677 μs |  0.89 |    0.05 |      - |    1108 B |        2.74 |

## net9.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 2.45GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 9.0.18 (9.0.18, 9.0.1826.31522), X64 RyuJIT x86-64-v3
  ShortRun : .NET 9.0.18 (9.0.18, 9.0.1826.31522), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                       | Categories         | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------- |------------------- |-----------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists         | EXISTS             | 188.241 μs |  22.301 μs | 1.2224 μs |  1.00 |    0.01 |      - |     400 B |        1.00 |
| Respire_Exists               | EXISTS             | 188.511 μs |  41.883 μs | 2.2957 μs |  1.00 |    0.01 |      - |    1012 B |        2.53 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_Get            | GET                | 188.627 μs |  12.957 μs | 0.7102 μs |  1.00 |    0.00 |      - |     504 B |        1.00 |
| Respire_Get                  | GET                | 188.786 μs |  23.351 μs | 1.2800 μs |  1.00 |    0.01 |      - |    1093 B |        2.17 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_Get_Concurrent | GET x50 concurrent |   4.840 μs |   2.205 μs | 0.1208 μs |  1.00 |    0.03 | 0.0195 |     333 B |        1.00 |
| Respire_Get_Concurrent       | GET x50 concurrent |   5.644 μs |   3.428 μs | 0.1879 μs |  1.17 |    0.04 | 0.0586 |    1031 B |        3.10 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_HGet           | HGET               | 188.956 μs |  15.119 μs | 0.8287 μs |  1.00 |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                 | HGET               | 188.748 μs |  19.478 μs | 1.0677 μs |  1.00 |    0.01 |      - |    1221 B |        2.35 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_HSet           | HSET               | 190.438 μs |  10.350 μs | 0.5673 μs |  1.00 |    0.00 |      - |     432 B |        1.00 |
| Respire_HSet                 | HSET               | 190.157 μs |  54.984 μs | 3.0139 μs |  1.00 |    0.01 |      - |    1275 B |        2.95 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_Incr           | INCR               | 189.364 μs |  18.134 μs | 0.9940 μs |  1.00 |    0.01 |      - |     400 B |        1.00 |
| Respire_Incr                 | INCR               | 189.698 μs |  66.809 μs | 3.6620 μs |  1.00 |    0.02 |      - |    1129 B |        2.82 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_LPushLPop      | LPUSH+LPOP         | 366.430 μs |  15.288 μs | 0.8380 μs |  1.00 |    0.00 |      - |     760 B |        1.00 |
| Respire_LPushLPop            | LPUSH+LPOP         | 368.714 μs | 104.123 μs | 5.7073 μs |  1.01 |    0.01 |      - |    1820 B |        2.39 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_Ping           | PING               | 186.408 μs |   6.925 μs | 0.3796 μs |  1.00 |    0.00 |      - |     408 B |        1.00 |
| Respire_Ping                 | PING               | 187.490 μs |  68.113 μs | 3.7335 μs |  1.01 |    0.02 |      - |     905 B |        2.22 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_SAdd           | SADD               | 187.522 μs |  19.834 μs | 1.0872 μs |  1.00 |    0.01 |      - |     416 B |        1.00 |
| Respire_SAdd                 | SADD               | 187.757 μs |  42.185 μs | 2.3123 μs |  1.00 |    0.01 |      - |    1132 B |        2.72 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_Set_10KB       | SET 10KB           | 199.458 μs |  29.794 μs | 1.6331 μs |  1.00 |    0.01 |      - |     416 B |        1.00 |
| Respire_Set_10KB             | SET 10KB           | 198.696 μs |  18.807 μs | 1.0309 μs |  1.00 |    0.01 |      - |    1177 B |        2.83 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_Set_Small      | SET 13B            | 189.452 μs |   8.298 μs | 0.4549 μs |  1.00 |    0.00 |      - |     416 B |        1.00 |
| Respire_Set_Small            | SET 13B            | 188.243 μs |  53.960 μs | 2.9578 μs |  0.99 |    0.01 |      - |    1175 B |        2.82 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_Set_1KB        | SET 1KB            | 189.438 μs |   2.455 μs | 0.1346 μs |  1.00 |    0.00 |      - |     416 B |        1.00 |
| Respire_Set_1KB              | SET 1KB            | 191.063 μs |  53.402 μs | 2.9271 μs |  1.01 |    0.01 |      - |    1185 B |        2.85 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_SetDel         | SET+DEL            | 361.509 μs |  35.097 μs | 1.9238 μs |  1.00 |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel               | SET+DEL            | 363.812 μs |  43.307 μs | 2.3738 μs |  1.01 |    0.01 |      - |    1597 B |        2.46 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_SIsMember      | SISMEMBER          | 187.504 μs |  13.689 μs | 0.7503 μs |  1.00 |    0.00 |      - |     416 B |        1.00 |
| Respire_SIsMember            | SISMEMBER          | 188.327 μs |  51.740 μs | 2.8361 μs |  1.00 |    0.01 |      - |    1162 B |        2.79 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
