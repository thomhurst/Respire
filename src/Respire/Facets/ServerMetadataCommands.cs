using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

public partial interface IServerCommands
{
    /// <summary>Returns owned COMMAND INFO entries in request order, including null for unknown commands.</summary>
    /// <remarks>Redis 2.8.13+. Empty requests mean all commands on Redis 7+.
    /// Descriptor subcommands are encoded as one pipe-separated lookup name. Results do not change routing.</remarks>
    ValueTask<RespireCommandInfo?[]> CommandInfoAsync(ReadOnlySpan<RespireCommand> commands = default, CancellationToken cancellationToken = default);
    /// <summary>Returns owned COMMAND DOCS entries; unknown commands are omitted. Empty requests mean all commands. Redis 7+.</summary>
    ValueTask<RespireCommandDocumentation[]> CommandDocsAsync(ReadOnlySpan<RespireCommand> commands = default, CancellationToken cancellationToken = default);
    /// <summary>Asks one node to extract owned key bytes from a complete command, without executing it. Redis 2.8.13+.</summary>
    /// <remarks>Descriptor words and binary argument tokens retain their boundaries. No client prefix is applied.
    /// Keyless, malformed, and unsupported commands preserve the server error; no local key layout is substituted.</remarks>
    ValueTask<byte[][]> CommandGetKeysAsync(RespireCommand command, ReadOnlySpan<RespireValue> arguments, CancellationToken cancellationToken = default);
    /// <summary>Lists owned module metadata from one node. Redis 4+.</summary>
    ValueTask<RespireModuleInfo[]> ModuleListAsync(CancellationToken cancellationToken = default);
    /// <summary>Rewrites one node's configuration file. Requires AllowAdmin. Redis 2.8+.</summary>
    ValueTask ConfigRewriteAsync(CancellationToken cancellationToken = default);
    /// <summary>Resets one node's server statistics. Requires AllowAdmin. Redis 2+.</summary>
    ValueTask ConfigResetStatisticsAsync(CancellationToken cancellationToken = default);
    /// <summary>Synchronously saves one node's RDB snapshot, blocking that server. Requires AllowAdmin. Redis 1+.</summary>
    /// <remarks>Cancellation or timeout does not undo or stop a server operation already sent.</remarks>
    ValueTask SaveAsync(CancellationToken cancellationToken = default);
    /// <summary>Starts or schedules one node's background RDB save. Requires AllowAdmin. Redis 1+; SCHEDULE requires 3.2.2+.</summary>
    /// <remarks>The reply acknowledges acceptance, not completion. Check INFO persistence for the eventual outcome.</remarks>
    ValueTask<RespireBackgroundPersistenceResult> BackgroundSaveAsync(bool schedule = false, CancellationToken cancellationToken = default);
    /// <summary>Starts or schedules one node's background AOF rewrite. Requires AllowAdmin. Redis 1+.</summary>
    /// <remarks>The reply acknowledges acceptance, not completion. Check INFO persistence for the eventual outcome.</remarks>
    ValueTask<RespireBackgroundPersistenceResult> BackgroundRewriteAofAsync(CancellationToken cancellationToken = default);

    /// <summary>Queries each discovered node, including replicas, returning endpoint-associated metadata or errors.</summary>
    /// <remarks>All metadata/persistence OnAllNodes methods return one result for standalone clients.
    /// Discovery cancellation throws; after discovery, cancellation and other failures are attributed to each node.
    /// Inspect every result. Mutations can partly succeed and are never rolled back.</remarks>
    ValueTask<RespireServerResult<RespireCommandInfo?[]>[]> CommandInfoOnAllNodesAsync(ReadOnlySpan<RespireCommand> commands = default, CancellationToken cancellationToken = default);
    /// <summary>Queries COMMAND DOCS separately on every discovered node.</summary>
    ValueTask<RespireServerResult<RespireCommandDocumentation[]>[]> CommandDocsOnAllNodesAsync(ReadOnlySpan<RespireCommand> commands = default, CancellationToken cancellationToken = default);
    /// <summary>Extracts keys separately on every discovered node, preserving differences in command support.</summary>
    ValueTask<RespireServerResult<byte[][]>[]> CommandGetKeysOnAllNodesAsync(RespireCommand command, ReadOnlySpan<RespireValue> arguments, CancellationToken cancellationToken = default);
    /// <summary>Lists modules separately on every discovered node.</summary>
    ValueTask<RespireServerResult<RespireModuleInfo[]>[]> ModuleListOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Explicitly rewrites every discovered node's local configuration. Requires AllowAdmin; true means OK.</summary>
    ValueTask<RespireServerResult<bool>[]> ConfigRewriteOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Explicitly resets every discovered node's local statistics. Requires AllowAdmin; true means OK.</summary>
    ValueTask<RespireServerResult<bool>[]> ConfigResetStatisticsOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Explicitly performs blocking SAVE on every discovered node. Requires AllowAdmin; true means OK.</summary>
    ValueTask<RespireServerResult<bool>[]> SaveOnAllNodesAsync(CancellationToken cancellationToken = default);
    /// <summary>Explicitly requests background RDB saves on every discovered node. Requires AllowAdmin.</summary>
    ValueTask<RespireServerResult<RespireBackgroundPersistenceResult>[]> BackgroundSaveOnAllNodesAsync(bool schedule = false, CancellationToken cancellationToken = default);
    /// <summary>Explicitly requests background AOF rewrites on every discovered node. Requires AllowAdmin.</summary>
    ValueTask<RespireServerResult<RespireBackgroundPersistenceResult>[]> BackgroundRewriteAofOnAllNodesAsync(CancellationToken cancellationToken = default);
}

internal sealed partial class ServerCommands
{
    // Every call must explicitly choose its admin policy; new mutations cannot inherit a permissive default.
    private readonly record struct MetadataCall<T>(string Operation, CmdN Command, ResponseConverter<ServerCommands, T> Convert, bool RequiresAdmin);
    private static readonly Verb MetadataInfoVerb = new(-1, "COMMAND", "INFO");
    private static readonly Verb MetadataDocsVerb = new(-1, "COMMAND", "DOCS");
    private static readonly Verb MetadataKeysVerb = new(-1, "COMMAND", "GETKEYS");
    private static readonly Verb MetadataBackgroundSaveVerb = new(-1, "BGSAVE");
    private static readonly MetadataCall<RespireModuleInfo[]> ModulesCall = new("MODULE LIST", new(new Verb(-1, "MODULE", "LIST"), []), static (ServerCommands _, in RespValue value) => CommandMetadataParser.Modules(in value), RequiresAdmin: false);
    private static readonly MetadataCall<bool> RewriteConfigurationCall = MetadataOkCall("CONFIG REWRITE", new(-1, "CONFIG", "REWRITE"));
    private static readonly MetadataCall<bool> ResetStatisticsCall = MetadataOkCall("CONFIG RESETSTAT", new(-1, "CONFIG", "RESETSTAT"));
    private static readonly MetadataCall<bool> SaveSnapshotCall = MetadataOkCall("SAVE", new(-1, "SAVE"));
    private static readonly MetadataCall<RespireBackgroundPersistenceResult> RewriteAofCall = new("BGREWRITEAOF", new(new Verb(-1, "BGREWRITEAOF"), []), static (ServerCommands _, in RespValue value) => CommandMetadataParser.Background(in value), RequiresAdmin: true);

    public ValueTask<RespireCommandInfo?[]> CommandInfoAsync(ReadOnlySpan<RespireCommand> commands = default, CancellationToken cancellationToken = default) => ExecuteMetadataAsync(CommandInfoCall(commands), cancellationToken);
    public ValueTask<RespireCommandDocumentation[]> CommandDocsAsync(ReadOnlySpan<RespireCommand> commands = default, CancellationToken cancellationToken = default) => ExecuteMetadataAsync(CommandDocsCall(commands), cancellationToken);
    public ValueTask<byte[][]> CommandGetKeysAsync(RespireCommand command, ReadOnlySpan<RespireValue> arguments, CancellationToken cancellationToken = default) => ExecuteMetadataAsync(CommandKeysCall(command, arguments), cancellationToken);
    public ValueTask<RespireModuleInfo[]> ModuleListAsync(CancellationToken cancellationToken = default) => ExecuteMetadataAsync(ModulesCall, cancellationToken);
    public ValueTask ConfigRewriteAsync(CancellationToken cancellationToken = default) => DiscardMetadataOkAsync(ExecuteMetadataAsync(RewriteConfigurationCall, cancellationToken));
    public ValueTask ConfigResetStatisticsAsync(CancellationToken cancellationToken = default) => DiscardMetadataOkAsync(ExecuteMetadataAsync(ResetStatisticsCall, cancellationToken));
    public ValueTask SaveAsync(CancellationToken cancellationToken = default) => DiscardMetadataOkAsync(ExecuteMetadataAsync(SaveSnapshotCall, cancellationToken));
    public ValueTask<RespireBackgroundPersistenceResult> BackgroundSaveAsync(bool schedule = false, CancellationToken cancellationToken = default) => ExecuteMetadataAsync(SaveBackgroundCall(schedule), cancellationToken);
    public ValueTask<RespireBackgroundPersistenceResult> BackgroundRewriteAofAsync(CancellationToken cancellationToken = default) => ExecuteMetadataAsync(RewriteAofCall, cancellationToken);

    public ValueTask<RespireServerResult<RespireCommandInfo?[]>[]> CommandInfoOnAllNodesAsync(ReadOnlySpan<RespireCommand> commands = default, CancellationToken cancellationToken = default) => ExecuteMetadataOnAllNodesAsync(CommandInfoCall(commands), cancellationToken);
    public ValueTask<RespireServerResult<RespireCommandDocumentation[]>[]> CommandDocsOnAllNodesAsync(ReadOnlySpan<RespireCommand> commands = default, CancellationToken cancellationToken = default) => ExecuteMetadataOnAllNodesAsync(CommandDocsCall(commands), cancellationToken);
    public ValueTask<RespireServerResult<byte[][]>[]> CommandGetKeysOnAllNodesAsync(RespireCommand command, ReadOnlySpan<RespireValue> arguments, CancellationToken cancellationToken = default) => ExecuteMetadataOnAllNodesAsync(CommandKeysCall(command, arguments), cancellationToken);
    public ValueTask<RespireServerResult<RespireModuleInfo[]>[]> ModuleListOnAllNodesAsync(CancellationToken cancellationToken = default) => ExecuteMetadataOnAllNodesAsync(ModulesCall, cancellationToken);
    public ValueTask<RespireServerResult<bool>[]> ConfigRewriteOnAllNodesAsync(CancellationToken cancellationToken = default) => ExecuteMetadataOnAllNodesAsync(RewriteConfigurationCall, cancellationToken);
    public ValueTask<RespireServerResult<bool>[]> ConfigResetStatisticsOnAllNodesAsync(CancellationToken cancellationToken = default) => ExecuteMetadataOnAllNodesAsync(ResetStatisticsCall, cancellationToken);
    public ValueTask<RespireServerResult<bool>[]> SaveOnAllNodesAsync(CancellationToken cancellationToken = default) => ExecuteMetadataOnAllNodesAsync(SaveSnapshotCall, cancellationToken);
    public ValueTask<RespireServerResult<RespireBackgroundPersistenceResult>[]> BackgroundSaveOnAllNodesAsync(bool schedule = false, CancellationToken cancellationToken = default) => ExecuteMetadataOnAllNodesAsync(SaveBackgroundCall(schedule), cancellationToken);
    public ValueTask<RespireServerResult<RespireBackgroundPersistenceResult>[]> BackgroundRewriteAofOnAllNodesAsync(CancellationToken cancellationToken = default) => ExecuteMetadataOnAllNodesAsync(RewriteAofCall, cancellationToken);

    private ValueTask<T> ExecuteMetadataAsync<T>(MetadataCall<T> call, CancellationToken cancellationToken)
    {
        if (call.RequiresAdmin) EnsureAdminAllowed(call.Operation);
        cancellationToken.ThrowIfCancellationRequested();
        return ConvertAsync(call.Operation, call.Command, cancellationToken, call.Convert);
    }

    private async ValueTask<RespireServerResult<T>[]> ExecuteMetadataOnAllNodesAsync<T>(MetadataCall<T> call, CancellationToken cancellationToken)
    {
        if (call.RequiresAdmin) EnsureAdminAllowed(call.Operation);
        cancellationToken.ThrowIfCancellationRequested();
        var cache = client.Core.ClientCache;
        // Use the same operation classifier as single-node and raw execution. Read-only calls
        // produce a no-op fence; RequiresAdmin controls authorization, not cache semantics.
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

    private static async ValueTask DiscardMetadataOkAsync(ValueTask<bool> operation) => _ = await operation.ConfigureAwait(false);

    private static MetadataCall<bool> MetadataOkCall(string operation, Verb verb)
        => new(operation, new(verb, []), static (ServerCommands _, in RespValue value) => CommandMetadataParser.Ok(in value), RequiresAdmin: true);

    private static MetadataCall<RespireBackgroundPersistenceResult> SaveBackgroundCall(bool schedule)
        => new("BGSAVE", new(MetadataBackgroundSaveVerb, schedule ? ["SCHEDULE"] : []), static (ServerCommands _, in RespValue value) => CommandMetadataParser.Background(in value), RequiresAdmin: true);

    private static MetadataCall<RespireCommandInfo?[]> CommandInfoCall(ReadOnlySpan<RespireCommand> commands)
        => new("COMMAND INFO", new(MetadataInfoVerb, MetadataNames(commands)), static (ServerCommands _, in RespValue value) => CommandMetadataParser.Info(in value), RequiresAdmin: false);

    private static MetadataCall<RespireCommandDocumentation[]> CommandDocsCall(ReadOnlySpan<RespireCommand> commands)
        => new("COMMAND DOCS", new(MetadataDocsVerb, MetadataNames(commands)), static (ServerCommands _, in RespValue value) => CommandMetadataParser.Docs(in value), RequiresAdmin: false);

    private static RespireValue[] MetadataNames(ReadOnlySpan<RespireCommand> commands)
    {
        var names = new RespireValue[commands.Length];
        for (var index = 0; index < commands.Length; index++)
            names[index] = string.Join('|', MetadataCommandWords(commands[index]));
        return names;
    }

    private static string[] MetadataCommandWords(RespireCommand command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name, nameof(command));
        return command.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static MetadataCall<byte[][]> CommandKeysCall(RespireCommand command, ReadOnlySpan<RespireValue> arguments)
    {
        var words = MetadataCommandWords(command);
        var tokens = new RespireValue[words.Length + arguments.Length];
        for (var index = 0; index < words.Length; index++) tokens[index] = words[index];
        for (var index = 0; index < arguments.Length; index++)
        {
            RespireValue.ThrowIfNull(arguments[index], nameof(arguments));
            tokens[words.Length + index] = arguments[index].Snapshot();
        }
        return new("COMMAND GETKEYS", new(MetadataKeysVerb, tokens), static (ServerCommands _, in RespValue value) => CommandMetadataParser.Keys(in value), RequiresAdmin: false);
    }
}
