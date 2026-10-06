```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9550/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i7-12700K 3.60GHz, 1 CPU, 20 logical and 12 physical cores
.NET SDK 10.0.401
  [Host] : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

Toolchain=InProcessEmitToolchain  UnrollFactor=1  

```
| Method              | PageSize | ConsumerDelayMilliseconds | Mean        | Error     | StdDev    | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|-------------------- |--------- |-------------------------- |------------:|----------:|----------:|------:|--------:|-------:|----------:|------------:|
| **PerPage**             | **1**        | **0**                         | **19,505.1 μs** | **368.16 μs** | **326.37 μs** |  **1.00** |    **0.02** |      **-** | **187.95 KB** |        **1.00** |
| PersistentPrototype | 1        | 0                         | 19,977.6 μs | 373.98 μs | 384.05 μs |  1.02 |    0.03 |      - | 188.34 KB |        1.00 |
|                     |          |                           |             |           |           |       |         |        |           |             |
| **PerPage**             | **8**        | **0**                         |  **2,604.0 μs** |  **51.73 μs** |  **74.18 μs** |  **1.00** |    **0.04** | **3.9063** |  **55.85 KB** |        **1.00** |
| PersistentPrototype | 8        | 0                         |  2,655.7 μs |  60.55 μs | 166.76 μs |  1.02 |    0.07 | 3.9063 |  56.03 KB |        1.00 |
|                     |          |                           |             |           |           |       |         |        |           |             |
| **PerPage**             | **64**       | **0**                         |    **357.1 μs** |   **7.12 μs** |  **13.03 μs** |  **1.00** |    **0.05** | **2.9297** |  **39.31 KB** |        **1.00** |
| PersistentPrototype | 64       | 0                         |    383.3 μs |   9.19 μs |  26.67 μs |  1.07 |    0.08 | 2.9297 |  39.61 KB |        1.01 |
