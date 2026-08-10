using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// A Lua script with its SHA1 precomputed. Create once (static readonly), execute many times —
/// execution tries EVALSHA first and transparently falls back to EVAL (which caches the script
/// server-side) the first time a server hasn't seen it.
/// </summary>
public sealed class RespireScript
{
    private const int StackallocThreshold = 256;
    private const string HexDigits = "0123456789abcdef";

    private RespireScript(string source, string sha1)
    {
        Source = source;
        Sha1 = sha1;
    }

    /// <summary>The Lua source text.</summary>
    public string Source { get; }

    /// <summary>The lowercase SHA1 used by Redis EVALSHA.</summary>
    public string Sha1 { get; }

    /// <summary>Creates a reusable script and computes its SHA1.</summary>
    public static RespireScript Create(string source)
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

            return new RespireScript(source, new string(hex));
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

/// <summary>Lua scripting commands.</summary>
public interface IScriptCommands
{
    /// <summary>
    /// Executes a script. Keys go through KEYS[…] (and get this view's key prefix); args through
    /// ARGV[…]. The result is a lease — dispose it. Redis: EVALSHA / EVAL.
    /// </summary>
    ValueTask<RespireResult> ExecuteAsync(
        RespireScript script,
        RespireKey[]? keys = null,
        RespireValue[]? args = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a script from span-based key and argument collections. The result owns pooled
    /// memory and must be disposed. Redis: EVALSHA / EVAL.
    /// </summary>
    ValueTask<RespireResult> ExecuteSpanAsync(
        RespireScript script,
        ReadOnlySpan<RespireKey> keys,
        ReadOnlySpan<RespireValue> args,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(script, keys.ToArray(), args.ToArray(), cancellationToken);

    /// <summary>Executes a script and deserializes its scalar result. Redis: EVALSHA / EVAL.</summary>
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

    /// <summary>Executes a script and reads its integer result. Redis: EVALSHA / EVAL.</summary>
    async ValueTask<long> ExecuteIntegerAsync(
        RespireScript script,
        RespireKey[]? keys = null,
        RespireValue[]? args = null,
        CancellationToken cancellationToken = default)
    {
        using var result = await ExecuteAsync(script, keys, args, cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>Executes a script and reads its string result, or null. Redis: EVALSHA / EVAL.</summary>
    async ValueTask<string?> ExecuteStringAsync(
        RespireScript script,
        RespireKey[]? keys = null,
        RespireValue[]? args = null,
        CancellationToken cancellationToken = default)
    {
        using var result = await ExecuteAsync(script, keys, args, cancellationToken).ConfigureAwait(false);
        return result.IsNull ? null : result.AsString();
    }

    /// <summary>Loads a script into the server cache and returns its SHA1. Redis: SCRIPT LOAD.</summary>
    ValueTask<string> LoadAsync(RespireScript script, CancellationToken cancellationToken = default);
}

internal sealed class ScriptCommands(RespireClient client) : IScriptCommands
{
    public async ValueTask<RespireResult> ExecuteAsync(
        RespireScript script, RespireKey[]? keys = null, RespireValue[]? args = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        var tail = client.BuildScriptTail(keys, args);
        return await client.ExecuteScriptAsync(script, tail, cancellationToken).ConfigureAwait(false);
    }

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
    private async ValueTask<T?> ExecuteTypedCoreAsync<T>(
        RespireScript script, RespireValue[] tail, CancellationToken cancellationToken)
    {
        using var result = await client.ExecuteScriptAsync(script, tail, cancellationToken).ConfigureAwait(false);
        return result.As<T>();
    }

    private async ValueTask<long> ExecuteIntegerCoreAsync(
        RespireScript script, RespireValue[] tail, CancellationToken cancellationToken)
    {
        using var result = await client.ExecuteScriptAsync(script, tail, cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    private async ValueTask<string?> ExecuteStringCoreAsync(
        RespireScript script, RespireValue[] tail, CancellationToken cancellationToken)
    {
        using var result = await client.ExecuteScriptAsync(script, tail, cancellationToken).ConfigureAwait(false);
        return result.IsNull ? null : result.AsString();
    }

    public ValueTask<string> LoadAsync(RespireScript script, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (client.Core.Cluster is { } cluster)
        {
            return LoadClusterAsync(cluster, script, cancellationToken);
        }

        return LoadSingleAsync(script, cancellationToken);
    }

    private async ValueTask<string> LoadSingleAsync(
        RespireScript script,
        CancellationToken cancellationToken)
    {
        var reply = await client.SendAsync(
            "SCRIPT LOAD", new Cmd1(Verbs.ScriptLoad, script.Source), cancellationToken).ConfigureAwait(false);
        var result = ResponseReader.String(in reply);
        reply.Dispose();
        return result;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<string> LoadClusterAsync(
        ClusterRouter cluster,
        RespireScript script,
        CancellationToken cancellationToken)
    {
        var masters = await cluster.GetMasterConnectionsAsync(cancellationToken).ConfigureAwait(false);
        string? result = null;
        foreach (var connection in masters)
        {
            var reply = await client.SendOnConnectionAsync(
                    "SCRIPT LOAD", connection, new Cmd1(Verbs.ScriptLoad, script.Source), cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var loadedSha1 = ResponseReader.String(in reply);
                result ??= loadedSha1;
            }
            finally
            {
                reply.Dispose();
            }
        }

        return result ?? throw new RespireConnectionException(
            "SCRIPT LOAD did not reach any Redis Cluster master.");
    }
}
