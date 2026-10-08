using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

/// <summary>Explicit node targeting for server administration.</summary>
public static class RespireServerNodeExtensions
{
    /// <summary>Creates a node handle without connecting or discovering topology.</summary>
    /// <remarks>Supported by Respire's server facet. Third-party implementations can provide their own extension.
    /// Each operation uses an independent, short-lived connection to this endpoint. Mutations require AllowAdmin.
    /// The endpoint is never replaced by routing, failover, redirects, or replay.</remarks>
    public static RespireServerNode OnNode(this IServerCommands server, RespireEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server is ServerCommands commands ? commands.CreateNode(endpoint)
            : throw new NotSupportedException("Explicit node targeting requires Respire's server facet.");
    }
}

/// <summary>Typed server operations at one explicitly selected physical endpoint.</summary>
/// <remarks>Uses the client's authentication, TLS, timeouts, protocol, and database. Key arguments and returned keys
/// are physical server keys: key prefixes on client views are not applied. BUSY controls use RESP2 and database zero
/// without setup commands other than authentication. The handle owns no persistent socket and follows client disposal.</remarks>
public sealed partial class RespireServerNode
{
    private readonly RespireClient _client;

    internal RespireServerNode(RespireClient client, RespireEndpoint endpoint)
    {
        ValidateEndpoint(endpoint, allowUnixSocket: true);
        if (endpoint.Port == 0 && client.Core.Options.UseTls)
            throw new ArgumentException("Unix sockets cannot use TLS.", nameof(endpoint));
        _client = client;
        Endpoint = endpoint;
    }

    /// <summary>The exact endpoint used by every operation.</summary>
    public RespireEndpoint Endpoint { get; }

    /// <summary>Generates an owned hexadecimal password. Bits must be 1 through 1024; default is 256. Redis: ACL GENPASS.</summary>
    public ValueTask<string> AclGeneratePasswordAsync(int? bits = null, CancellationToken cancellationToken = default)
    {
        if (bits is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(bits));
        return ExecuteAsync("ACL GENPASS", bits is { } count ? [count] : [], ServerDiagnosticsParser.Text, cancellationToken);
    }

    /// <summary>Lists owned binary ACL usernames. Redis: ACL USERS.</summary>
    public ValueTask<byte[][]> AclUsersAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("ACL USERS", [], AclParser.ByteStrings, cancellationToken);

    /// <summary>Replaces ACL state from the configured aclfile. Requires AllowAdmin. Redis: ACL LOAD.</summary>
    public ValueTask AclLoadAsync(CancellationToken cancellationToken = default)
        => MutationAsync("ACL LOAD", [], cancellationToken);

    /// <summary>Writes current ACL state to the configured aclfile. Requires AllowAdmin. Redis: ACL SAVE.</summary>
    public ValueTask AclSaveAsync(CancellationToken cancellationToken = default)
        => MutationAsync("ACL SAVE", [], cancellationToken);

    /// <summary>Writes SHUTDOWN to a dedicated control socket. Requires AllowAdmin.</summary>
    /// <remarks>Completion confirms only the local socket write, not server acceptance or shutdown. Redis sends no
    /// success reply; server-side errors are not observed by this request API. Verify shutdown independently.
    /// Cancellation or transport failure after submission has an ambiguous outcome. The request is never replayed.</remarks>
    public ValueTask SendShutdownAsync(RespireShutdownOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        var arguments = new List<RespireValue>(3);
        switch (options.SaveMode)
        {
            case RespireShutdownSaveMode.Default: break;
            case RespireShutdownSaveMode.Save: arguments.Add("SAVE"); break;
            case RespireShutdownSaveMode.NoSave: arguments.Add("NOSAVE"); break;
            default: throw new ArgumentOutOfRangeException(nameof(options));
        }
        if (options.Now) arguments.Add("NOW");
        if (options.Force) arguments.Add("FORCE");
        return ShutdownWriteAsync(arguments.ToArray(), cancellationToken);
    }

    /// <summary>Aborts an in-progress shutdown and awaits OK. Requires AllowAdmin and Redis 7.0 or later.</summary>
    public ValueTask AbortShutdownAsync(CancellationToken cancellationToken = default)
        => MutationAsync("SHUTDOWN", ["ABORT"], cancellationToken, NodeCallKind.ControlMutation);

    /// <summary>Starts coordinated FAILOVER. Requires AllowAdmin and Redis 6.2 or later.</summary>
    public ValueTask FailoverAsync(RespireFailoverOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (options.Force && (options.Target is null || options.Timeout is null))
            throw new ArgumentException("Forced failover requires Target and Timeout.", nameof(options));
        var arguments = new List<RespireValue>(6);
        if (options.Target is { } target)
        {
            ValidateEndpoint(target, allowUnixSocket: false);
            arguments.Add("TO"); arguments.Add(target.Host); arguments.Add(target.Port);
            if (options.Force) arguments.Add("FORCE");
        }
        if (options.Timeout is { } timeout) { arguments.Add("TIMEOUT"); arguments.Add(Milliseconds(timeout, nameof(options))); }
        return MutationAsync("FAILOVER", arguments.ToArray(), cancellationToken);
    }

    /// <summary>Aborts coordinated FAILOVER. Requires AllowAdmin. Aborting can leave inconsistent replication state.</summary>
    public ValueTask AbortFailoverAsync(CancellationToken cancellationToken = default)
        => MutationAsync("FAILOVER", ["ABORT"], cancellationToken);

    /// <summary>Configures replication from a TCP primary. Requires AllowAdmin. Redis: REPLICAOF host port.</summary>
    public ValueTask ReplicaOfAsync(RespireEndpoint primary, CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(primary, allowUnixSocket: false);
        return MutationAsync("REPLICAOF", [primary.Host, primary.Port], cancellationToken);
    }

    /// <summary>Promotes this node to a primary. Requires AllowAdmin. Redis: REPLICAOF NO ONE.</summary>
    public ValueTask PromoteToPrimaryAsync(CancellationToken cancellationToken = default)
        => MutationAsync("REPLICAOF", ["NO", "ONE"], cancellationToken);

    /// <summary>Swaps two database contents. Requires AllowAdmin. Redis: SWAPDB.</summary>
    public ValueTask SwapDatabasesAsync(int first, int second, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfNegative(second);
        return MutationAsync("SWAPDB", [first, second], cancellationToken);
    }

    /// <summary>Loads a module from a server-side path. Requires AllowAdmin. Redis: MODULE LOAD.</summary>
    public ValueTask ModuleLoadAsync(string path, ReadOnlySpan<RespireValue> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return MutationAsync("MODULE LOAD", Prepend(path, arguments), cancellationToken);
    }

    /// <summary>Loads a module with CONFIG pairs and ARGS. Requires AllowAdmin and Redis 7.0 or later. Redis: MODULE LOADEX.</summary>
    public ValueTask ModuleLoadExtendedAsync(string path, ReadOnlySpan<KeyValuePair<string, RespireValue>> configuration,
        ReadOnlySpan<RespireValue> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var tokens = new List<RespireValue>(1 + configuration.Length * 3 + arguments.Length + 1) { path };
        foreach (var pair in configuration)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key, nameof(configuration));
            tokens.Add("CONFIG"); tokens.Add(pair.Key); tokens.Add(Snapshot(pair.Value, nameof(configuration)));
        }
        if (!arguments.IsEmpty) { tokens.Add("ARGS"); foreach (var argument in arguments) tokens.Add(Snapshot(argument, nameof(arguments))); }
        return MutationAsync("MODULE LOADEX", tokens.ToArray(), cancellationToken);
    }

    /// <summary>Unloads a named module. Requires AllowAdmin. Redis: MODULE UNLOAD.</summary>
    public ValueTask ModuleUnloadAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return MutationAsync("MODULE UNLOAD", [name], cancellationToken);
    }

    /// <summary>Returns owned allocator diagnostic text. Redis: MEMORY MALLOC-STATS.</summary>
    public ValueTask<string> MemoryMallocStatsAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("MEMORY MALLOC-STATS", [], ServerDiagnosticsParser.Text, cancellationToken);

    /// <summary>Returns an owned ASCII latency graph for an existing event. Redis: LATENCY GRAPH.</summary>
    public ValueTask<string> LatencyGraphAsync(string eventName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        return ExecuteAsync("LATENCY GRAPH", [eventName], ServerDiagnosticsParser.Text, cancellationToken);
    }

    /// <summary>Extracts owned physical binary keys and flags from a command invocation. Redis 7.0 or later.</summary>
    public ValueTask<RespireCommandKeyFlags[]> CommandGetKeysAndFlagsAsync(RespireCommand command,
        ReadOnlySpan<RespireValue> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name, nameof(command));
        var words = command.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokens = new RespireValue[words.Length + arguments.Length];
        for (var index = 0; index < words.Length; index++) tokens[index] = words[index];
        for (var index = 0; index < arguments.Length; index++) tokens[words.Length + index] = Snapshot(arguments[index], nameof(arguments));
        return ExecuteAsync("COMMAND GETKEYSANDFLAGS", tokens, ServerNodeParser.KeysAndFlags, cancellationToken);
    }

    /// <summary>Kills a read-only BUSY script on an independent control connection. Requires AllowAdmin. Redis: SCRIPT KILL.</summary>
    /// <remarks>Redis rejects killing a script that has written data. Server errors, including NOTBUSY and UNKILLABLE, propagate.</remarks>
    public ValueTask ScriptKillAsync(CancellationToken cancellationToken = default)
        => MutationAsync("SCRIPT KILL", [], cancellationToken, NodeCallKind.ControlMutation);

    /// <summary>Kills a read-only BUSY function on an independent control connection. Requires AllowAdmin and Redis 7.0 or later.</summary>
    public ValueTask FunctionKillAsync(CancellationToken cancellationToken = default)
        => MutationAsync("FUNCTION KILL", [], cancellationToken, NodeCallKind.ControlMutation);

    /// <summary>Starts an incremental backup. Requires AllowAdmin and Redis 8.10 or later. Redis: BACKUP START.</summary>
    public ValueTask BackupStartAsync(CancellationToken cancellationToken = default) => MutationAsync("BACKUP START", [], cancellationToken);
    /// <summary>Seals an incremental backup. Requires AllowAdmin and Redis 8.10 or later. Redis: BACKUP SEAL.</summary>
    public ValueTask BackupSealAsync(CancellationToken cancellationToken = default) => MutationAsync("BACKUP SEAL", [], cancellationToken);
    /// <summary>Aborts an incremental backup. Requires AllowAdmin and Redis 8.10 or later. Redis: BACKUP ABORT.</summary>
    public ValueTask BackupAbortAsync(CancellationToken cancellationToken = default) => MutationAsync("BACKUP ABORT", [], cancellationToken);
    /// <summary>Deletes backup artifacts. Requires AllowAdmin and Redis 8.10 or later. Redis: BACKUP CLEANUP.</summary>
    public ValueTask BackupCleanupAsync(CancellationToken cancellationToken = default) => MutationAsync("BACKUP CLEANUP", [], cancellationToken);
    /// <summary>Returns owned backup state. Redis 8.10 or later. Redis: BACKUP STATUS.</summary>
    public ValueTask<RespireBackupStatus> BackupStatusAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("BACKUP STATUS", [], ServerNodeParser.BackupStatus, cancellationToken);
    /// <summary>Lists owned immutable backup file paths. Redis 8.10 or later. Redis: BACKUP LIST.</summary>
    public ValueTask<string[]> BackupListAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync("BACKUP LIST", [], AclParser.Strings, cancellationToken);

    /// <summary>Returns physical binary keys matching a pattern. Redis: KEYS.</summary>
    /// <remarks>Debugging only: KEYS scans the entire database and blocks the server. Use IKeyCommands.ScanAsync for production iteration.</remarks>
    public ValueTask<byte[][]> KeysAsync(RespireValue pattern, CancellationToken cancellationToken = default)
        => ExecuteAsync("KEYS", [Snapshot(pattern, nameof(pattern))], AclParser.ByteStrings, cancellationToken);

    /// <summary>Migrates physical source keys to a TCP destination. Requires AllowAdmin. Redis: MIGRATE.</summary>
    /// <remarks>Keys are snapshotted before I/O. Timeout is the positive server-side maximum idle transfer time,
    /// not an overall transfer deadline. The client's CommandTimeout and caller cancellation apply independently;
    /// configure them for the entire expected transfer duration. RespireMigrateOptions.CommandTimeout can override
    /// the client response budget for this call without changing the shared client's timeout.
    /// COPY/REPLACE and destination authentication are optional. Errors, cancellation, and disconnects can leave keys at either server;
    /// this method never redirects or replays. Reconcile both servers before retrying an ambiguous transfer.</remarks>
    public ValueTask<RespireMigrateResult> MigrateAsync(RespireEndpoint destination, ReadOnlySpan<RespireKey> keys,
        int database, TimeSpan timeout, RespireMigrateOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateEndpoint(destination, allowUnixSocket: false);
        ArgumentOutOfRangeException.ThrowIfNegative(database);
        if (keys.IsEmpty) throw new ArgumentException("At least one key is required.", nameof(keys));
        options ??= new();
        if (options.CommandTimeout is { } commandTimeout && commandTimeout < TimeSpan.FromMilliseconds(1))
            throw new ArgumentOutOfRangeException(nameof(options), "MIGRATE CommandTimeout must be at least one millisecond.");
        if (options.Username is not null && options.Password is null)
            throw new ArgumentException("Destination username requires a password.", nameof(options));
        var tokens = new List<RespireValue>(keys.Length + 12)
            { destination.Host, destination.Port, "", database, Milliseconds(timeout, nameof(timeout)) };
        if (options.Copy) tokens.Add("COPY");
        if (options.Replace) tokens.Add("REPLACE");
        if (options.Password is { } password)
        {
            tokens.Add(options.Username is null ? "AUTH" : "AUTH2");
            if (options.Username is { } username) tokens.Add(username);
            tokens.Add(password);
        }
        tokens.Add("KEYS");
        foreach (var key in keys) tokens.Add(key.AsValue().Snapshot());
        return ExecuteAsync("MIGRATE", tokens.ToArray(), ServerNodeParser.Migration, cancellationToken, NodeCallKind.Mutation,
            commandTimeout: options.CommandTimeout);
    }

    private delegate T ReplyParser<T>(in RespValue reply);

    private enum NodeCallKind { Read, Mutation, ControlMutation }

    private ValueTask<T> ExecuteAsync<T>(string operation, RespireValue[] arguments, ReplyParser<T> parser,
        CancellationToken cancellationToken, NodeCallKind callKind = NodeCallKind.Read,
        TimeSpan? commandTimeout = null)
        => WithNodeConnectionAsync(operation, callKind,
            (Client: _client, Operation: operation, Arguments: arguments, Parser: parser, ReadOnly: callKind == NodeCallKind.Read),
            static async (connection, state, token, fence) =>
            {
                var command = ReadOnlyCommand<CmdN>.ForNodeRead(new CmdN(new Verb(-1, state.Operation), state.Arguments), state.ReadOnly);
                using var reply = await state.Client.SendOnPinnedConnectionAsync(state.Operation, connection,
                    new MutationCommand<ReadOnlyCommand<CmdN>>(command, fence), token).ConfigureAwait(false);
                return state.Parser(in reply);
            }, cancellationToken, commandTimeout);

    private async ValueTask<T> WithNodeConnectionAsync<TState, T>(string operation, NodeCallKind callKind,
        TState state, Func<RespireConnection, TState, CancellationToken, ClientSideCacheCoordinator.MutationFence, ValueTask<T>> execute,
        CancellationToken cancellationToken, TimeSpan? commandTimeout = null)
    {
        var mutation = callKind is NodeCallKind.Mutation or NodeCallKind.ControlMutation;
        if (mutation) ServerCommands.EnsureAdminAllowed(_client, operation);
        ObjectDisposedException.ThrowIf(_client.Core.Disposed, _client);
        cancellationToken.ThrowIfCancellationRequested();
        var cache = mutation ? _client.Core.ClientCache : null;
        var fence = cache is null ? default : cache.BeginUnknownMutation();
        DedicatedConnectionPool? pool = null;
        try
        {
            pool = _client.Core.CreateServerPool(Endpoint,
                controlConnection: callKind == NodeCallKind.ControlMutation, commandTimeout: commandTimeout);
            var connection = await pool.RentAsync(cancellationToken).ConfigureAwait(false);
            return await execute(connection, state, cancellationToken, fence).ConfigureAwait(false);
        }
        finally
        {
            // Explicit-node commands retain conservative completion work. Disposing this
            // operation's pool drains its native owners before the outer fence can leave.
            try { if (pool is not null) await _client.Core.ReleaseServerPoolAsync(pool).ConfigureAwait(false); }
            finally { if (fence.IsRequired) cache!.CompleteMutation(in fence); }
        }
    }

    private async ValueTask MutationAsync(string operation, RespireValue[] arguments, CancellationToken cancellationToken,
        NodeCallKind callKind = NodeCallKind.Mutation)
        => _ = await ExecuteAsync(operation, arguments, ServerNodeParser.Ok, cancellationToken, callKind).ConfigureAwait(false);

    private async ValueTask ShutdownWriteAsync(RespireValue[] arguments, CancellationToken cancellationToken)
        => _ = await WithNodeConnectionAsync("SHUTDOWN", NodeCallKind.ControlMutation, arguments,
            static async (connection, tokens, token, fence) =>
            {
                await connection.SendFireAndForgetAsync(new MutationCommand<CmdN>(new CmdN(new Verb(-1, "SHUTDOWN"), tokens), fence),
                    token, "SHUTDOWN").ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);

    private static RespireValue Snapshot(RespireValue value, string name)
    {
        RespireValue.ThrowIfNull(value, name);
        return value.Snapshot();
    }

    private static RespireValue[] Prepend(string first, ReadOnlySpan<RespireValue> rest)
    {
        var result = new RespireValue[rest.Length + 1];
        result[0] = first;
        for (var index = 0; index < rest.Length; index++) result[index + 1] = Snapshot(rest[index], nameof(rest));
        return result;
    }

    private static long Milliseconds(TimeSpan timeout, string name)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(name, "Timeout must be positive.");
        // Round up so a sub-millisecond positive timeout never means Redis' unbounded zero.
        return timeout.Ticks / TimeSpan.TicksPerMillisecond + (timeout.Ticks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1);
    }

    private static void ValidateEndpoint(RespireEndpoint endpoint, bool allowUnixSocket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Host, nameof(endpoint));
        if (allowUnixSocket && endpoint.Port == 0 && RespireEndpoint.IsValidUnixPath(endpoint.Host)) return;
        if (endpoint.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(endpoint), "A TCP endpoint requires port 1 through 65535.");
    }
}
