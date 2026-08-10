---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 22:26 UTC from commit `83347ad6092d`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31436319002) for logs and downloadable artifacts.
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
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Gen0   | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|-------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 187.170 μs | 1.0441 μs | 1.5628 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 185.967 μs | 0.8879 μs | 1.3290 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get              | GET                  | 189.440 μs | 1.1435 μs | 1.6399 μs |  1.00 | Baseline        |    0.01 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 186.591 μs | 1.0564 μs | 1.5150 μs |  0.99 | Same            |    0.01 |      - |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 173.953 μs | 1.2312 μs | 1.8428 μs |  1.00 | Baseline        |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 170.317 μs | 0.8531 μs | 1.2769 μs |  0.98 | Same            |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   2.371 μs | 0.0502 μs | 0.0736 μs |  1.00 | Baseline        |    0.04 | 0.0098 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   2.036 μs | 0.0082 μs | 0.0118 μs |  0.86 | Faster          |    0.03 |      - |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.229 μs | 0.0451 μs | 0.0675 μs |  1.00 | Baseline        |    0.02 |      - |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.265 μs | 0.0292 μs | 0.0438 μs |  1.01 | Same            |    0.02 |      - |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HGet             | HGET                 | 192.462 μs | 0.6892 μs | 1.0316 μs |  1.00 | Baseline        |    0.01 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 188.778 μs | 1.2113 μs | 1.8130 μs |  0.98 | Same            |    0.01 |      - |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_HSet             | HSET                 | 191.044 μs | 0.7930 μs | 1.1869 μs |  1.00 | Baseline        |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 189.581 μs | 1.2716 μs | 1.8639 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Incr             | INCR                 | 188.763 μs | 0.5438 μs | 0.7971 μs |  1.00 | Baseline        |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 188.490 μs | 0.7647 μs | 1.1446 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 370.424 μs | 1.5255 μs | 2.2833 μs |  1.00 | Baseline        |    0.01 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 366.685 μs | 1.4081 μs | 2.0640 μs |  0.99 | Same            |    0.01 |      - |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping             | PING                 | 186.207 μs | 1.2923 μs | 1.9342 μs |  1.00 | Baseline        |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 186.068 μs | 0.8066 μs | 1.2073 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 171.564 μs | 1.0147 μs | 1.5188 μs |  1.00 | Baseline        |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 169.708 μs | 0.9555 μs | 1.4301 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 189.179 μs | 0.6256 μs | 0.9363 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 188.322 μs | 1.0382 μs | 1.5218 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 201.984 μs | 0.6624 μs | 0.9914 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 203.009 μs | 0.9302 μs | 1.3923 μs |  1.01 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 190.548 μs | 0.6849 μs | 1.0251 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 188.436 μs | 0.7598 μs | 1.0896 μs |  0.99 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 192.543 μs | 0.6963 μs | 1.0422 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 188.883 μs | 1.1032 μs | 1.6171 μs |  0.98 | Same            |    0.01 |      - |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 175.024 μs | 0.7695 μs | 1.1280 μs |  1.00 | Baseline        |    0.01 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 173.516 μs | 0.7374 μs | 1.0809 μs |  0.99 | Same            |    0.01 |      - |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 366.835 μs | 1.1146 μs | 1.6337 μs |  1.00 | Baseline        |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 362.634 μs | 0.9699 μs | 1.4216 μs |  0.99 | Same            |    0.01 |      - |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 187.737 μs | 0.5266 μs | 0.7719 μs |  1.00 | Baseline        |    0.01 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 188.086 μs | 0.7880 μs | 1.1550 μs |  1.00 | Same            |    0.01 |      - |         - |        0.00 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
INTEL XEON PLATINUM 8573C 2.30GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4
  Job-IDGKZI : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v4

IterationCount=15  LaunchCount=2  WarmupCount=10  

```
| Method                         | Categories           | Mean       | Error     | StdDev    | Ratio | MannWhitney(5%) | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|----------:|----------:|------:|---------------- |--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 107.716 μs | 0.7350 μs | 1.1001 μs |  1.00 | Baseline        |    0.01 |     296 B |        1.00 |
| Respire_Exists                 | EXISTS               | 103.695 μs | 0.8876 μs | 1.3285 μs |  0.96 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get              | GET                  | 109.821 μs | 1.2818 μs | 1.9186 μs |  1.00 | Baseline        |    0.02 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 103.175 μs | 0.6797 μs | 0.9964 μs |  0.94 | Same            |    0.02 |      48 B |        0.10 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  |  92.021 μs | 1.3622 μs | 2.0388 μs |  1.00 | Baseline        |    0.03 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  |  91.182 μs | 0.9814 μs | 1.4385 μs |  0.99 | Same            |    0.03 |      50 B |        0.15 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Pipelined    | GET x200 pipelined   |   1.828 μs | 0.1047 μs | 0.1567 μs |  1.01 | Baseline        |    0.12 |     289 B |        1.00 |
| Respire_Get_Pipelined          | GET x200 pipelined   |   1.564 μs | 0.0123 μs | 0.0180 μs |  0.86 | Faster          |    0.08 |      49 B |        0.17 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   3.404 μs | 0.0522 μs | 0.0748 μs |  1.00 | Baseline        |    0.03 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   3.068 μs | 0.0442 μs | 0.0662 μs |  0.90 | Faster          |    0.03 |      52 B |        0.18 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HGet             | HGET                 | 109.591 μs | 1.0748 μs | 1.6087 μs |  1.00 | Baseline        |    0.02 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 103.497 μs | 0.6786 μs | 1.0157 μs |  0.94 | Same            |    0.02 |      48 B |        0.09 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_HSet             | HSET                 | 107.779 μs | 1.0929 μs | 1.6020 μs |  1.00 | Baseline        |    0.02 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 103.081 μs | 1.4633 μs | 2.1903 μs |  0.96 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Incr             | INCR                 | 107.109 μs | 0.9373 μs | 1.4028 μs |  1.00 | Baseline        |    0.02 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 102.296 μs | 1.0533 μs | 1.5766 μs |  0.96 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 208.834 μs | 1.3601 μs | 1.9067 μs |  1.00 | Baseline        |    0.01 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 203.443 μs | 1.4665 μs | 2.1950 μs |  0.97 | Same            |    0.01 |     256 B |        0.34 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping             | PING                 | 105.310 μs | 1.1483 μs | 1.7187 μs |  1.00 | Baseline        |    0.02 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 101.110 μs | 0.8257 μs | 1.2359 μs |  0.96 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential |  89.530 μs | 0.7107 μs | 1.0637 μs |  1.00 | Baseline        |    0.02 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential |  88.576 μs | 0.8625 μs | 1.2910 μs |  0.99 | Same            |    0.02 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SAdd             | SADD                 | 106.186 μs | 0.9989 μs | 1.4951 μs |  1.00 | Baseline        |    0.02 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 101.935 μs | 0.8334 μs | 1.2474 μs |  0.96 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 115.296 μs | 1.3224 μs | 1.9793 μs |  1.00 | Baseline        |    0.02 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 108.065 μs | 0.7718 μs | 1.1552 μs |  0.94 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 108.710 μs | 0.9696 μs | 1.4512 μs |  1.00 | Baseline        |    0.02 |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 103.867 μs | 0.7167 μs | 1.0727 μs |  0.96 | Same            |    0.02 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 110.117 μs | 1.2846 μs | 1.9227 μs |  1.00 | Baseline        |    0.02 |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 104.159 μs | 1.8408 μs | 2.7553 μs |  0.95 | Same            |    0.03 |         - |        0.00 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  |  93.411 μs | 0.9347 μs | 1.3700 μs |  1.00 | Baseline        |    0.02 |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  |  93.326 μs | 0.7223 μs | 1.0588 μs |  1.00 | Same            |    0.02 |       2 B |       0.008 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 206.226 μs | 1.7183 μs | 2.5187 μs |  1.00 | Baseline        |    0.02 |     647 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 200.753 μs | 1.5663 μs | 2.3444 μs |  0.97 | Same            |    0.02 |     200 B |        0.31 |
|                                |                      |            |           |           |       |                 |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 109.185 μs | 0.8325 μs | 1.2203 μs |  1.00 | Baseline        |    0.02 |     310 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 102.799 μs | 0.7167 μs | 1.0727 μs |  0.94 | Same            |    0.01 |         - |        0.00 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
