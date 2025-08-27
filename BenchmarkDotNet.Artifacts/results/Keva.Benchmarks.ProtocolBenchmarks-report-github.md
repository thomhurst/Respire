```

BenchmarkDotNet v0.15.2, Windows 11 (10.0.26100.4946/24H2/2024Update/HudsonValley)
Unknown processor
.NET SDK 10.0.100-preview.4.25204.4
  [Host]     : .NET 8.0.19 (8.0.1925.36514), X64 RyuJIT AVX2
  Job-YFEFPZ : .NET 8.0.19 (8.0.1925.36514), X64 RyuJIT AVX2

IterationCount=10  WarmupCount=3  

```
| Method                          | Mean          | Error          | StdDev        | Gen0    | Gen1   | Allocated |
|-------------------------------- |--------------:|---------------:|--------------:|--------:|-------:|----------:|
| &#39;Parse Command Array&#39;           |     334.79 ns |     103.453 ns |     68.428 ns |  0.0134 |      - |     168 B |
| &#39;Parse Large Array (100 items)&#39; |   4,274.22 ns |      80.404 ns |     53.182 ns |  0.3204 |      - |    4024 B |
| &#39;Parse Nested Array&#39;            |     524.08 ns |      12.669 ns |      7.539 ns |  0.0248 |      - |     320 B |
| &#39;Parse Mixed Types Array&#39;       |     374.31 ns |       6.553 ns |      3.427 ns |  0.0181 |      - |     232 B |
| &#39;Parse Simple String&#39;           |      61.81 ns |       1.276 ns |      0.844 ns |  0.0025 |      - |      32 B |
| &#39;Parse Bulk String&#39;             |      89.48 ns |       2.945 ns |      1.948 ns |  0.0025 |      - |      32 B |
| &#39;Parse Integer&#39;                 |      65.00 ns |       4.183 ns |      2.489 ns |  0.0019 |      - |      24 B |
| &#39;Round-trip Simple Types&#39;       |     107.55 ns |       4.162 ns |      2.753 ns |  0.0024 |      - |      32 B |
| &#39;Round-trip Command Array&#39;      |     552.21 ns |     174.998 ns |    115.750 ns |  0.0134 |      - |     168 B |
| &#39;Round-trip Large Array&#39;        |   8,245.35 ns |   3,287.094 ns |  2,174.209 ns |  0.3204 |      - |    4024 B |
| &#39;1000 Parse Operations&#39;         | 462,227.14 ns | 119,857.263 ns | 79,278.158 ns | 18.0664 |      - |  232000 B |
| &#39;1000 Write Operations&#39;         | 116,858.80 ns |   1,656.891 ns |  1,095.931 ns |  2.4414 |      - |   32128 B |
| &#39;Write Array of 100 Integers&#39;   |   2,155.80 ns |      44.919 ns |     26.731 ns |  0.3204 | 0.0038 |    4024 B |
| &#39;Write Mixed Types Array&#39;       |     285.08 ns |       7.075 ns |      4.210 ns |  0.0181 |      - |     232 B |
| &#39;Write Command&#39;                 |     167.82 ns |       4.792 ns |      3.170 ns |  0.0153 |      - |     192 B |
| &#39;Write Large Command (10 args)&#39; |     596.28 ns |      14.791 ns |      8.802 ns |  0.0505 |      - |     640 B |
| &#39;Write Simple String&#39;           |      33.69 ns |       0.943 ns |      0.623 ns |  0.0025 |      - |      32 B |
| &#39;Write Bulk String&#39;             |      37.89 ns |       0.663 ns |      0.439 ns |  0.0032 |      - |      40 B |
| &#39;Write Integer&#39;                 |      21.22 ns |       0.442 ns |      0.292 ns |  0.0019 |      - |      24 B |
