---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-08 19:28 UTC from commit `dcec44e60f43`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31274321017) for logs and downloadable artifacts.
:::

StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.

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
| Method                       | Categories         | Mean       | Error      | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------- |------------------- |-----------:|-----------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists         | EXISTS             | 195.214 μs |  14.242 μs | 0.7806 μs |  1.00 |    0.00 |      - |     399 B |        1.00 |
| Respire_Exists               | EXISTS             | 194.291 μs |  28.608 μs | 1.5681 μs |  1.00 |    0.01 |      - |    1012 B |        2.54 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_Get            | GET                | 195.886 μs |  23.442 μs | 1.2849 μs |  1.00 |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                  | GET                | 194.641 μs |  33.798 μs | 1.8526 μs |  0.99 |    0.01 |      - |    1073 B |        2.13 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_Get_Concurrent | GET x50 concurrent |   5.205 μs |   5.451 μs | 0.2988 μs |  1.00 |    0.07 | 0.0195 |     333 B |        1.00 |
| Respire_Get_Concurrent       | GET x50 concurrent |   5.889 μs |   5.784 μs | 0.3171 μs |  1.13 |    0.08 | 0.0586 |    1030 B |        3.09 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_HGet           | HGET               | 196.038 μs |  33.620 μs | 1.8428 μs |  1.00 |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                 | HGET               | 192.714 μs |  48.778 μs | 2.6737 μs |  0.98 |    0.01 |      - |    1155 B |        2.22 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_HSet           | HSET               | 182.956 μs | 173.537 μs | 9.5121 μs |  1.00 |    0.06 |      - |     430 B |        1.00 |
| Respire_HSet                 | HSET               | 197.182 μs |  44.398 μs | 2.4336 μs |  1.08 |    0.05 |      - |    1242 B |        2.89 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_Incr           | INCR               | 194.214 μs |  58.137 μs | 3.1867 μs |  1.00 |    0.02 |      - |     400 B |        1.00 |
| Respire_Incr                 | INCR               | 194.004 μs |  84.570 μs | 4.6356 μs |  1.00 |    0.03 |      - |    1104 B |        2.76 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_LPushLPop      | LPUSH+LPOP         | 381.540 μs |  54.144 μs | 2.9678 μs |  1.00 |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop            | LPUSH+LPOP         | 380.139 μs |  86.639 μs | 4.7490 μs |  1.00 |    0.01 |      - |    1772 B |        2.33 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_Ping           | PING               | 188.105 μs | 119.704 μs | 6.5614 μs |  1.00 |    0.04 |      - |     407 B |        1.00 |
| Respire_Ping                 | PING               | 188.624 μs |  41.609 μs | 2.2807 μs |  1.00 |    0.03 |      - |     889 B |        2.18 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_SAdd           | SADD               | 194.056 μs |   8.816 μs | 0.4832 μs |  1.00 |    0.00 |      - |     416 B |        1.00 |
| Respire_SAdd                 | SADD               | 192.649 μs |  48.764 μs | 2.6729 μs |  0.99 |    0.01 |      - |    1102 B |        2.65 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_Set_10KB       | SET 10KB           | 209.464 μs |  24.047 μs | 1.3181 μs |  1.00 |    0.01 |      - |     416 B |        1.00 |
| Respire_Set_10KB             | SET 10KB           | 205.386 μs |  59.555 μs | 3.2644 μs |  0.98 |    0.01 |      - |    1153 B |        2.77 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_Set_Small      | SET 13B            | 195.198 μs |  25.916 μs | 1.4206 μs |  1.00 |    0.01 |      - |     416 B |        1.00 |
| Respire_Set_Small            | SET 13B            | 194.569 μs |  56.093 μs | 3.0747 μs |  1.00 |    0.02 |      - |    1157 B |        2.78 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_Set_1KB        | SET 1KB            | 197.832 μs |  21.163 μs | 1.1600 μs |  1.00 |    0.01 |      - |     416 B |        1.00 |
| Respire_Set_1KB              | SET 1KB            | 194.782 μs |  54.596 μs | 2.9926 μs |  0.98 |    0.01 |      - |    1156 B |        2.78 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_SetDel         | SET+DEL            | 379.187 μs |  40.835 μs | 2.2383 μs |  1.00 |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel               | SET+DEL            | 374.923 μs |  85.820 μs | 4.7041 μs |  0.99 |    0.01 |      - |    1542 B |        2.38 |
|                              |                    |            |            |           |       |         |        |           |             |
| StackExchange_SIsMember      | SISMEMBER          | 191.441 μs |  75.774 μs | 4.1534 μs |  1.00 |    0.03 |      - |     416 B |        1.00 |
| Respire_SIsMember            | SISMEMBER          | 194.715 μs |  69.723 μs | 3.8217 μs |  1.02 |    0.03 |      - |    1126 B |        2.71 |

## net9.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 9V74 2.60GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 9.0.18 (9.0.18, 9.0.1826.31522), X64 RyuJIT x86-64-v3
  ShortRun : .NET 9.0.18 (9.0.18, 9.0.1826.31522), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                       | Categories         | Mean       | Error       | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|----------------------------- |------------------- |-----------:|------------:|----------:|------:|--------:|-------:|----------:|------------:|
| StackExchange_Exists         | EXISTS             | 131.539 μs | 128.3967 μs | 7.0379 μs |  1.00 |    0.07 |      - |     399 B |        1.00 |
| Respire_Exists               | EXISTS             | 133.865 μs |  94.7690 μs | 5.1946 μs |  1.02 |    0.06 |      - |    1037 B |        2.60 |
|                              |                    |            |             |           |       |         |        |           |             |
| StackExchange_Get            | GET                | 135.273 μs |  40.7828 μs | 2.2354 μs |  1.00 |    0.02 |      - |     503 B |        1.00 |
| Respire_Get                  | GET                | 137.230 μs |  63.3435 μs | 3.4721 μs |  1.01 |    0.03 |      - |    1099 B |        2.18 |
|                              |                    |            |             |           |       |         |        |           |             |
| StackExchange_Get_Concurrent | GET x50 concurrent |   4.015 μs |   0.3007 μs | 0.0165 μs |  1.00 |    0.01 | 0.0195 |     333 B |        1.00 |
| Respire_Get_Concurrent       | GET x50 concurrent |   4.548 μs |   1.8935 μs | 0.1038 μs |  1.13 |    0.02 | 0.0586 |    1030 B |        3.09 |
|                              |                    |            |             |           |       |         |        |           |             |
| StackExchange_HGet           | HGET               | 141.263 μs |   7.0114 μs | 0.3843 μs |  1.00 |    0.00 |      - |     520 B |        1.00 |
| Respire_HGet                 | HGET               | 140.069 μs |  13.4148 μs | 0.7353 μs |  0.99 |    0.01 |      - |    1212 B |        2.33 |
|                              |                    |            |             |           |       |         |        |           |             |
| StackExchange_HSet           | HSET               | 139.887 μs |  32.2931 μs | 1.7701 μs |  1.00 |    0.02 |      - |     432 B |        1.00 |
| Respire_HSet                 | HSET               | 136.271 μs |  84.3327 μs | 4.6226 μs |  0.97 |    0.03 |      - |    1267 B |        2.93 |
|                              |                    |            |             |           |       |         |        |           |             |
| StackExchange_Incr           | INCR               | 133.465 μs |  75.5187 μs | 4.1394 μs |  1.00 |    0.04 |      - |     400 B |        1.00 |
| Respire_Incr                 | INCR               | 135.571 μs |  53.0012 μs | 2.9052 μs |  1.02 |    0.03 |      - |    1115 B |        2.79 |
|                              |                    |            |             |           |       |         |        |           |             |
| StackExchange_LPushLPop      | LPUSH+LPOP         | 279.561 μs |  58.1913 μs | 3.1897 μs |  1.00 |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop            | LPUSH+LPOP         | 287.027 μs |  54.0441 μs | 2.9623 μs |  1.03 |    0.01 |      - |    1830 B |        2.41 |
|                              |                    |            |             |           |       |         |        |           |             |
| StackExchange_Ping           | PING               | 137.288 μs |   9.4131 μs | 0.5160 μs |  1.00 |    0.00 |      - |     407 B |        1.00 |
| Respire_Ping                 | PING               | 129.468 μs |  44.9258 μs | 2.4625 μs |  0.94 |    0.02 |      - |     903 B |        2.22 |
|                              |                    |            |             |           |       |         |        |           |             |
| StackExchange_SAdd           | SADD               | 137.939 μs |  22.2780 μs | 1.2211 μs |  1.00 |    0.01 |      - |     415 B |        1.00 |
| Respire_SAdd                 | SADD               | 139.013 μs |  47.8729 μs | 2.6241 μs |  1.01 |    0.02 |      - |    1135 B |        2.73 |
|                              |                    |            |             |           |       |         |        |           |             |
| StackExchange_Set_10KB       | SET 10KB           | 157.333 μs |  35.6698 μs | 1.9552 μs |  1.00 |    0.02 |      - |     416 B |        1.00 |
| Respire_Set_10KB             | SET 10KB           | 157.109 μs |  31.8986 μs | 1.7485 μs |  1.00 |    0.01 |      - |    1157 B |        2.78 |
|                              |                    |            |             |           |       |         |        |           |             |
| StackExchange_Set_Small      | SET 13B            | 138.290 μs |  33.3554 μs | 1.8283 μs |  1.00 |    0.02 |      - |     416 B |        1.00 |
| Respire_Set_Small            | SET 13B            | 137.065 μs |  39.2984 μs | 2.1541 μs |  0.99 |    0.02 |      - |    1169 B |        2.81 |
|                              |                    |            |             |           |       |         |        |           |             |
| StackExchange_Set_1KB        | SET 1KB            | 139.734 μs |  16.0578 μs | 0.8802 μs |  1.00 |    0.01 |      - |     413 B |        1.00 |
| Respire_Set_1KB              | SET 1KB            | 140.827 μs |  50.8954 μs | 2.7897 μs |  1.01 |    0.02 |      - |    1172 B |        2.84 |
|                              |                    |            |             |           |       |         |        |           |             |
| StackExchange_SetDel         | SET+DEL            | 272.627 μs |  38.2401 μs | 2.0961 μs |  1.00 |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel               | SET+DEL            | 270.684 μs | 172.5317 μs | 9.4570 μs |  0.99 |    0.03 |      - |    1630 B |        2.52 |
|                              |                    |            |             |           |       |         |        |           |             |
| StackExchange_SIsMember      | SISMEMBER          | 136.202 μs |  32.0788 μs | 1.7583 μs |  1.00 |    0.02 |      - |     412 B |        1.00 |
| Respire_SIsMember            | SISMEMBER          | 136.034 μs |  23.5083 μs | 1.2886 μs |  1.00 |    0.01 |      - |    1145 B |        2.78 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
