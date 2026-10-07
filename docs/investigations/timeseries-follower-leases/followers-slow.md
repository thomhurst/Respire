```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9550/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i7-12700K 3.60GHz, 1 CPU, 20 logical and 12 physical cores
.NET SDK 10.0.401
  [Host] : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

Toolchain=InProcessEmitToolchain  InvocationCount=1  UnrollFactor=1  

```
| Method              | PageSize | ConsumerDelayMilliseconds | Mean     | Error    | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|-------------------- |--------- |-------------------------- |---------:|---------:|---------:|------:|--------:|----------:|------------:|
| **PerPage**             | **1**        | **1**                         | **984.6 ms** | **13.87 ms** | **12.30 ms** |  **1.00** |    **0.02** |  **199.2 KB** |        **1.00** |
| PersistentPrototype | 1        | 1                         | 986.9 ms | 10.76 ms | 10.06 ms |  1.00 |    0.02 | 202.66 KB |        1.02 |
|                     |          |                           |          |          |          |       |         |           |             |
| **PerPage**             | **8**        | **1**                         | **993.2 ms** |  **3.65 ms** |  **3.05 ms** |  **1.00** |    **0.00** |  **70.31 KB** |        **1.00** |
| PersistentPrototype | 8        | 1                         | 989.7 ms |  7.77 ms |  7.27 ms |  1.00 |    0.01 |  70.82 KB |        1.01 |
|                     |          |                           |          |          |          |       |         |           |             |
| **PerPage**             | **64**       | **1**                         | **996.2 ms** |  **3.13 ms** |  **2.93 ms** |  **1.00** |    **0.00** |   **53.8 KB** |        **1.00** |
| PersistentPrototype | 64       | 1                         | 997.4 ms |  3.17 ms |  2.81 ms |  1.00 |    0.00 |   53.7 KB |        1.00 |
