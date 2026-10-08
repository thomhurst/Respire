; Unshipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------------------------------------------------------
RESP001 | Respire  | Warning  | UndisposedPooledResultAnalyzer: pooled result never disposed
RESP002 | Respire  | Warning  | PendingReadBeforeFlushAnalyzer: pending read before the batch is sent
RESP003 | Respire  | Error    | RespireCommandGenerator: unsupported generated command declaration
RESP004 | Respire  | Error    | RespireHashGenerator: unsupported hash model or key template
RESP005 | Respire  | Error    | RespireJsonGenerator: unsupported JSON model or key template
