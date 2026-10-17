using Respire.Commands;
using Respire.Protocol;

namespace Respire;

public partial interface IRespireServerCommands
{
    /// <summary>Describes configuration parameters matching one or more glob patterns on one node. Valkey 9.2+.</summary>
    ValueTask<RespireConfigurationInfo[]> ConfigInfoAsync(ReadOnlySpan<RespireValue> patterns, CancellationToken cancellationToken = default);
    /// <summary>Returns the digest of effective ACL rules on one node. Valkey 9.2+.</summary>
    ValueTask<byte[]> AclDigestAsync(CancellationToken cancellationToken = default);
    /// <summary>Lists ACL roles on one node as owned names. Valkey 9.2+.</summary>
    ValueTask<byte[][]> AclRolesAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns an owned ACL role, or null if absent, on one node. Valkey 9.2+.</summary>
    ValueTask<RespireAclRole?> AclGetRoleAsync(RespireValue role, CancellationToken cancellationToken = default);
    /// <summary>Applies ordered role rules on one node. Requires AllowAdmin. Valkey 9.2+.</summary>
    /// <remarks>Names and rules are binary-safe, unprefixed tokens. Configuration is node-local and is not persisted.</remarks>
    ValueTask AclSetRoleAsync(RespireValue role, ReadOnlySpan<RespireValue> rules, CancellationToken cancellationToken = default);
    /// <summary>Deletes roles on one node and returns the number deleted. Requires AllowAdmin. Valkey 9.2+.</summary>
    ValueTask<long> AclDeleteRolesAsync(ReadOnlySpan<RespireValue> roles, CancellationToken cancellationToken = default);
}

internal sealed partial class ServerCommands
{
    public ValueTask<RespireConfigurationInfo[]> ConfigInfoAsync(ReadOnlySpan<RespireValue> patterns, CancellationToken cancellationToken = default)
    {
        var arguments = SnapshotValkeyArguments(patterns, nameof(patterns));
        return ExecuteMetadataAsync(new MetadataCall<RespireConfigurationInfo[]>("CONFIG INFO", new(new Verb(-1, "CONFIG", "INFO"), arguments),
            static (ServerCommands _, in RespValue reply) => CommandMetadataParser.Configuration(in reply), RequiresAdmin: false), cancellationToken);
    }

    public ValueTask<byte[]> AclDigestAsync(CancellationToken cancellationToken = default)
        => ExecuteAclAsync(new AclCall<byte[]>("ACL DIGEST", new(new Verb(-1, "ACL", "DIGEST"), []),
            static (ServerCommands _, in RespValue reply) => AclParser.Bytes(in reply)), cancellationToken);

    public ValueTask<byte[][]> AclRolesAsync(CancellationToken cancellationToken = default)
        => ExecuteAclAsync(new AclCall<byte[][]>("ACL ROLES", new(new Verb(-1, "ACL", "ROLES"), []),
            static (ServerCommands _, in RespValue reply) => AclParser.ByteStrings(in reply)), cancellationToken);

    public ValueTask<RespireAclRole?> AclGetRoleAsync(RespireValue role, CancellationToken cancellationToken = default)
        => ExecuteAclAsync(new AclCall<RespireAclRole?>("ACL GETROLE", new(new Verb(-1, "ACL", "GETROLE"), [SnapshotAclArgument(role, nameof(role))]),
            static (ServerCommands _, in RespValue reply) => AclParser.Role(in reply)), cancellationToken);

    public ValueTask AclSetRoleAsync(RespireValue role, ReadOnlySpan<RespireValue> rules, CancellationToken cancellationToken = default)
    {
        var arguments = new RespireValue[rules.Length + 1];
        arguments[0] = SnapshotAclArgument(role, nameof(role));
        for (var index = 0; index < rules.Length; index++) arguments[index + 1] = SnapshotAclArgument(rules[index], nameof(rules));
        return DiscardAclOkAsync(ExecuteAclAsync(new AclCall<bool>("ACL SETROLE", new(new Verb(-1, "ACL", "SETROLE"), arguments),
            static (ServerCommands _, in RespValue reply) => AclParser.Ok(in reply), RequiresAdmin: true), cancellationToken));
    }

    public ValueTask<long> AclDeleteRolesAsync(ReadOnlySpan<RespireValue> roles, CancellationToken cancellationToken = default)
        => ExecuteAclAsync(new AclCall<long>("ACL DELROLE", new(new Verb(-1, "ACL", "DELROLE"), SnapshotValkeyArguments(roles, nameof(roles))),
            static (ServerCommands _, in RespValue reply) => AclParser.NonnegativeInteger(in reply), RequiresAdmin: true), cancellationToken);

    private static RespireValue[] SnapshotValkeyArguments(ReadOnlySpan<RespireValue> values, string parameterName)
    {
        if (values.IsEmpty) throw new ArgumentException("At least one argument is required.", parameterName);
        var arguments = new RespireValue[values.Length];
        for (var index = 0; index < values.Length; index++) arguments[index] = SnapshotAclArgument(values[index], parameterName);
        return arguments;
    }
}
