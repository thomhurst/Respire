```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26200.9550/25H2/2025Update/HudsonValley2)
12th Gen Intel Core i7-12700K 3.60GHz, 1 CPU, 20 logical and 12 physical cores
.NET SDK 10.0.401
  [Host] : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3

Toolchain=InProcessEmitToolchain  

```
| Method             | Mean     | Error    | StdDev   | Ratio | Allocated | Alloc Ratio |
|------------------- |---------:|---------:|---------:|------:|----------:|------------:|
| PerPageRentReturn  | 38.40 ns | 0.292 ns | 0.273 ns |  1.00 |         - |          NA |
| RetainedLeaseCheck | 11.72 ns | 0.139 ns | 0.130 ns |  0.31 |         - |          NA |
