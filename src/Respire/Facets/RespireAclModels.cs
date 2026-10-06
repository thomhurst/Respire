using System.Diagnostics.CodeAnalysis;

namespace Respire;

/// <summary>Owned ACL key or channel rules without lossy splitting or UTF-8 decoding.</summary>
/// <param name="RuleExpression">Redis 7+ rule expression, or null for the legacy array representation.</param>
/// <param name="LegacyPatterns">Redis 6.x patterns, or null for the rule-expression representation.</param>
public sealed record RespireAclPatterns(
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    byte[]? RuleExpression,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    byte[][]? LegacyPatterns);

/// <summary>An owned ACL selector. Unknown fields contain GC-owned results; disposal is optional.</summary>
public sealed record RespireAclSelector(
    string Commands, RespireAclPatterns Keys, RespireAclPatterns? Channels,
    IReadOnlyDictionary<string, RespireResult> AdditionalFields);

/// <summary>An owned ACL user definition. Channels require Redis 6.2; selectors require Redis 7.</summary>
/// <remarks>Unknown fields contain GC-owned results; disposal is optional. No result borrows a connection buffer.</remarks>
public sealed record RespireAclUser(
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    string[] Flags,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    string[] PasswordHashes, string Commands, RespireAclPatterns Keys,
    RespireAclPatterns? Channels,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    RespireAclSelector[] Selectors,
    IReadOnlyDictionary<string, RespireResult> AdditionalFields);

/// <summary>An owned ACL log entry. Reason and Context preserve new server-defined values.</summary>
/// <remarks>EntryId and Unix millisecond timestamps are available from Redis 7.2 and otherwise null.
/// Unknown fields contain GC-owned results; disposal is optional.</remarks>
public sealed record RespireAclLogEntry(
    long Count, string Reason, string Context,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    byte[] Object,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    byte[] Username,
    double AgeSeconds,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    byte[] ClientInfo, long? EntryId, long? TimestampCreated,
    long? TimestampLastUpdated, IReadOnlyDictionary<string, RespireResult> AdditionalFields);

/// <summary>The result of checking permissions without executing a command.</summary>
/// <param name="IsAllowed">Whether the server returned OK.</param>
/// <param name="DenialReason">The server's denial text, or null on success. Server errors still throw.</param>
public sealed record RespireAclDryRunResult(bool IsAllowed, string? DenialReason);
