```

BenchmarkDotNet v0.15.2, Windows 11 (10.0.26100.4946/24H2/2024Update/HudsonValley)
Unknown processor
.NET SDK 10.0.100-preview.4.25204.4
  [Host]     : .NET 8.0.19 (8.0.1925.36514), X64 RyuJIT AVX2
  Job-YFEFPZ : .NET 8.0.19 (8.0.1925.36514), X64 RyuJIT AVX2

IterationCount=10  WarmupCount=3  

```
| Method                          | Mean          | Error         | StdDev       | Gen0    | Gen1   | Allocated |
|-------------------------------- |--------------:|--------------:|-------------:|--------:|-------:|----------:|
| &#39;Parse Command Array&#39;           |     289.89 ns |     35.131 ns |    20.906 ns |  0.0134 |      - |     168 B |
| &#39;Parse Large Array (100 items)&#39; |   4,133.33 ns |    190.256 ns |   113.218 ns |  0.3204 |      - |    4024 B |
| &#39;Parse Nested Array&#39;            |     526.94 ns |      4.894 ns |     2.912 ns |  0.0248 |      - |     320 B |
| &#39;Parse Mixed Types Array&#39;       |     362.49 ns |      7.567 ns |     5.005 ns |  0.0181 |      - |     232 B |
| &#39;Parse Simple String&#39;           |      60.40 ns |      1.572 ns |     0.936 ns |  0.0025 |      - |      32 B |
| &#39;Parse Bulk String&#39;             |      81.24 ns |      3.728 ns |     2.466 ns |  0.0025 |      - |      32 B |
| &#39;Parse Integer&#39;                 |      61.62 ns |      1.206 ns |     0.631 ns |  0.0019 |      - |      24 B |
| &#39;Round-trip Simple Types&#39;       |     108.24 ns |      4.513 ns |     2.985 ns |  0.0025 |      - |      32 B |
| &#39;Round-trip Command Array&#39;      |     405.21 ns |     19.525 ns |    11.619 ns |  0.0134 |      - |     168 B |
| &#39;Round-trip Large Array&#39;        |   6,245.07 ns |    230.789 ns |   152.652 ns |  0.3204 |      - |    4024 B |
| &#39;1000 Parse Operations&#39;         | 380,541.92 ns | 10,633.999 ns | 7,033.732 ns | 18.0664 |      - |  232000 B |
| &#39;1000 Write Operations&#39;         | 112,703.89 ns |  2,169.699 ns | 1,134.795 ns |  2.4414 |      - |   32128 B |
| &#39;Write Array of 100 Integers&#39;   |   2,113.80 ns |     79.464 ns |    41.561 ns |  0.3204 | 0.0038 |    4024 B |
| &#39;Write Mixed Types Array&#39;       |     265.93 ns |     16.171 ns |     9.623 ns |  0.0181 |      - |     232 B |
| &#39;Write Command&#39;                 |     181.13 ns |      9.132 ns |     6.040 ns |  0.0153 |      - |     192 B |
| &#39;Write Large Command (10 args)&#39; |     594.04 ns |     31.988 ns |    21.158 ns |  0.0505 |      - |     640 B |
| &#39;Write Simple String&#39;           |      32.28 ns |      0.833 ns |     0.551 ns |  0.0025 |      - |      32 B |
| &#39;Write Bulk String&#39;             |      38.76 ns |      2.514 ns |     1.663 ns |  0.0032 |      - |      40 B |
| &#39;Write Integer&#39;                 |      19.89 ns |      0.766 ns |     0.507 ns |  0.0019 |      - |      24 B |
