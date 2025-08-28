```

BenchmarkDotNet v0.15.2, Windows 11 (10.0.26100.4946/24H2/2024Update/HudsonValley)
Unknown processor
.NET SDK 10.0.100-preview.4.25204.4
  [Host]     : .NET 8.0.19 (8.0.1925.36514), X64 RyuJIT AVX2
  Job-YBPNYS : .NET 8.0.19 (8.0.1925.36514), X64 RyuJIT AVX2

Runtime=.NET 8.0  IterationCount=3  WarmupCount=1  

```
| Method                        | Mean     | Error     | StdDev   | Min      | Max      | Median   | P95      | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------------------------ |---------:|----------:|---------:|---------:|---------:|---------:|---------:|------:|--------:|----------:|------------:|
| UltraRespire_Ping                | 216.8 μs |  66.84 μs |  3.66 μs | 212.8 μs | 220.1 μs | 217.5 μs | 219.8 μs |  1.00 |    0.02 |         - |          NA |
| StackExchange_Ping            | 319.9 μs | 185.72 μs | 10.18 μs | 310.0 μs | 330.4 μs | 319.3 μs | 329.3 μs |  1.48 |    0.05 |     424 B |          NA |
| UltraRespire_Set_Small           | 215.0 μs | 396.72 μs | 21.75 μs | 189.9 μs | 228.5 μs | 226.6 μs | 228.3 μs |  0.99 |    0.09 |         - |          NA |
| StackExchange_Set_Small       | 324.5 μs |  78.79 μs |  4.32 μs | 321.3 μs | 329.4 μs | 322.7 μs | 328.7 μs |  1.50 |    0.03 |     432 B |          NA |
| UltraRespire_Set_Medium          | 215.5 μs | 327.70 μs | 17.96 μs | 201.7 μs | 235.8 μs | 208.9 μs | 233.1 μs |  0.99 |    0.07 |         - |          NA |
| StackExchange_Set_Medium      | 270.5 μs | 535.99 μs | 29.38 μs | 248.5 μs | 303.9 μs | 259.1 μs | 299.4 μs |  1.25 |    0.12 |     432 B |          NA |
| UltraRespire_Set_Large           | 219.4 μs | 389.20 μs | 21.33 μs | 198.4 μs | 241.0 μs | 218.8 μs | 238.8 μs |  1.01 |    0.09 |         - |          NA |
| StackExchange_Set_Large       | 295.9 μs | 445.65 μs | 24.43 μs | 269.6 μs | 317.9 μs | 300.3 μs | 316.1 μs |  1.37 |    0.10 |     432 B |          NA |
| UltraRespire_Get                 | 180.7 μs | 367.00 μs | 20.12 μs | 159.1 μs | 198.9 μs | 184.1 μs | 197.4 μs |  0.83 |    0.08 |      40 B |          NA |
| StackExchange_Get             | 259.5 μs | 490.29 μs | 26.87 μs | 231.1 μs | 284.5 μs | 262.8 μs | 282.3 μs |  1.20 |    0.11 |     504 B |          NA |
| StackExchange_MGet            | 342.1 μs | 270.83 μs | 14.85 μs | 325.1 μs | 352.3 μs | 349.0 μs | 352.0 μs |  1.58 |    0.06 |     896 B |          NA |
| StackExchange_Pipeline_5_Sets | 283.1 μs | 270.41 μs | 14.82 μs | 266.8 μs | 295.7 μs | 286.9 μs | 294.8 μs |  1.31 |    0.06 |    2968 B |          NA |
| UltraRespire_Exists              | 219.6 μs | 250.13 μs | 13.71 μs | 204.0 μs | 229.8 μs | 224.9 μs | 229.3 μs |  1.01 |    0.06 |         - |          NA |
| StackExchange_Exists          | 289.4 μs | 426.01 μs | 23.35 μs | 262.8 μs | 306.6 μs | 298.6 μs | 305.8 μs |  1.33 |    0.10 |     400 B |          NA |
| UltraRespire_Del                 | 347.4 μs |  35.69 μs |  1.96 μs | 345.2 μs | 348.9 μs | 348.1 μs | 348.8 μs |  1.60 |    0.02 |         - |          NA |
| StackExchange_Del             | 553.1 μs | 387.50 μs | 21.24 μs | 537.0 μs | 577.2 μs | 545.1 μs | 574.0 μs |  2.55 |    0.09 |     664 B |          NA |
| UltraRespire_Incr                | 191.3 μs | 473.55 μs | 25.96 μs | 166.6 μs | 218.4 μs | 189.0 μs | 215.4 μs |  0.88 |    0.10 |         - |          NA |
| StackExchange_Incr            | 241.1 μs | 470.68 μs | 25.80 μs | 214.9 μs | 266.5 μs | 242.0 μs | 264.0 μs |  1.11 |    0.10 |     400 B |          NA |
