---
title: Benchmarks
description: Latest automated Respire and StackExchange.Redis benchmark results.
---

# Benchmarks

:::info Automated results
Generated 2026-08-10 00:11 UTC from commit `f29017bc2467`. See the [GitHub Actions run](https://github.com/thomhurst/Respire/actions/runs/31343506991) for logs and downloadable artifacts.
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
| StackExchange_Exists           | EXISTS               | 192.947 μs | 15.0605 μs | 0.8255 μs |  1.00 |    0.01 |      - |     295 B |        1.00 |
| Respire_Exists                 | EXISTS               | 195.723 μs | 29.1230 μs | 1.5963 μs |  1.01 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get              | GET                  | 195.813 μs | 12.4229 μs | 0.6809 μs |  1.00 |    0.00 |      - |     504 B |        1.00 |
| Respire_Get                    | GET                  | 193.425 μs | 20.2285 μs | 1.1088 μs |  0.99 |    0.01 |      - |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 176.643 μs | 24.5077 μs | 1.3433 μs |  1.00 |    0.01 |      - |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 175.373 μs |  5.7823 μs | 0.3169 μs |  0.99 |    0.01 |      - |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.466 μs |  0.8910 μs | 0.0488 μs |  1.00 |    0.01 | 0.0098 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.331 μs |  1.0502 μs | 0.0576 μs |  0.98 |    0.01 |      - |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HGet             | HGET                 | 194.239 μs | 96.1269 μs | 5.2690 μs |  1.00 |    0.03 |      - |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 192.443 μs | 76.1021 μs | 4.1714 μs |  0.99 |    0.03 |      - |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_HSet             | HSET                 | 193.836 μs | 37.1924 μs | 2.0386 μs |  1.00 |    0.01 |      - |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 195.106 μs | 25.5107 μs | 1.3983 μs |  1.01 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Incr             | INCR                 | 194.376 μs | 22.0834 μs | 1.2105 μs |  1.00 |    0.01 |      - |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 195.170 μs | 11.6994 μs | 0.6413 μs |  1.00 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 381.025 μs | 12.9570 μs | 0.7102 μs |  1.00 |    0.00 |      - |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 375.686 μs | 16.9862 μs | 0.9311 μs |  0.99 |    0.00 |      - |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping             | PING                 | 190.890 μs | 38.3151 μs | 2.1002 μs |  1.00 |    0.01 |      - |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 194.580 μs | 17.9429 μs | 0.9835 μs |  1.02 |    0.01 |      - |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 175.444 μs | 21.5200 μs | 1.1796 μs |  1.00 |    0.01 |      - |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 172.349 μs | 52.9862 μs | 2.9044 μs |  0.98 |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SAdd             | SADD                 | 193.477 μs | 41.9915 μs | 2.3017 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 194.668 μs | 19.1464 μs | 1.0495 μs |  1.01 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 204.648 μs | 52.0614 μs | 2.8537 μs |  1.00 |    0.02 |      - |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 209.775 μs |  8.7513 μs | 0.4797 μs |  1.03 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_Small        | SET 13B              | 196.256 μs | 15.7846 μs | 0.8652 μs |  1.00 |    0.01 |      - |     312 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 195.150 μs | 10.1020 μs | 0.5537 μs |  0.99 |    0.00 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 196.101 μs | 22.9313 μs | 1.2569 μs |  1.00 |    0.01 |      - |     311 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 196.676 μs | 20.0655 μs | 1.0999 μs |  1.00 |    0.01 |      - |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 178.692 μs | 13.0689 μs | 0.7164 μs |  1.00 |    0.00 |      - |     250 B |       1.000 |
| Respire_Set_SteadyState        | SET x100 sequential  | 176.846 μs | 73.8439 μs | 4.0476 μs |  0.99 |    0.02 |      - |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SetDel           | SET+DEL              | 376.108 μs | 28.4653 μs | 1.5603 μs |  1.00 |    0.01 |      - |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 372.142 μs | 21.4041 μs | 1.1732 μs |  0.99 |    0.00 |      - |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |        |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 193.152 μs |  7.4799 μs | 0.4100 μs |  1.00 |    0.00 |      - |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 194.621 μs |  8.9048 μs | 0.4881 μs |  1.01 |    0.00 |      - |      32 B |        0.10 |

## net8.0

```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.4 LTS (Noble Numbat)
AMD EPYC 7763 3.18GHz, 1 CPU, 4 logical and 2 physical cores
.NET SDK 10.0.302
  [Host]   : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3
  ShortRun : .NET 8.0.29 (8.0.29, 8.0.2926.32403), X64 RyuJIT x86-64-v3

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                         | Categories           | Mean       | Error      | StdDev    | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------- |--------------------- |-----------:|-----------:|----------:|------:|--------:|----------:|------------:|
| StackExchange_Exists           | EXISTS               | 192.004 μs | 16.0727 μs | 0.8810 μs |  1.00 |    0.01 |     293 B |        1.00 |
| Respire_Exists                 | EXISTS               | 195.805 μs | 17.4723 μs | 0.9577 μs |  1.02 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get              | GET                  | 198.335 μs | 35.4707 μs | 1.9443 μs |  1.00 |    0.01 |     504 B |        1.00 |
| Respire_Get                    | GET                  | 197.871 μs | 19.8684 μs | 1.0891 μs |  1.00 |    0.01 |      80 B |        0.16 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_SteadyState  | GET x100 sequential  | 179.924 μs | 40.2036 μs | 2.2037 μs |  1.00 |    0.02 |     338 B |        1.00 |
| Respire_Get_SteadyState        | GET x100 sequential  | 181.217 μs | 44.2178 μs | 2.4237 μs |  1.01 |    0.02 |      50 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Get_Concurrent   | GET x50 concurrent   |   5.673 μs |  0.2241 μs | 0.0123 μs |  1.00 |    0.00 |     291 B |        1.00 |
| Respire_Get_Concurrent         | GET x50 concurrent   |   5.482 μs |  0.7968 μs | 0.0437 μs |  0.97 |    0.01 |      52 B |        0.18 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HGet             | HGET                 | 200.675 μs | 25.2828 μs | 1.3858 μs |  1.00 |    0.01 |     520 B |        1.00 |
| Respire_HGet                   | HGET                 | 197.918 μs | 16.1353 μs | 0.8844 μs |  0.99 |    0.01 |      80 B |        0.15 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_HSet             | HSET                 | 196.792 μs | 12.5154 μs | 0.6860 μs |  1.00 |    0.00 |     328 B |        1.00 |
| Respire_HSet                   | HSET                 | 200.402 μs |  6.4100 μs | 0.3514 μs |  1.02 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Incr             | INCR                 | 195.006 μs | 22.3652 μs | 1.2259 μs |  1.00 |    0.01 |     296 B |        1.00 |
| Respire_Incr                   | INCR                 | 196.576 μs | 15.9484 μs | 0.8742 μs |  1.01 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_LPushLPop        | LPUSH+LPOP           | 388.184 μs | 25.2985 μs | 1.3867 μs |  1.00 |    0.00 |     760 B |        1.00 |
| Respire_LPushLPop              | LPUSH+LPOP           | 384.088 μs |  9.2925 μs | 0.5094 μs |  0.99 |    0.00 |     256 B |        0.34 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping             | PING                 | 193.896 μs | 37.4321 μs | 2.0518 μs |  1.00 |    0.01 |     304 B |        1.00 |
| Respire_Ping                   | PING                 | 194.584 μs | 19.0970 μs | 1.0468 μs |  1.00 |    0.01 |      32 B |        0.11 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Ping_SteadyState | PING x100 sequential | 178.695 μs | 17.2448 μs | 0.9452 μs |  1.00 |    0.01 |     242 B |       1.000 |
| Respire_Ping_SteadyState       | PING x100 sequential | 177.436 μs | 13.6791 μs | 0.7498 μs |  0.99 |    0.01 |       2 B |       0.008 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SAdd             | SADD                 | 194.269 μs | 20.7561 μs | 1.1377 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SAdd                   | SADD                 | 195.342 μs | 48.1537 μs | 2.6395 μs |  1.01 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_10KB         | SET 10KB             | 206.992 μs |  9.5479 μs | 0.5234 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_10KB               | SET 10KB             | 211.577 μs | 40.1602 μs | 2.2013 μs |  1.02 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_Small        | SET 13B              | 187.274 μs | 24.5937 μs | 1.3481 μs |  1.00 |    0.01 |     307 B |        1.00 |
| Respire_Set_Small              | SET 13B              | 197.872 μs |  8.5071 μs | 0.4663 μs |  1.06 |    0.01 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_1KB          | SET 1KB              | 196.826 μs | 13.4364 μs | 0.7365 μs |  1.00 |    0.00 |     312 B |        1.00 |
| Respire_Set_1KB                | SET 1KB              | 199.850 μs | 15.4843 μs | 0.8487 μs |  1.02 |    0.00 |      32 B |        0.10 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_Set_SteadyState  | SET x100 sequential  | 179.169 μs | 12.8392 μs | 0.7038 μs |  1.00 |    0.00 |     250 B |        1.00 |
| Respire_Set_SteadyState        | SET x100 sequential  | 183.145 μs | 26.3215 μs | 1.4428 μs |  1.02 |    0.01 |       3 B |        0.01 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SetDel           | SET+DEL              | 384.466 μs | 60.2447 μs | 3.3022 μs |  1.00 |    0.01 |     648 B |        1.00 |
| Respire_SetDel                 | SET+DEL              | 380.898 μs | 24.1718 μs | 1.3249 μs |  0.99 |    0.01 |     200 B |        0.31 |
|                                |                      |            |            |           |       |         |           |             |
| StackExchange_SIsMember        | SISMEMBER            | 193.097 μs | 40.2028 μs | 2.2037 μs |  1.00 |    0.01 |     312 B |        1.00 |
| Respire_SIsMember              | SISMEMBER            | 195.868 μs | 12.2267 μs | 0.6702 μs |  1.01 |    0.01 |      32 B |        0.10 |

## Reading the results

Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.
