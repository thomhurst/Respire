namespace Respire;

/// <summary>An owned Valkey 9.2 configuration description. Numeric bounds retain their exact decimal strings.</summary>
/// <remarks>Unknown fields are owned results; disposal is optional. Missing optional fields are null or empty lists.</remarks>
public sealed record RespireConfigurationInfo(
    string Name, string Type, IReadOnlyList<string> Flags, string? Alias,
    IReadOnlyList<string> Values, IReadOnlyList<string> Range,
    IReadOnlyDictionary<string, RespireResult> AdditionalFields);

/// <summary>An owned Valkey 9.2 ACL role. The root and additional selectors retain database rules in AdditionalFields.</summary>
public sealed record RespireAclRole(
    RespireAclSelector Root, IReadOnlyList<RespireAclSelector> Selectors, IReadOnlyList<byte[]> Users);
