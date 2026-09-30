using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

public partial interface IServerCommands
{
    /// <summary>Returns the authenticated username as owned bytes from one node. Redis 6+.</summary>
    ValueTask<byte[]> AclWhoAmIAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns owned ACL configuration lines from one node. Redis 6+.</summary>
    ValueTask<byte[][]> AclListAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns an owned user definition, or null if absent, from one node. Redis 6+.</summary>
    ValueTask<RespireAclUser?> AclGetUserAsync(RespireValue username, CancellationToken cancellationToken = default);
    /// <summary>Applies ordered rule tokens to one node. Empty rules are valid. Requires AllowAdmin. Redis 6+.</summary>
    /// <remarks>Updates are additive unless rules include reset. Usernames and rules are not key-prefixed.
    /// ACL configuration is node-local; this does not configure other Cluster nodes or persist an ACL file.</remarks>
    ValueTask AclSetUserAsync(RespireValue username, ReadOnlySpan<RespireValue> rules, CancellationToken cancellationToken = default);
    /// <summary>Deletes users on one node and returns the number deleted. Requires AllowAdmin. Redis 6+.</summary>
    ValueTask<long> AclDeleteUsersAsync(ReadOnlySpan<RespireValue> usernames, CancellationToken cancellationToken = default);
    /// <summary>Lists categories, or commands in one category, on one node. Redis 6+.</summary>
    ValueTask<string[]> AclCategoriesAsync(RespireValue? category = null, CancellationToken cancellationToken = default);
    /// <summary>Returns owned ACL log entries, newest first, on one node. Null count uses the server default. Redis 6+.</summary>
    ValueTask<RespireAclLogEntry[]> AclLogAsync(int? count = null, CancellationToken cancellationToken = default);
    /// <summary>Clears one node's ACL log. Requires AllowAdmin. Redis 6+.</summary>
    ValueTask AclLogResetAsync(CancellationToken cancellationToken = default);
    /// <summary>Checks permissions on one node without executing the command. Redis 7+.</summary>
    /// <remarks>Denials are values; missing users, invalid commands and server errors throw.
    /// Command arguments are separate binary-safe tokens and are not key-prefixed.</remarks>
    ValueTask<RespireAclDryRunResult> AclDryRunAsync(RespireValue username, RespireCommand command, ReadOnlySpan<RespireValue> arguments, CancellationToken cancellationToken = default);

    /// <summary>Queries each discovered node, including replicas, with endpoint provenance; standalone returns one result.</summary>
    /// <remarks>All ACL OnAllNodes methods throw cancellation during discovery. After discovery, failures and
    /// cancellation are per-node results. Inspect every result. Operations are not atomic across nodes.</remarks>
    ValueTask<RespireServerResult<byte[]>[]> AclWhoAmIOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns each discovered node's ACL lines separately. See AclWhoAmIOnAllNodesAsync for failure semantics.</summary>
    ValueTask<RespireServerResult<byte[][]>[]> AclListOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Returns each discovered node's user definition, including null for missing users.</summary>
    ValueTask<RespireServerResult<RespireAclUser?>[]> AclGetUserOnAllNodesAsync(RespireValue username, CancellationToken cancellationToken = default);
    /// <summary>Explicitly applies rules on each discovered node. Requires AllowAdmin; true means OK on that node.</summary>
    /// <remarks>Partial success is possible. ACL configuration is not automatically replicated or rolled back.</remarks>
    ValueTask<RespireServerResult<bool>[]> AclSetUserOnAllNodesAsync(RespireValue username, ReadOnlySpan<RespireValue> rules, CancellationToken cancellationToken = default);
    /// <summary>Explicitly deletes users on each discovered node. Requires AllowAdmin; returns per-node counts.</summary>
    ValueTask<RespireServerResult<long>[]> AclDeleteUsersOnAllNodesAsync(ReadOnlySpan<RespireValue> usernames, CancellationToken cancellationToken = default);
    /// <summary>Returns categories or category commands separately from each discovered node.</summary>
    ValueTask<RespireServerResult<string[]>[]> AclCategoriesOnAllNodesAsync(RespireValue? category = null, CancellationToken cancellationToken = default);
    /// <summary>Returns owned ACL log entries separately from each discovered node.</summary>
    ValueTask<RespireServerResult<RespireAclLogEntry[]>[]> AclLogOnAllNodesAsync(int? count = null, CancellationToken cancellationToken = default);
    /// <summary>Explicitly clears each discovered node's ACL log. Requires AllowAdmin; true means OK on that node.</summary>
    ValueTask<RespireServerResult<bool>[]> AclLogResetOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Checks permissions separately on each discovered node without executing the command.</summary>
    ValueTask<RespireServerResult<RespireAclDryRunResult>[]> AclDryRunOnAllNodesAsync(RespireValue username, RespireCommand command, ReadOnlySpan<RespireValue> arguments, CancellationToken cancellationToken = default);
}

internal sealed partial class ServerCommands
{
    // ACL identifiers, rules and simulated keys must never affect routing or key-prefix rewriting.
    private static readonly Verb AclWhoAmI = new(-1, "ACL", "WHOAMI");
    private static readonly Verb AclList = new(-1, "ACL", "LIST");
    private static readonly Verb AclGetUser = new(-1, "ACL", "GETUSER");
    private static readonly Verb AclSetUser = new(-1, "ACL", "SETUSER");
    private static readonly Verb AclDeleteUsers = new(-1, "ACL", "DELUSER");
    private static readonly Verb AclCategories = new(-1, "ACL", "CAT");
    private static readonly Verb AclLog = new(-1, "ACL", "LOG");
    private static readonly Verb AclDryRun = new(-1, "ACL", "DRYRUN");
    private readonly record struct AclCall<T>(string Operation, CmdN Command, ResponseConverter<ServerCommands, T> Convert, bool RequiresAdmin = false);
    private static readonly AclCall<byte[]> WhoAmICall = new("ACL WHOAMI", new(AclWhoAmI, []), static (ServerCommands _, in RespValue value) => AclParser.Bytes(in value));
    private static readonly AclCall<byte[][]> ListAclCall = new("ACL LIST", new(AclList, []), static (ServerCommands _, in RespValue value) => AclParser.ByteStrings(in value));
    private static readonly AclCall<bool> ResetAclLogCall = new("ACL LOG RESET", new(AclLog, ["RESET"]), static (ServerCommands _, in RespValue value) => AclParser.Ok(in value), true);

    public ValueTask<byte[]> AclWhoAmIAsync(CancellationToken cancellationToken = default) => ExecuteAclAsync(WhoAmICall, cancellationToken);
    public ValueTask<byte[][]> AclListAsync(CancellationToken cancellationToken = default) => ExecuteAclAsync(ListAclCall, cancellationToken);
    public ValueTask<RespireAclUser?> AclGetUserAsync(RespireValue username, CancellationToken cancellationToken = default) => ExecuteAclAsync(GetAclUserCall(username), cancellationToken);
    public ValueTask AclSetUserAsync(RespireValue username, ReadOnlySpan<RespireValue> rules, CancellationToken cancellationToken = default) => DiscardAclOkAsync(ExecuteAclAsync(SetAclUserCall(username, rules), cancellationToken));
    public ValueTask<long> AclDeleteUsersAsync(ReadOnlySpan<RespireValue> usernames, CancellationToken cancellationToken = default) => ExecuteAclAsync(DeleteAclUsersCall(usernames), cancellationToken);
    public ValueTask<string[]> AclCategoriesAsync(RespireValue? category = null, CancellationToken cancellationToken = default) => ExecuteAclAsync(AclCategoriesCall(category), cancellationToken);
    public ValueTask<RespireAclLogEntry[]> AclLogAsync(int? count = null, CancellationToken cancellationToken = default) => ExecuteAclAsync(ReadAclLogCall(count), cancellationToken);
    public ValueTask AclLogResetAsync(CancellationToken cancellationToken = default) => DiscardAclOkAsync(ExecuteAclAsync(ResetAclLogCall, cancellationToken));
    public ValueTask<RespireAclDryRunResult> AclDryRunAsync(RespireValue username, RespireCommand command, ReadOnlySpan<RespireValue> arguments, CancellationToken cancellationToken = default) => ExecuteAclAsync(DryRunAclCall(username, command, arguments), cancellationToken);

    public ValueTask<RespireServerResult<byte[]>[]> AclWhoAmIOnAllNodesAsync(CancellationToken cancellationToken = default) => ExecuteAclOnAllNodesAsync(WhoAmICall, cancellationToken);
    public ValueTask<RespireServerResult<byte[][]>[]> AclListOnAllNodesAsync(CancellationToken cancellationToken = default) => ExecuteAclOnAllNodesAsync(ListAclCall, cancellationToken);
    public ValueTask<RespireServerResult<RespireAclUser?>[]> AclGetUserOnAllNodesAsync(RespireValue username, CancellationToken cancellationToken = default) => ExecuteAclOnAllNodesAsync(GetAclUserCall(username), cancellationToken);
    public ValueTask<RespireServerResult<bool>[]> AclSetUserOnAllNodesAsync(RespireValue username, ReadOnlySpan<RespireValue> rules, CancellationToken cancellationToken = default) => ExecuteAclOnAllNodesAsync(SetAclUserCall(username, rules), cancellationToken);
    public ValueTask<RespireServerResult<long>[]> AclDeleteUsersOnAllNodesAsync(ReadOnlySpan<RespireValue> usernames, CancellationToken cancellationToken = default) => ExecuteAclOnAllNodesAsync(DeleteAclUsersCall(usernames), cancellationToken);
    public ValueTask<RespireServerResult<string[]>[]> AclCategoriesOnAllNodesAsync(RespireValue? category = null, CancellationToken cancellationToken = default) => ExecuteAclOnAllNodesAsync(AclCategoriesCall(category), cancellationToken);
    public ValueTask<RespireServerResult<RespireAclLogEntry[]>[]> AclLogOnAllNodesAsync(int? count = null, CancellationToken cancellationToken = default) => ExecuteAclOnAllNodesAsync(ReadAclLogCall(count), cancellationToken);
    public ValueTask<RespireServerResult<bool>[]> AclLogResetOnAllNodesAsync(CancellationToken cancellationToken = default) => ExecuteAclOnAllNodesAsync(ResetAclLogCall, cancellationToken);
    public ValueTask<RespireServerResult<RespireAclDryRunResult>[]> AclDryRunOnAllNodesAsync(RespireValue username, RespireCommand command, ReadOnlySpan<RespireValue> arguments, CancellationToken cancellationToken = default) => ExecuteAclOnAllNodesAsync(DryRunAclCall(username, command, arguments), cancellationToken);

    private ValueTask<T> ExecuteAclAsync<T>(AclCall<T> call, CancellationToken cancellationToken)
    {
        if (call.RequiresAdmin) EnsureAdminAllowed(call.Operation);
        return ConvertAsync(call.Operation, call.Command, cancellationToken, call.Convert);
    }

    private async ValueTask<RespireServerResult<T>[]> ExecuteAclOnAllNodesAsync<T>(AclCall<T> call, CancellationToken cancellationToken)
    {
        if (call.RequiresAdmin) EnsureAdminAllowed(call.Operation);
        var cache = client.Core.ClientCache;
        var fence = cache is null ? default : cache.BeforeCommand(call.Operation, call.Command);
        try
        {
            return await FanOutAsync(call.Operation, call.Command, cancellationToken, call.Convert).ConfigureAwait(false);
        }
        finally
        {
            if (fence.IsRequired) cache!.CompleteMutation(in fence);
        }
    }

    private static async ValueTask DiscardAclOkAsync(ValueTask<bool> operation) => _ = await operation.ConfigureAwait(false);

    private static AclCall<RespireAclUser?> GetAclUserCall(RespireValue username)
        => new("ACL GETUSER", new(AclGetUser, [SnapshotAclArgument(username, nameof(username))]), static (ServerCommands _, in RespValue value) => AclParser.User(in value));

    private static AclCall<bool> SetAclUserCall(RespireValue username, ReadOnlySpan<RespireValue> rules)
    {
        var arguments = new RespireValue[rules.Length + 1];
        arguments[0] = SnapshotAclArgument(username, nameof(username));
        for (var index = 0; index < rules.Length; index++) arguments[index + 1] = SnapshotAclArgument(rules[index], nameof(rules));
        return new("ACL SETUSER", new(AclSetUser, arguments), static (ServerCommands _, in RespValue value) => AclParser.Ok(in value), true);
    }

    private static AclCall<long> DeleteAclUsersCall(ReadOnlySpan<RespireValue> usernames)
    {
        if (usernames.IsEmpty) throw new ArgumentException("At least one username is required.", nameof(usernames));
        var arguments = new RespireValue[usernames.Length];
        for (var index = 0; index < usernames.Length; index++) arguments[index] = SnapshotAclArgument(usernames[index], nameof(usernames));
        return new("ACL DELUSER", new(AclDeleteUsers, arguments), static (ServerCommands _, in RespValue value) => AclParser.NonnegativeInteger(in value), true);
    }

    private static AclCall<string[]> AclCategoriesCall(RespireValue? category)
        => new("ACL CAT", new(AclCategories, category is { } value ? [SnapshotAclArgument(value, nameof(category))] : []), static (ServerCommands _, in RespValue value) => AclParser.Strings(in value));

    private static AclCall<RespireAclLogEntry[]> ReadAclLogCall(int? count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        return new("ACL LOG", new(AclLog, count is { } value ? [value] : []), static (ServerCommands _, in RespValue value) => AclParser.Log(in value));
    }

    private static AclCall<RespireAclDryRunResult> DryRunAclCall(RespireValue username, RespireCommand command, ReadOnlySpan<RespireValue> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name, nameof(command));
        var words = command.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokens = new RespireValue[1 + words.Length + arguments.Length];
        tokens[0] = SnapshotAclArgument(username, nameof(username));
        for (var index = 0; index < words.Length; index++) tokens[index + 1] = words[index];
        for (var index = 0; index < arguments.Length; index++) tokens[index + 1 + words.Length] = SnapshotAclArgument(arguments[index], nameof(arguments));
        return new("ACL DRYRUN", new(AclDryRun, tokens), static (ServerCommands _, in RespValue value) => AclParser.DryRun(in value));
    }

    private static RespireValue SnapshotAclArgument(RespireValue value, string parameterName)
    {
        RespireValue.ThrowIfNull(value, parameterName);
        return value.Snapshot();
    }
}
