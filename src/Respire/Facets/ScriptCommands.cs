using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// A Lua script with its SHA1 precomputed. Create once (static readonly), execute many times —
/// execution tries EVALSHA first and transparently falls back to EVAL (which caches the script
/// server-side) the first time a server hasn't seen it. Read-only scripts use the corresponding
/// EVALSHA_RO / EVAL_RO commands.
/// </summary>
public sealed class RespireScript
{
    private const int StackallocThreshold = 256;
    private const string HexDigits = "0123456789abcdef";

    private RespireScript(string source, string sha1, bool readOnly)
    {
        Source = source;
        Sha1 = sha1;
        IsReadOnly = readOnly;
    }

    /// <summary>The Lua source text.</summary>
    public string Source { get; }

    /// <summary>The lowercase SHA1 used by Redis EVALSHA.</summary>
    public string Sha1 { get; }

    /// <summary>Whether execution uses EVALSHA_RO / EVAL_RO (Redis 7+), which reject writes.</summary>
    /// <remarks>This selects the Redis command contract; it does not select a replica connection.</remarks>
    public bool IsReadOnly { get; }

    internal string EvalOperation => IsReadOnly ? "EVAL_RO" : "EVAL";
    internal string EvalShaOperation => IsReadOnly ? "EVALSHA_RO" : "EVALSHA";
    internal Verb EvalVerb => IsReadOnly ? Verbs.EvalRo : Verbs.Eval;
    internal Verb EvalShaVerb => IsReadOnly ? Verbs.EvalShaRo : Verbs.EvalSha;

    /// <summary>Creates a reusable script and computes its SHA1.</summary>
    public static RespireScript Create(string source) => Create(source, readOnly: false);

    /// <summary>Creates a reusable script. Read-only execution requires Redis 7 or later.</summary>
    public static RespireScript Create(string source, bool readOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var byteCount = Encoding.UTF8.GetByteCount(source);
        byte[]? rented = null;
        var utf8 = byteCount <= StackallocThreshold
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount));

        try
        {
            if (byteCount == source.Length)
            {
                Ascii.FromUtf16(source, utf8, out _);
            }
            else
            {
                Encoding.UTF8.GetBytes(source, utf8);
            }

            Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
            SHA1.HashData(utf8[..byteCount], hash);

            Span<char> hex = stackalloc char[SHA1.HashSizeInBytes * 2];
            for (var i = 0; i < hash.Length; i++)
            {
                hex[i * 2] = HexDigits[hash[i] >> 4];
                hex[i * 2 + 1] = HexDigits[hash[i] & 0xF];
            }

            return new RespireScript(source, new string(hex), readOnly);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }
}

/// <summary>How Redis releases memory when clearing its script cache.</summary>
public enum ScriptFlushMode
{
    /// <summary>Use the server's lazyfree-lazy-user-flush setting.</summary>
    Default,
    /// <summary>Release script memory synchronously (Redis 6.2+).</summary>
    Sync,
    /// <summary>Release script memory asynchronously (Redis 6.2+).</summary>
    Async,
}

/// <summary>Lua scripting commands.</summary>
public interface IScriptCommands
{
    /// <summary>
    /// Executes a script. Keys go through KEYS[…] (and get this view's key prefix); args through
    /// ARGV[…]. The result is a lease — dispose it. Redis: EVALSHA / EVAL, or EVALSHA_RO / EVAL_RO for read-only scripts.
    /// Arrays are forwarded as spans without copying; null arrays mean empty inputs.
    /// </summary>
    ValueTask<RespireResult> ExecuteAsync(
        RespireScript script,
        RespireKey[]? keys = null,
        RespireValue[]? args = null,
        CancellationToken cancellationToken = default)
        => ExecuteSpanAsync(script, keys.AsSpan(), args.AsSpan(), cancellationToken);

    /// <summary>
    /// Executes a script from span-based key and argument collections. The result owns pooled
    /// memory and must be disposed. Redis: EVALSHA / EVAL, or EVALSHA_RO / EVAL_RO for read-only scripts.
    /// Implementations must consume the spans before returning; callers may reuse their input
    /// storage as soon as this method returns its pending operation.
    /// </summary>
    ValueTask<RespireResult> ExecuteSpanAsync(
        RespireScript script,
        ReadOnlySpan<RespireKey> keys,
        ReadOnlySpan<RespireValue> args,
        CancellationToken cancellationToken = default);

    /// <summary>Executes a script and deserializes its scalar result. Redis: EVALSHA / EVAL, or EVALSHA_RO / EVAL_RO for read-only scripts.</summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    async ValueTask<T?> ExecuteAsync<T>(
        RespireScript script,
        RespireKey[]? keys = null,
        RespireValue[]? args = null,
        CancellationToken cancellationToken = default)
    {
        using var result = await ExecuteAsync(script, keys, args, cancellationToken).ConfigureAwait(false);
        return result.As<T>();
    }

    /// <summary>Executes a script and reads its integer result. Redis: EVALSHA / EVAL, or EVALSHA_RO / EVAL_RO for read-only scripts.</summary>
    async ValueTask<long> ExecuteIntegerAsync(
        RespireScript script,
        RespireKey[]? keys = null,
        RespireValue[]? args = null,
        CancellationToken cancellationToken = default)
    {
        using var result = await ExecuteAsync(script, keys, args, cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Executes a script and reads its string result, or null. Redis: EVALSHA / EVAL, or EVALSHA_RO / EVAL_RO for read-only scripts.</summary>
    async ValueTask<string?> ExecuteStringAsync(
        RespireScript script,
        RespireKey[]? keys = null,
        RespireValue[]? args = null,
        CancellationToken cancellationToken = default)
    {
        using var result = await ExecuteAsync(script, keys, args, cancellationToken).ConfigureAwait(false);
        return result.IsNull ? null : result.AsString();
    }

    /// <summary>Loads a script on the server or every discovered Cluster primary and returns its SHA1. Redis: SCRIPT LOAD.</summary>
    ValueTask<string> LoadAsync(RespireScript script, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks script SHA1 digests in input order. In Cluster, a digest is present only when every
    /// discovered primary has it. This is a point-in-time check; execution still handles NOSCRIPT.
    /// Redis: SCRIPT EXISTS.
    /// </summary>
    ValueTask<bool[]> ExistsAsync(params ReadOnlySpan<string> sha1s)
        => ExistsAsync(sha1s, CancellationToken.None);

    /// <summary>Checks script SHA1 digests, with cancellation. Redis: SCRIPT EXISTS.</summary>
    ValueTask<bool[]> ExistsAsync(ReadOnlySpan<string> sha1s, CancellationToken cancellationToken)
        => throw new NotSupportedException("This implementation does not support SCRIPT EXISTS.");

    /// <summary>
    /// Clears the script cache on the server, or every discovered Cluster primary. This does not
    /// delete keys. The default mode follows the server's lazyfree-lazy-user-flush setting.
    /// Explicit SYNC / ASYNC requires Redis 6.2+. Redis: SCRIPT FLUSH.
    /// </summary>
    ValueTask FlushAsync(ScriptFlushMode mode = ScriptFlushMode.Default, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This implementation does not support SCRIPT FLUSH.");
}

internal sealed class ScriptCommands(RespireClient client) : IScriptCommands
{
    public ValueTask<RespireResult> ExecuteAsync(
        RespireScript script, RespireKey[]? keys = null, RespireValue[]? args = null,
        CancellationToken cancellationToken = default)
        => ExecuteSpanAsync(script, keys.AsSpan(), args.AsSpan(), cancellationToken);

    public ValueTask<RespireResult> ExecuteSpanAsync(
        RespireScript script,
        ReadOnlySpan<RespireKey> keys,
        ReadOnlySpan<RespireValue> args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        var tail = client.BuildScriptTailFromSpans(keys, args);
        return client.ExecuteScriptAsync(script, tail, cancellationToken);
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public ValueTask<T?> ExecuteAsync<T>(
        RespireScript script, RespireKey[]? keys = null, RespireValue[]? args = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        var tail = client.BuildScriptTail(keys, args);
        return ExecuteTypedCoreAsync<T>(script, tail, cancellationToken);
    }

    public ValueTask<long> ExecuteIntegerAsync(
        RespireScript script, RespireKey[]? keys = null, RespireValue[]? args = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        var tail = client.BuildScriptTail(keys, args);
        return ExecuteIntegerCoreAsync(script, tail, cancellationToken);
    }

    public ValueTask<string?> ExecuteStringAsync(
        RespireScript script, RespireKey[]? keys = null, RespireValue[]? args = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        var tail = client.BuildScriptTail(keys, args);
        return ExecuteStringCoreAsync(script, tail, cancellationToken);
    }

    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<T?> ExecuteTypedCoreAsync<T>(
        RespireScript script, RespireValue[] tail, CancellationToken cancellationToken)
    {
        using var result = await client.ExecuteScriptAsync(script, tail, cancellationToken).ConfigureAwait(false);
        return result.As<T>();
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<long> ExecuteIntegerCoreAsync(
        RespireScript script, RespireValue[] tail, CancellationToken cancellationToken)
    {
        using var result = await client.ExecuteScriptAsync(script, tail, cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<string?> ExecuteStringCoreAsync(
        RespireScript script, RespireValue[] tail, CancellationToken cancellationToken)
    {
        using var result = await client.ExecuteScriptAsync(script, tail, cancellationToken).ConfigureAwait(false);
        return result.IsNull ? null : result.AsString();
    }

    public ValueTask<bool[]> ExistsAsync(params ReadOnlySpan<string> sha1s)
        => ExistsAsync(sha1s, CancellationToken.None);

    public ValueTask<bool[]> ExistsAsync(ReadOnlySpan<string> sha1s, CancellationToken cancellationToken)
    {
        var command = new CmdN(Verbs.ScriptExists, MapDigests(sha1s));
        return client.Core.Cluster is { } cluster
            ? ExistsClusterAsync(cluster, command, cancellationToken)
            : client.ConvertResponseAsync("SCRIPT EXISTS", command, cancellationToken, this,
                static (ScriptCommands _, in RespValue value) => ResponseReader.FlagArray(in value));
    }

    public ValueTask FlushAsync(ScriptFlushMode mode = ScriptFlushMode.Default, CancellationToken cancellationToken = default)
    {
        var command = new Cmd(FlushVerb(mode));
        return client.Core.Cluster is { } cluster
            ? FlushClusterAsync(cluster, command, cancellationToken)
            : client.OkAsync("SCRIPT FLUSH", command, cancellationToken);
    }

    internal static RespireValue[] MapDigests(ReadOnlySpan<string> sha1s)
    {
        if (sha1s.IsEmpty)
        {
            throw new ArgumentException("At least one script SHA1 digest is required.", nameof(sha1s));
        }

        var values = new RespireValue[sha1s.Length];
        for (var i = 0; i < sha1s.Length; i++)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sha1s[i]);
            values[i] = sha1s[i];
        }
        return values;
    }

    internal static Verb FlushVerb(ScriptFlushMode mode) => mode switch
    {
        ScriptFlushMode.Default => Verbs.ScriptFlush,
        ScriptFlushMode.Sync => Verbs.ScriptFlushSync,
        ScriptFlushMode.Async => Verbs.ScriptFlushAsync,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool[]> ExistsClusterAsync(
        ClusterRouter cluster, CmdN command, CancellationToken cancellationToken)
    {
        var results = await SendToPrimariesAsync(cluster, "SCRIPT EXISTS", command,
            static (ScriptCommands _, in RespValue value) => ResponseReader.FlagArray(in value), cancellationToken)
            .ConfigureAwait(false);
        var result = results[0];
        for (var primary = 1; primary < results.Length; primary++)
        {
            var exists = results[primary];
            if (exists.Length != result.Length)
            {
                throw new RespireProtocolException("SCRIPT EXISTS returned inconsistent result lengths.");
            }
            for (var i = 0; i < result.Length; i++)
            {
                result[i] &= exists[i];
            }
        }
        return result;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask FlushClusterAsync(
        ClusterRouter cluster, Cmd command, CancellationToken cancellationToken)
        => _ = await SendToPrimariesAsync(cluster, "SCRIPT FLUSH", command,
            static (ScriptCommands _, in RespValue value) => ResponseReader.Ok(in value), cancellationToken)
            .ConfigureAwait(false);

    public ValueTask<string> LoadAsync(RespireScript script, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (client.Core.Cluster is { } cluster)
        {
            return LoadClusterAsync(cluster, script, cancellationToken);
        }

        return LoadSingleAsync(script, cancellationToken);
    }

    private ValueTask<string> LoadSingleAsync(
        RespireScript script,
        CancellationToken cancellationToken)
        => client.ConvertResponseAsync(
            "SCRIPT LOAD", new Cmd1(Verbs.ScriptLoad, script.Source), cancellationToken, this,
            static (ScriptCommands _, in RespValue value) => ResponseReader.String(in value));

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<string> LoadClusterAsync(
        ClusterRouter cluster,
        RespireScript script,
        CancellationToken cancellationToken)
    {
        var results = await SendToPrimariesAsync(cluster, "SCRIPT LOAD", new Cmd1(Verbs.ScriptLoad, script.Source),
            static (ScriptCommands _, in RespValue value) => ResponseReader.String(in value), cancellationToken)
            .ConfigureAwait(false);
        return results[0];
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<TResult[]> SendToPrimariesAsync<TCommand, TResult>(
        ClusterRouter cluster, string operation, TCommand command,
        ResponseConverter<ScriptCommands, TResult> convert, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var masters = await cluster.GetMasterConnectionsAsync(cancellationToken).ConfigureAwait(false);
        if (masters.Length == 0)
        {
            throw new RespireConnectionException($"{operation} did not reach any Redis Cluster primary.");
        }
        var responses = new Task<TResult>[masters.Length];
        for (var i = 0; i < masters.Length; i++)
        {
            responses[i] = SendAndConvertAsync(operation, masters[i], command, convert, cancellationToken).AsTask();
        }
        // Observe every send, including failures, before returning. Each operation owns and
        // disposes its reply independently, even when another primary fails or cancels.
        return await Task.WhenAll(responses).ConfigureAwait(false);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<TResult> SendAndConvertAsync<TCommand, TResult>(
        string operation, RespireConnection connection, TCommand command,
        ResponseConverter<ScriptCommands, TResult> convert, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        using var reply = await client.SendOnConnectionAsync(operation, connection, command, cancellationToken)
            .ConfigureAwait(false);
        return convert(this, in reply);
    }
}
