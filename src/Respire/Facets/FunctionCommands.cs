using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>Redis Functions (Redis 7+). Immediate mutations reach all discovered Cluster primaries.</summary>
/// <remarks>LIST, DUMP and STATS inspect one routing node in Cluster. Use a client connected directly
/// to each primary when inspecting or backing up divergent server state. Fan-out mutations are not atomic.</remarks>
public interface IFunctionCommands
{
    /// <summary>Calls a function with prefixed keys. Dispose the leased result. A reusable library permits one missing-function reload/retry.</summary>
    ValueTask<RespireResult> ExecuteAsync(RespireFunction function, RespireKey[]? keys = null,
        RespireValue[]? args = null, CancellationToken cancellationToken = default)
        => ExecuteSpanAsync(function, keys.AsSpan(), args.AsSpan(), cancellationToken);
    /// <summary>Consumes key/argument spans before returning. Underlying byte memory must remain unchanged until completion.</summary>
    ValueTask<RespireResult> ExecuteSpanAsync(RespireFunction function, ReadOnlySpan<RespireKey> keys,
        ReadOnlySpan<RespireValue> args, CancellationToken cancellationToken = default);
    /// <summary>Calls a function and deserializes its scalar result using the client serializer.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    async ValueTask<T?> ExecuteAsync<T>(RespireFunction function, RespireKey[]? keys = null,
        RespireValue[]? args = null, CancellationToken cancellationToken = default)
    {
        using var result = await ExecuteAsync(function, keys, args, cancellationToken).ConfigureAwait(false);
        return result.As<T>();
    }
    /// <summary>Calls a function and reads its integer result.</summary>
    async ValueTask<long> ExecuteIntegerAsync(RespireFunction function, RespireKey[]? keys = null,
        RespireValue[]? args = null, CancellationToken cancellationToken = default)
    {
        using var result = await ExecuteAsync(function, keys, args, cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }
    /// <summary>Calls a function and reads its string result, or null.</summary>
    async ValueTask<string?> ExecuteStringAsync(RespireFunction function, RespireKey[]? keys = null,
        RespireValue[]? args = null, CancellationToken cancellationToken = default)
    {
        using var result = await ExecuteAsync(function, keys, args, cancellationToken).ConfigureAwait(false);
        return result.IsNull ? null : result.AsString();
    }
    /// <summary>Loads source on the server or all discovered Cluster primaries; returns the library name. Redis: FUNCTION LOAD.</summary>
    ValueTask<string> LoadAsync(string source, bool replace = false, CancellationToken cancellationToken = default);
    /// <summary>Loads a reusable library with its replacement policy.</summary>
    ValueTask<string> LoadAsync(RespireFunctionLibrary library, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        return LoadAsync(library.Source, library.Replace, cancellationToken);
    }
    /// <summary>Owned metadata from one server, optionally including source. Pattern uses Redis glob syntax. Redis: FUNCTION LIST.</summary>
    ValueTask<RespireFunctionLibraryInfo[]> ListAsync(string? libraryPattern = null, bool withCode = false, CancellationToken cancellationToken = default);
    /// <summary>Deletes a library on the server or all discovered Cluster primaries; true on OK. Redis: FUNCTION DELETE.</summary>
    ValueTask<bool> DeleteAsync(string libraryName, CancellationToken cancellationToken = default);
    /// <summary>Clears libraries on the server or all discovered Cluster primaries; true on OK. Redis: FUNCTION FLUSH.</summary>
    ValueTask<bool> FlushAsync(FunctionFlushMode mode = FunctionFlushMode.Default, CancellationToken cancellationToken = default);
    /// <summary>Returns an owned binary library backup from one server. Redis: FUNCTION DUMP.</summary>
    ValueTask<byte[]> DumpAsync(CancellationToken cancellationToken = default);
    /// <summary>Restores libraries on the server or all discovered Cluster primaries; true on OK. Keep payload bytes unchanged until completion.</summary>
    ValueTask<bool> RestoreAsync(ReadOnlyMemory<byte> payload, FunctionRestorePolicy policy = FunctionRestorePolicy.Append, CancellationToken cancellationToken = default);
    /// <summary>Returns owned execution/engine statistics from one server. Redis: FUNCTION STATS.</summary>
    ValueTask<RespireFunctionStats> StatsAsync(CancellationToken cancellationToken = default);
}

internal sealed class FunctionCommands(RespireClient client) : IFunctionCommands
{
    public ValueTask<RespireResult> ExecuteSpanAsync(RespireFunction function, ReadOnlySpan<RespireKey> keys,
        ReadOnlySpan<RespireValue> args, CancellationToken cancellationToken = default)
    {
        var command = CallCommand(client, function, keys, args);
        return ExecuteCoreAsync(function, command, cancellationToken);
    }

    private async ValueTask<RespireResult> ExecuteCoreAsync(RespireFunction function, BatchScriptCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await client.SendAsync(function.Operation, command, cancellationToken).ConfigureAwait(false);
            return client.CreateResult(in reply);
        }
        catch (RespireServerException error) when (function.Library is not null && error.Message == "ERR Function not found")
        {
            var library = function.Library;
            var gate = library.ReloadGate(client.Core);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureLibraryAsync(library, cancellationToken).ConfigureAwait(false);
            }
            finally { gate.Release(); }
            // Redis reserves this reply for a missing function. A second failure escapes;
            // timeouts, connection failures and arbitrary function errors never trigger retries.
            var reply = await client.SendAsync(function.Operation, command, cancellationToken).ConfigureAwait(false);
            return client.CreateResult(in reply);
        }
    }

    internal static BatchScriptCommand CallCommand(RespireClient client, RespireFunction function,
        ReadOnlySpan<RespireKey> keys, ReadOnlySpan<RespireValue> args)
    {
        ArgumentNullException.ThrowIfNull(function);
        var tail = client.BuildScriptTailFromSpans(keys, args);
        if (client.Core.Cluster is not null && keys.Length > 1)
        {
            tail[1].TryGetClusterSlot(out var expected);
            for (var i = 2; i <= keys.Length; i++)
                if (tail[i].TryGetClusterSlot(out var slot) && slot != expected)
                    throw new RespireServerException("CROSSSLOT Keys in request don't hash to the same slot", function.Operation);
        }
        return new(function.Verb, function.Name, tail, keys.Length);
    }

    public ValueTask<string> LoadAsync(string source, bool replace = false, CancellationToken cancellationToken = default)
        => MutateAsync("FUNCTION LOAD", LoadCommand(source, replace),
            static (FunctionCommands _, in RespValue value) => ResponseReader.String(in value), cancellationToken);
    public ValueTask<RespireFunctionLibraryInfo[]> ListAsync(string? libraryPattern = null, bool withCode = false, CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync("FUNCTION LIST", ListCommand(libraryPattern, withCode), cancellationToken, this,
            static (FunctionCommands _, in RespValue value) => FunctionResponseReader.Libraries(in value));
    public ValueTask<bool> DeleteAsync(string libraryName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryName);
        return MutateAsync("FUNCTION DELETE", new Cmd1(FunctionVerbs.Delete, libraryName),
            static (FunctionCommands _, in RespValue value) => ResponseReader.Ok(in value), cancellationToken);
    }
    public ValueTask<bool> FlushAsync(FunctionFlushMode mode = FunctionFlushMode.Default, CancellationToken cancellationToken = default)
        => MutateAsync("FUNCTION FLUSH", FlushCommand(mode),
            static (FunctionCommands _, in RespValue value) => ResponseReader.Ok(in value), cancellationToken);
    public ValueTask<byte[]> DumpAsync(CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync("FUNCTION DUMP", new Cmd(FunctionVerbs.Dump), cancellationToken, this,
            static (FunctionCommands _, in RespValue value) => value.AsSpan().ToArray());
    public ValueTask<bool> RestoreAsync(ReadOnlyMemory<byte> payload, FunctionRestorePolicy policy = FunctionRestorePolicy.Append, CancellationToken cancellationToken = default)
        => MutateAsync("FUNCTION RESTORE", RestoreCommand(payload, policy),
            static (FunctionCommands _, in RespValue value) => ResponseReader.Ok(in value), cancellationToken);
    public ValueTask<RespireFunctionStats> StatsAsync(CancellationToken cancellationToken = default)
        => client.ConvertResponseAsync("FUNCTION STATS", new Cmd(FunctionVerbs.Stats), cancellationToken, this,
            static (FunctionCommands _, in RespValue value) => FunctionResponseReader.Stats(in value));

    internal static CmdN LoadCommand(string source, bool replace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return new(FunctionVerbs.Load, replace ? ["REPLACE", source] : [source]);
    }
    internal static CmdN ListCommand(string? pattern, bool withCode)
    {
        if (pattern is not null) ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        RespireValue[] args = (pattern, withCode) switch
        {
            (null, false) => [], (null, true) => ["WITHCODE"],
            (_, false) => ["LIBRARYNAME", pattern!], (_, true) => ["LIBRARYNAME", pattern!, "WITHCODE"]
        };
        return new(FunctionVerbs.List, args);
    }
    internal static CmdN FlushCommand(FunctionFlushMode mode)
        => new(FunctionVerbs.Flush, mode switch
        {
            FunctionFlushMode.Default => [], FunctionFlushMode.Sync => ["SYNC"], FunctionFlushMode.Async => ["ASYNC"],
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        });
    internal static CmdN RestoreCommand(ReadOnlyMemory<byte> payload, FunctionRestorePolicy policy)
        => new(FunctionVerbs.Restore, [payload, policy switch
        {
            FunctionRestorePolicy.Append => "APPEND", FunctionRestorePolicy.Flush => "FLUSH", FunctionRestorePolicy.Replace => "REPLACE",
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        }]);

    private async ValueTask<T> MutateAsync<TCommand, T>(string operation, TCommand command,
        ResponseConverter<FunctionCommands, T> convert, CancellationToken cancellationToken) where TCommand : struct, IRespCommand
    {
        if (client.Core.Cluster is not { } cluster)
            return await client.ConvertResponseAsync(operation, command, cancellationToken, this, convert).ConfigureAwait(false);
        var connections = await cluster.GetMasterConnectionsAsync(cancellationToken).ConfigureAwait(false);
        if (connections.Length == 0) throw new RespireConnectionException($"{operation} did not reach any Redis Cluster primary.");
        var tasks = new Task<T>[connections.Length];
        for (var i = 0; i < tasks.Length; i++) tasks[i] = SendAndConvertAsync(connections[i], operation, command, convert, cancellationToken).AsTask();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        if (results.Any(result => !EqualityComparer<T>.Default.Equals(results[0], result)))
            throw new RespireProtocolException($"{operation} returned inconsistent results across primaries.");
        return results[0];
    }
    private async ValueTask<T> SendAndConvertAsync<TCommand, T>(RespireConnection connection, string operation,
        TCommand command, ResponseConverter<FunctionCommands, T> convert, CancellationToken cancellationToken) where TCommand : struct, IRespCommand
    {
        using var reply = await client.SendOnConnectionAsync(operation, connection, command, cancellationToken).ConfigureAwait(false);
        return convert(this, in reply);
    }
    private async ValueTask EnsureLibraryAsync(RespireFunctionLibrary library, CancellationToken cancellationToken)
    {
        if (client.Core.Cluster is { } cluster)
        {
            var connections = await cluster.GetMasterConnectionsAsync(cancellationToken).ConfigureAwait(false);
            if (connections.Length == 0) throw new RespireConnectionException("Library reload did not reach any Redis Cluster primary.");
            await Task.WhenAll(connections.Select(connection => EnsureOnConnectionAsync(connection, library, cancellationToken).AsTask())).ConfigureAwait(false);
        }
        else
        {
            await client.Core.Multiplexer.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            await EnsureOnConnectionAsync(client.Core.Multiplexer.GetConnection(), library, cancellationToken).ConfigureAwait(false);
        }
    }
    private async ValueTask EnsureOnConnectionAsync(RespireConnection connection, RespireFunctionLibrary library, CancellationToken cancellationToken)
    {
        // Inspect before loading: concurrent first use accepts identical source, but never silently
        // overwrites a different library unless replacement was explicitly requested.
        var libraries = await SendAndConvertAsync(connection, "FUNCTION LIST", ListCommand(null, true),
            static (FunctionCommands _, in RespValue value) => FunctionResponseReader.Libraries(in value), cancellationToken).ConfigureAwait(false);
        var existing = libraries.FirstOrDefault(item => item.Name == library.Name);
        if (existing?.Code == library.Source) return;
        _ = await SendAndConvertAsync(connection, "FUNCTION LOAD", LoadCommand(library.Source, library.Replace),
            static (FunctionCommands _, in RespValue value) => ResponseReader.String(in value), cancellationToken).ConfigureAwait(false);
    }
}

internal static class FunctionVerbs
{
    internal static readonly Verb Load = new(-1, "FUNCTION", "LOAD");
    internal static readonly Verb List = new(-1, "FUNCTION", "LIST");
    internal static readonly Verb Delete = new(-1, "FUNCTION", "DELETE");
    internal static readonly Verb Flush = new(-1, "FUNCTION", "FLUSH");
    internal static readonly Verb Dump = new(-1, "FUNCTION", "DUMP");
    internal static readonly Verb Restore = new(-1, "FUNCTION", "RESTORE");
    internal static readonly Verb Stats = new(-1, "FUNCTION", "STATS");
}
