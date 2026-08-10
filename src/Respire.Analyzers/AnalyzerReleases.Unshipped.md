; Unshipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------------------------------------------------------
RESP001 | Respire  | Warning  | UndisposedPooledResultAnalyzer: pooled result never disposed
RESP002 | Respire  | Warning  | PendingReadBeforeFlushAnalyzer: pending read before the batch is sent
