using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    private ValueTask<TResult> ConvertResponseUnhookedAsync<TCommand, TState, TResult>(
        string operation, in TCommand command, CancellationToken ct, TState state,
        ResponseConverter<TState, TResult> converter, bool transferOwnership = false, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        if (!observation.IsEmpty) return ConvertResponseCoreAsync(operation, command, ct, state, converter, transferOwnership, observation);
        // A transferred reply would hand the caller a value the cache still owns.
        if (!transferOwnership && TryGetCachedQuery(operation, in command, out var cache, out var query)
            && cache.TryGetHit(in query, out var hit))
            return ConvertCachedHit(state, converter, in hit);
        if (TryGetNativeDispatchConnection(operation, in command, out var connection))
            return connection.SendNativeConvertedAsync(in command, ct, operation, state, converter, transferOwnership);
        return DispatchResponseSource<TResult>.Run(
            (Client: this, Operation: operation, Command: command, Token: ct, State: state,
                Converter: converter, Transfer: transferOwnership),
            static (state, observation) => state.Client.ConvertResponseCoreAsync(state.Operation,
                state.Command, state.Token, state.State, state.Converter, state.Transfer, observation));
    }

    internal ValueTask<string?> StringOrNullUnhookedAsync<TCommand>(string operation, in TCommand command, CancellationToken ct, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        if (!observation.IsEmpty) return StringOrNullCoreAsync(operation, command, ct, observation);
        if (TryGetCachedQuery(operation, in command, out var cache, out var query)
            && cache.TryGetStringHit(in query, out var hit))
            return new ValueTask<string?>(hit);
        if (TryGetNativeDispatchConnection(operation, in command, out var connection))
            return connection.SendNativeStringAsync(in command, ct, operation);
        return DispatchResponseSource<string?>.Run((Client: this, Operation: operation, Command: command, Token: ct),
            static (state, observation) => state.Client.StringOrNullCoreAsync(state.Operation, state.Command, state.Token, observation));
    }

    private ValueTask<byte[]?> BytesOrNullUnhookedAsync<TCommand>(string operation, in TCommand command, CancellationToken ct, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        if (!observation.IsEmpty) return BytesOrNullCoreAsync(operation, command, ct, observation);
        if (TryGetCachedQuery(operation, in command, out var cache, out var query)
            && cache.TryGetHit(in query, out var hit))
            return new ValueTask<byte[]?>(ResponseReader.BytesOrNull(in hit));
        if (TryGetNativeDispatchConnection(operation, in command, out var connection))
            return connection.SendNativeBytesAsync(in command, ct, operation);
        return DispatchResponseSource<byte[]?>.Run((Client: this, Operation: operation, Command: command, Token: ct),
            static (state, observation) => state.Client.BytesOrNullCoreAsync(state.Operation, state.Command, state.Token, observation));
    }

    // Hot facet commands hand an empty observation to the helpers above, so the default
    // standalone route reaches native dispatch without a DispatchResponseSource. Such a facet
    // owns only its preflight (validation, key resolution, command construction) and records
    // that failure here. Every later failure belongs to the helper: the native source, its
    // admission boundary, or DispatchResponseSource.Run on observed routes. A preflight that
    // calls one of those helpers must not wrap it, or one exception would be reported twice.
    internal static void RecordPreflightFailure(Exception error)
        => RespireTelemetry.RecordError(error, internallyHandled: false);

    internal RespireValue PreflightKey(in RespireKey key)
    {
        try { return Key(in key); }
        catch (Exception error) { RecordPreflightFailure(error); throw; }
    }

    internal RespireKey PreflightResolveKey(RespireKey key)
    {
        try { return ResolveKey(key); }
        catch (Exception error) { RecordPreflightFailure(error); throw; }
    }

    // A resident command-query hit does no I/O. Its only telemetry is the cache request
    // metric, which the hit probe records, so it needs no dispatch owner. These are
    // SendCoreAsync's lookup conditions. Sentinel generations, disposal, streaming uploads
    // and the GET/MGET/HMGET producers keep their owned route, which also counts every miss.
    private bool TryGetCachedQuery<TCommand>(string operation, in TCommand command,
        out ClientSideCacheCoordinator cache, out ClientSideCacheCoordinator.QueryRequest query)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        cache = core.ClientCache!;
        query = default;
        return cache is not null && !_bypassClientCache && core.Sentinel is null && !core.Disposed
            && (_readFrom == RespireReadFrom.Primary || command.ReadKind == ReadCommandKind.None)
            && command is not IStreamingRespCommand
            && operation is not ("GET" or "MGET" or "HMGET")
            && ClientSideCacheCoordinator.TryCreateQuery(operation, in command, out query);
    }

    // Converts a borrowed cache value. A conversion failure is the operation's final error,
    // reported once without an endpoint, as the owned route reports it for a cached reply.
    private static ValueTask<TResult> ConvertCachedHit<TState, TResult>(
        TState state, ResponseConverter<TState, TResult> converter, in RespValue hit)
    {
        try { return new ValueTask<TResult>(converter(state, in hit)); }
        catch (Exception error)
        {
            ErrorObservation.FinishFinal(default, error);
            throw;
        }
    }

    private bool TryGetNativeDispatchConnection<TCommand>(string operation, in TCommand command,
        out RespireConnection connection)
        where TCommand : struct, IRespCommand
    {
        connection = null!;
        // These routes can retry or clean up after native inspection. They retain the
        // generation-bound owner, including the logical cache mutation fence.
        var core = _core;
        RespireEndpoint? endpoint = null;
        try
        {
            if (core.CommandRetries is not null || core.Cluster is not null || core.Sentinel is not null || core.Circuits is not null
                || core.ClientCache is not null || command is IStreamingRespCommand
                || ScriptingEngineInfo.IsScriptingCommand(operation)
                || !CanUseDirectReplySource(operation, in command)
                || !core.TryGetReadyPrimaryMultiplexer(out var multiplexer)) return false;
            ObjectDisposedException.ThrowIf(core.Disposed, this);
            endpoint = multiplexer.ActiveConnectionEndpoint;
            connection = (UsesStandaloneBacklog ? SelectStandaloneConnection(default) : multiplexer.GetConnection())!;
            return connection is not null;
        }
        catch (Exception error)
        {
            RespireTelemetry.RecordError(error, internallyHandled: false, endpoint: endpoint);
            throw;
        }
    }
}
