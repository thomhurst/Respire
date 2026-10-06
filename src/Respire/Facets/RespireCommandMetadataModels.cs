using System.Diagnostics.CodeAnalysis;

namespace Respire;

/// <summary>Owned COMMAND INFO metadata. Null entries in a query result represent unknown commands.</summary>
/// <remarks>Negative arity means a minimum argument count. Legacy key indexes include command tokens.
/// Key specifications retain their full structured server representation; they do not change client routing.
/// Arrays are caller-owned. Record equality compares array references, not their contents.</remarks>
public sealed record RespireCommandInfo(
    string Name, long Arity,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    string[] Flags, long FirstKey, long LastKey, long KeyStep,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    string[] AclCategories,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    string[] Tips,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    RespireResult[] KeySpecifications,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    RespireCommandInfo[] Subcommands,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    RespireResult[] AdditionalElements);

/// <summary>Owned documentary metadata for one command or subcommand.</summary>
/// <remarks>Optional fields remain null or empty when omitted by the server. Unknown fields are preserved.
/// Arrays are caller-owned. Record equality compares array references, not their contents.</remarks>
public sealed record RespireCommandDocumentation(
    string Name, string? Summary, string? Since, string? Group, string? Complexity,
    string? Module,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    string[] Flags, string? DeprecatedSince, string? ReplacedBy,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    RespireCommandHistory[] History,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    RespireCommandArgument[] Arguments,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    RespireCommandDocumentation[] Subcommands, IReadOnlyDictionary<string, RespireResult> AdditionalFields);

/// <summary>A documented command change and the server version that introduced it.</summary>
public sealed record RespireCommandHistory(string Version, string Description);

/// <summary>One recursive command argument description; Type and Flags preserve server strings.</summary>
/// <remarks>KeySpecificationIndex refers to COMMAND INFO metadata. Nested arguments describe blocks/alternatives.
/// Arrays are caller-owned. Record equality compares array references, not their contents.</remarks>
public sealed record RespireCommandArgument(
    string Name, string Type, string? DisplayText, long? KeySpecificationIndex,
    string? Token, string? Summary, string? Since, string? DeprecatedSince,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    string[] Flags,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    RespireCommandArgument[] Arguments, IReadOnlyDictionary<string, RespireResult> AdditionalFields);

/// <summary>One loaded module. Version is the module's integer version, not a Redis version.</summary>
/// <remarks>Path and Arguments are optional on older servers and retain binary bytes. All storage is owned.
/// Arrays are mutable; record equality compares their references, not their contents.</remarks>
public sealed record RespireModuleInfo(
    string Name, long Version,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    byte[]? Path,
    [param: SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Response DTO retains its existing array storage without copying on access.")]
    byte[][]? Arguments,
    IReadOnlyDictionary<string, RespireResult> AdditionalFields);

/// <summary>A background persistence request's acceptance state, not its eventual outcome.</summary>
public enum RespireBackgroundPersistenceState
{
    /// <summary>The server returned an unfamiliar success message; inspect Message.</summary>
    Unknown,
    /// <summary>The server reports that the background operation started.</summary>
    Started,
    /// <summary>The server reports that the operation was scheduled.</summary>
    Scheduled,
}

/// <summary>The server's owned background persistence reply. Use INFO persistence to check completion.</summary>
public sealed record RespireBackgroundPersistenceResult(RespireBackgroundPersistenceState State, string Message);
