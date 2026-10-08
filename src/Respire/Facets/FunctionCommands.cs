using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
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
    internal static readonly TimeSpan FunctionPropagationLimit = TimeSpan.FromSeconds(5);
    public ValueTask<RespireResult> ExecuteSpanAsync(RespireFunction function, ReadOnlySpan<RespireKey> keys,
        ReadOnlySpan<RespireValue> args, CancellationToken cancellationToken = default)
    {
        var command = CallCommand(client, function, keys, args);
        return ExecuteCoreAsync(function, command, cancellationToken);
    }

    private async ValueTask<RespireResult> ExecuteCoreAsync(RespireFunction function, BatchScriptCommand command, CancellationToken cancellationToken)
    {
        var reload = function.Library?.ReloadState(client.Core);
        var generation = reload is null ? 0 : Volatile.Read(ref reload.Generation);
        try
        {
            var reply = await client.SendAsync(function.Operation, command, cancellationToken).ConfigureAwait(false);
            return client.CreateResult(in reply);
        }
        catch (RespireServerException error) when (function.Library is not null && IsFunctionNotFound(error))
        {
            var library = function.Library;
            var gate = reload!.Gate;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var currentGeneration = Volatile.Read(ref reload.Generation);
                // A matching library without this function cannot be repaired by replication.
                // Preserve Redis's original not-found error instead of entering propagation polling.
                if (!await EnsureLibraryAsync(library, function.Name, cancellationToken).ConfigureAwait(false))
                    throw;
                // Revalidate this function even when another caller refreshed the library.
                if (currentGeneration == generation)
                    Interlocked.Increment(ref reload.Generation);
            }
            finally { gate.Release(); }
            // Redis reserves this reply for a missing function. A primary retry stays bounded;
            // replica reads wait only for replication of the registered library.
            var readFrom = client.GetReadFromForCommand(in command);
            var reply = readFrom == RespireReadFrom.Primary
                ? await client.PrimaryReadView.SendAsync(function.Operation, command, cancellationToken).ConfigureAwait(false)
                : await RetryUntilFunctionAvailableAsync(function.Operation, command, cancellationToken,
                    client.Core.Options.CommandTimeout, error).ConfigureAwait(false);
            return client.CreateResult(in reply);
        }
    }

    private async ValueTask<RespValue> RetryUntilFunctionAvailableAsync(
        string operation, BatchScriptCommand command, CancellationToken cancellationToken, TimeSpan? timeout,
        RespireServerException lastMissingFunction)
    {
        var propagationTimeout = timeout is { } configuredTimeout && configuredTimeout < FunctionPropagationLimit
            ? configuredTimeout : FunctionPropagationLimit;
        var started = Stopwatch.GetTimestamp();
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        admission.CancelAfter(propagationTimeout);
        var delay = TimeSpan.FromMilliseconds(25);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(started) >= propagationTimeout)
                    throw RespireTimeoutException.FunctionPropagation(operation, propagationTimeout, lastMissingFunction);
                try
                {
                    var retry = new FunctionRetryCommand(command, operation, started, propagationTimeout,
                        lastMissingFunction, cancellationToken);
                    return await client.SendAsync(operation, retry, admission.Token).ConfigureAwait(false);
                }
                catch (RespireServerException error) when (IsFunctionNotFound(error))
                {
                    lastMissingFunction = error;
                }
                var remaining = propagationTimeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) continue;
                await Task.Delay(delay < remaining ? delay : remaining, admission.Token).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 250));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException error) when (error.CancellationToken == admission.Token
            && admission.IsCancellationRequested)
        {
            throw RespireTimeoutException.FunctionPropagation(operation, propagationTimeout, lastMissingFunction);
        }
    }

    private readonly struct FunctionRetryCommand(BatchScriptCommand command, string operation, long started,
        TimeSpan budget, RespireServerException lastMissingFunction, CancellationToken callerToken) : IRespCommandWrapper
    {
        public int GetWriteSizeHint() => command.GetWriteSizeHint();
        public ReadCommandKind ReadKind => command.ReadKind;
        public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => command.GetClientCacheMetadata(operation);
        public RespireCacheMutation GetCacheMutation(string operation) => command.GetCacheMutation(operation);
        public bool TryGetClusterSlot(out int slot) => command.TryGetClusterSlot(out slot);
        public void Write(ref RespWriter writer) => command.Write(ref writer);
        public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken) => callerToken;

        // BatchScriptCommand has no acceptance side effects.
        public void OnAccepted() { }

        public void ValidateAdmission()
        {
            callerToken.ThrowIfCancellationRequested();
            // Check the clock at publication as well as using a timer for parked acquisition.
            // A delayed timer callback must never admit an expired retry.
            if (Stopwatch.GetElapsedTime(started) >= budget)
                throw RespireTimeoutException.FunctionPropagation(operation, budget, lastMissingFunction);
        }
    }

    // Do not broaden this to a substring match: user function errors can contain these words
    // after performing writes, and retrying such a function can repeat its side effects.
    private static bool IsFunctionNotFound(RespireServerException error)
        => error.Message == "ERR Function not found";

    internal static string EscapeLibraryPattern(string name)
    {
        var pattern = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            if (character is '\\' or '*' or '?' or '[' or ']') pattern.Append('\\');
            pattern.Append(character);
        }
        return pattern.ToString();
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
        var connections = await cluster.GetMasterConnectionsAsync(cancellationToken, discovery: null).ConfigureAwait(false);
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
        using var reply = await client.SendToClusterTargetAsync(operation, connection, command, cancellationToken).ConfigureAwait(false);
        return convert(this, in reply);
    }
    private async ValueTask<bool> EnsureLibraryAsync(RespireFunctionLibrary library, string functionName,
        CancellationToken cancellationToken)
    {
        if (client.Core.Cluster is { } cluster)
        {
            var connections = await cluster.GetMasterConnectionsAsync(cancellationToken, discovery: null).ConfigureAwait(false);
            if (connections.Length == 0) throw new RespireConnectionException("Library reload did not reach any Redis Cluster primary.");
            var results = await Task.WhenAll(connections.Select(connection =>
                EnsureOnConnectionAsync(connection, library, functionName, cancellationToken).AsTask())).ConfigureAwait(false);
            return results.All(static available => available);
        }
        await client.Core.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await EnsureOnConnectionAsync(client.Core.Multiplexer.GetConnection(), library, functionName,
            cancellationToken).ConfigureAwait(false);
    }
    private async ValueTask<bool> EnsureOnConnectionAsync(RespireConnection connection, RespireFunctionLibrary library,
        string functionName, CancellationToken cancellationToken)
    {
        // Inspect before loading: concurrent first use accepts identical source, but never silently
        // overwrites a different library unless replacement was explicitly requested.
        var matching = await FindMatchingLibraryAsync(connection, library, cancellationToken).ConfigureAwait(false);
        if (matching is not null)
            return matching.Functions.Any(function => function.Name == functionName);
        try
        {
            _ = await SendAndConvertAsync(connection, "FUNCTION LOAD", LoadCommand(library.Source, library.Replace),
                static (FunctionCommands _, in RespValue value) => ResponseReader.String(in value), cancellationToken).ConfigureAwait(false);
            matching = await FindMatchingLibraryAsync(connection, library, cancellationToken).ConfigureAwait(false);
            return matching?.Functions.Any(function => function.Name == functionName) == true;
        }
        catch (RespireServerException error) when (!library.Replace
            && error.Message == $"ERR Library '{library.Name}' already exists")
        {
            // Another client/process may load after our LIST. Accept only identical source;
            // never replace, retry LOAD, or hide a conflicting library's original error.
            matching = await FindMatchingLibraryAsync(connection, library, cancellationToken).ConfigureAwait(false);
            if (matching is null) throw;
            return matching.Functions.Any(function => function.Name == functionName);
        }
    }

    private async ValueTask<RespireFunctionLibraryInfo?> FindMatchingLibraryAsync(RespireConnection connection,
        RespireFunctionLibrary library, CancellationToken cancellationToken)
    {
        var libraries = await SendAndConvertAsync(connection, "FUNCTION LIST", ListCommand(EscapeLibraryPattern(library.Name), true),
            static (FunctionCommands _, in RespValue value) => FunctionResponseReader.Libraries(in value), cancellationToken).ConfigureAwait(false);
        return libraries.FirstOrDefault(item => item.Name == library.Name && item.Code == library.Source);
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
