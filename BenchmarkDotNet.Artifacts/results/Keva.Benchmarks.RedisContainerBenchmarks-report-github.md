```

BenchmarkDotNet v0.15.2, Windows 11 (10.0.26100.4946/24H2/2024Update/HudsonValley)
Unknown processor
.NET SDK 10.0.100-preview.4.25204.4
  [Host]     : .NET 8.0.19 (8.0.1925.36514), X64 RyuJIT AVX2
  Job-YBPNYS : .NET 8.0.19 (8.0.1925.36514), X64 RyuJIT AVX2

Runtime=.NET 8.0  IterationCount=3  WarmupCount=1  

```
| Method                        | Mean       | Error       | StdDev    | Min        | Max        | Median     | P95        | Ratio | RatioSD | Gen0   | Gen1   | Allocated | Alloc Ratio |
|------------------------------ |-----------:|------------:|----------:|-----------:|-----------:|-----------:|-----------:|------:|--------:|-------:|-------:|----------:|------------:|
| Keva_Ping                     | 1,063.6 μs | 6,111.30 μs | 334.98 μs |   676.8 μs | 1,262.3 μs | 1,251.6 μs | 1,261.2 μs |  1.09 |    0.48 |      - |      - |   11650 B |        1.00 |
| StackExchange_Ping            |   241.7 μs |   328.12 μs |  17.99 μs |   229.7 μs |   262.4 μs |   232.9 μs |   259.4 μs |  0.25 |    0.08 |      - |      - |     444 B |        0.04 |
| Keva_Set_Small                | 1,047.6 μs | 3,649.82 μs | 200.06 μs |   825.2 μs | 1,212.9 μs | 1,104.6 μs | 1,202.1 μs |  1.07 |    0.40 |      - |      - |   11445 B |        0.98 |
| StackExchange_Set_Small       |   266.8 μs |   547.22 μs |  29.99 μs |   243.1 μs |   300.5 μs |   256.7 μs |   296.1 μs |  0.27 |    0.10 |      - |      - |     472 B |        0.04 |
| Keva_Set_Medium               | 1,095.6 μs | 1,129.10 μs |  61.89 μs | 1,025.0 μs | 1,140.5 μs | 1,121.2 μs | 1,138.6 μs |  1.12 |    0.38 | 0.9766 |      - |   14454 B |        1.24 |
| StackExchange_Set_Medium      |   287.3 μs |   701.57 μs |  38.46 μs |   242.9 μs |   310.7 μs |   308.2 μs |   310.4 μs |  0.29 |    0.10 |      - |      - |     472 B |        0.04 |
| Keva_Set_Large                | 1,147.8 μs | 1,767.39 μs |  96.88 μs | 1,065.6 μs | 1,254.6 μs | 1,123.3 μs | 1,241.5 μs |  1.17 |    0.40 | 3.9063 | 1.9531 |   52520 B |        4.51 |
| StackExchange_Set_Large       |   268.4 μs |    89.50 μs |   4.91 μs |   263.5 μs |   273.3 μs |   268.3 μs |   272.8 μs |  0.27 |    0.09 |      - |      - |     472 B |        0.04 |
| Keva_Get                      | 1,059.3 μs | 3,987.31 μs | 218.56 μs |   812.7 μs | 1,228.9 μs | 1,136.5 μs | 1,219.7 μs |  1.08 |    0.41 |      - |      - |   11390 B |        0.98 |
| StackExchange_Get             |   280.0 μs |   230.90 μs |  12.66 μs |   266.4 μs |   291.4 μs |   282.2 μs |   290.5 μs |  0.29 |    0.10 |      - |      - |     544 B |        0.05 |
| Keva_MGet                     |         NA |          NA |        NA |         NA |         NA |         NA |         NA |     ? |       ? |     NA |     NA |        NA |           ? |
| StackExchange_MGet            |   256.9 μs |   348.13 μs |  19.08 μs |   236.2 μs |   273.7 μs |   260.9 μs |   272.4 μs |  0.26 |    0.09 |      - |      - |     916 B |        0.08 |
| Keva_Pipeline_5_Sets          | 1,784.6 μs | 1,948.39 μs | 106.80 μs | 1,706.1 μs | 1,906.2 μs | 1,741.5 μs | 1,889.8 μs |  1.83 |    0.62 | 3.9063 | 1.9531 |   57383 B |        4.93 |
| StackExchange_Pipeline_5_Sets |   311.5 μs |   137.50 μs |   7.54 μs |   303.1 μs |   317.5 μs |   314.0 μs |   317.2 μs |  0.32 |    0.11 |      - |      - |    3008 B |        0.26 |
| Keva_Exists                   |   928.0 μs | 3,493.45 μs | 191.49 μs |   708.5 μs | 1,060.9 μs | 1,014.5 μs | 1,056.2 μs |  0.95 |    0.36 |      - |      - |   11365 B |        0.98 |
| StackExchange_Exists          |   244.4 μs |   161.88 μs |   8.87 μs |   237.1 μs |   254.3 μs |   241.7 μs |   253.0 μs |  0.25 |    0.08 |      - |      - |     440 B |        0.04 |
| Keva_Del                      |         NA |          NA |        NA |         NA |         NA |         NA |         NA |     ? |       ? |     NA |     NA |        NA |           ? |
| StackExchange_Del             |   448.0 μs |   304.65 μs |  16.70 μs |   436.4 μs |   467.1 μs |   440.4 μs |   464.5 μs |  0.46 |    0.15 |      - |      - |     744 B |        0.06 |
| Keva_Incr                     |         NA |          NA |        NA |         NA |         NA |         NA |         NA |     ? |       ? |     NA |     NA |        NA |           ? |
| StackExchange_Incr            |   238.4 μs |   607.87 μs |  33.32 μs |   210.1 μs |   275.1 μs |   230.0 μs |   270.6 μs |  0.24 |    0.09 |      - |      - |     420 B |        0.04 |

Benchmarks with issues:
  RedisContainerBenchmarks.Keva_MGet: Job-YBPNYS(Runtime=.NET 8.0, IterationCount=3, WarmupCount=1)
  RedisContainerBenchmarks.Keva_Del: Job-YBPNYS(Runtime=.NET 8.0, IterationCount=3, WarmupCount=1)
  RedisContainerBenchmarks.Keva_Incr: Job-YBPNYS(Runtime=.NET 8.0, IterationCount=3, WarmupCount=1)
