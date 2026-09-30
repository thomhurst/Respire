using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>Deferred Redis Functions. Calls never reload or replay; administration affects only the execution node.</summary>
public interface IBatchFunctionCommands
{
    /// <summary>Queues FCALL/FCALL_RO. Results own GC storage; unread results retain no pooled buffers. Dispose invalidates nested views.</summary>
    RespirePending<RespireResult> Execute(RespireFunction function, RespireKey[]? keys = null, RespireValue[]? args = null);
    /// <summary>Queues a call from spans; collections are consumed before returning, but byte memory is borrowed until execution completes.</summary>
    RespirePending<RespireResult> ExecuteSpan(RespireFunction function, ReadOnlySpan<RespireKey> keys, ReadOnlySpan<RespireValue> args);
    /// <summary>Queues a call and deserializes its scalar reply.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    RespirePending<T?> Execute<T>(RespireFunction function, RespireKey[]? keys = null, RespireValue[]? args = null);
    /// <summary>Queues a call and reads its integer reply.</summary>
    RespirePending<long> ExecuteInteger(RespireFunction function, RespireKey[]? keys = null, RespireValue[]? args = null);
    /// <summary>Queues a call and reads its string reply, or null.</summary>
    RespirePending<string?> ExecuteString(RespireFunction function, RespireKey[]? keys = null, RespireValue[]? args = null);
    /// <summary>Queues FUNCTION LOAD on the execution node.</summary>
    RespirePending<string> Load(string source, bool replace = false);
    /// <summary>Queues FUNCTION LOAD using the library's replacement policy.</summary>
    RespirePending<string> Load(RespireFunctionLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return Load(library.Source, library.Replace);
    }
    /// <summary>Queues FUNCTION LIST; metadata owns its storage.</summary>
    RespirePending<RespireFunctionLibraryInfo[]> List(string? libraryPattern = null, bool withCode = false);
    /// <summary>Queues FUNCTION DELETE; true on OK.</summary>
    RespirePending<bool> Delete(string libraryName);
    /// <summary>Queues FUNCTION FLUSH; true on OK.</summary>
    RespirePending<bool> Flush(FunctionFlushMode mode = FunctionFlushMode.Default);
    /// <summary>Queues FUNCTION DUMP; the returned binary payload owns its storage.</summary>
    RespirePending<byte[]> Dump();
    /// <summary>Queues FUNCTION RESTORE; true on OK. Keep payload bytes unchanged until execution completes.</summary>
    RespirePending<bool> Restore(ReadOnlyMemory<byte> payload, FunctionRestorePolicy policy = FunctionRestorePolicy.Append);
    /// <summary>Queues FUNCTION STATS; metadata owns its storage.</summary>
    RespirePending<RespireFunctionStats> Stats();
}

internal sealed class BatchFunctionCommands(IPendingSink sink) : IBatchFunctionCommands
{
    public RespirePending<RespireResult> Execute(RespireFunction function, RespireKey[]? keys = null, RespireValue[]? args = null)
        => ExecuteSpan(function, keys.AsSpan(), args.AsSpan());
    public RespirePending<RespireResult> ExecuteSpan(RespireFunction function, ReadOnlySpan<RespireKey> keys, ReadOnlySpan<RespireValue> args)
    {
        var command = FunctionCommands.CallCommand(sink.Client, function, keys, args);
        return sink.Add<BatchScriptCommand, RespireResult>(function.Operation, command, keys,
            static (client, value) =>
            {
                var owned = value.ToOwned();
                return client.CreateResult(in owned);
            });
    }
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T?> Execute<T>(RespireFunction function, RespireKey[]? keys = null, RespireValue[]? args = null)
        => Call(function, keys, args, static (client, value) => client.DeserializeBorrowed<T>(in value));
    public RespirePending<long> ExecuteInteger(RespireFunction function, RespireKey[]? keys = null, RespireValue[]? args = null)
        => Call(function, keys, args, static (_, value) => ResponseReader.Integer(in value));
    public RespirePending<string?> ExecuteString(RespireFunction function, RespireKey[]? keys = null, RespireValue[]? args = null)
        => Call(function, keys, args, static (_, value) => ResponseReader.StringOrNull(in value));
    private RespirePending<T> Call<T>(RespireFunction function, RespireKey[]? keys, RespireValue[]? args, Func<RespireClient, RespValue, T> convert)
    {
        var command = FunctionCommands.CallCommand(sink.Client, function, keys.AsSpan(), args.AsSpan());
        return sink.Add<BatchScriptCommand, T>(function.Operation, command, keys.AsSpan(), convert);
    }
    public RespirePending<string> Load(string source, bool replace = false)
        => sink.Add<CmdN, string>("FUNCTION LOAD", FunctionCommands.LoadCommand(source, replace), static (_, value) => ResponseReader.String(in value));
    public RespirePending<RespireFunctionLibraryInfo[]> List(string? libraryPattern = null, bool withCode = false)
        => sink.Add<CmdN, RespireFunctionLibraryInfo[]>("FUNCTION LIST", FunctionCommands.ListCommand(libraryPattern, withCode), static (_, value) => FunctionResponseReader.Libraries(in value));
    public RespirePending<bool> Delete(string libraryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryName);
        return sink.Add<Cmd1, bool>("FUNCTION DELETE", new Cmd1(FunctionVerbs.Delete, libraryName), static (_, value) => ResponseReader.Ok(in value));
    }
    public RespirePending<bool> Flush(FunctionFlushMode mode = FunctionFlushMode.Default)
        => sink.Add<CmdN, bool>("FUNCTION FLUSH", FunctionCommands.FlushCommand(mode), static (_, value) => ResponseReader.Ok(in value));
    public RespirePending<byte[]> Dump()
        => sink.Add<Cmd, byte[]>("FUNCTION DUMP", new Cmd(FunctionVerbs.Dump), static (_, value) => value.AsSpan().ToArray());
    public RespirePending<bool> Restore(ReadOnlyMemory<byte> payload, FunctionRestorePolicy policy = FunctionRestorePolicy.Append)
        => sink.Add<CmdN, bool>("FUNCTION RESTORE", FunctionCommands.RestoreCommand(payload, policy), static (_, value) => ResponseReader.Ok(in value));
    public RespirePending<RespireFunctionStats> Stats()
        => sink.Add<Cmd, RespireFunctionStats>("FUNCTION STATS", new Cmd(FunctionVerbs.Stats), static (_, value) => FunctionResponseReader.Stats(in value));
}
