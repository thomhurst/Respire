; Unshipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------------------------------------------------------
RESP007 | Respire | Disabled | StackExchangeMigrationAnalyzer: migrate supported database methods
RESP008 | Respire | Disabled | StackExchangeMigrationAnalyzer: manual migration required
RESP009 | Respire | Warning | SyncOverAsyncAnalyzer: await Respire operations
RESP010 | Respire | Warning | ShortLivedClientAnalyzer: share a long-lived client
RESP011 | Respire | Warning | UnboundedReadAnalyzer: scan or page large collections
RESP012 | Respire | Warning | BlockingSubscriptionHandlerAnalyzer: avoid blocking subscription consumers
