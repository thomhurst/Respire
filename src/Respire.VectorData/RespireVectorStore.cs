using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.VectorData;
using Respire.Search;

namespace Respire.VectorData;

/// <summary>A hash-backed VectorData store with explicit AOT-safe record mappings.</summary>
/// <remarks>The caller owns the client. This connector supports standalone Redis Query Engine with an unprefixed client.
/// Redis Cluster is unsupported: collection deletion scans one node and cannot remove all unindexed hashes across shards.</remarks>
public sealed class RespireVectorStore : VectorStore
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly ConcurrentDictionary<Type, object> _mappers = new();
    private readonly IRespireClient _client;
    private readonly string _namespace;
    private volatile bool _disposed;

    /// <summary>Creates a store. The namespace isolates its index and document names from other applications.</summary>
    public RespireVectorStore(IRespireClient client, string keyNamespace = "respire:vector:")
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyNamespace);
        _client = client;
        _namespace = "respire:vector:" + EncodeName(keyNamespace) + ":";
    }

    /// <summary>Registers one immutable mapping per record type. Existing collection definitions cannot replace it.</summary>
    public void RegisterMapper<TRecord>(RespireVectorDataHashMapper<TRecord> mapper) where TRecord : class
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(mapper);
        if (!_mappers.TryAdd(typeof(TRecord), mapper)) throw new InvalidOperationException("A mapper is already registered for this record type.");
    }

    /// <inheritdoc />
    [RequiresDynamicCode("The VectorStore abstraction requires dynamic code. Use GetHashCollection for explicit AOT-safe mapping.")]
    [RequiresUnreferencedCode("The VectorStore abstraction requires unreferenced code. Use GetHashCollection for explicit AOT-safe mapping.")]
    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(string name, VectorStoreCollectionDefinition? definition = null)
    {
        ThrowIfDisposed();
        if (typeof(TKey) != typeof(string)) throw new NotSupportedException("Hash collections support string keys.");
        if (definition is not null) throw new NotSupportedException("Register an explicit mapper; reflection-based collection definitions are not supported.");
        return (VectorStoreCollection<TKey, TRecord>)(object)GetHashCollection<TRecord>(name);
    }

    /// <summary>Gets a string-keyed hash collection using its registered mapper without reflection or dynamic code.</summary>
    public RespireVectorStoreCollection<TRecord> GetHashCollection<TRecord>(string name) where TRecord : class
    {
        ThrowIfDisposed();
        if (!_mappers.TryGetValue(typeof(TRecord), out var mapper)) throw new InvalidOperationException("Register a mapper for this record type before creating a collection.");
        return new(_client, name, IndexName(name), DocumentPrefix(name), (RespireVectorDataHashMapper<TRecord>)mapper);
    }

    /// <inheritdoc />
    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(string name, VectorStoreCollectionDefinition definition)
        => throw new NotSupportedException("Dynamic collections are not supported; use an explicit typed mapper.");

    /// <inheritdoc />
    public override async IAsyncEnumerable<string> ListCollectionNamesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var prefix = _namespace + "i:";
        var indexes = await RespireVectorDataOperations.ExecuteAsync(
            _client.Search.ListIndexesAsync(cancellationToken), nameof(ListCollectionNamesAsync)).ConfigureAwait(false);
        foreach (var name in indexes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                yield return RespireVectorDataOperations.DecodeName(name[prefix.Length..], nameof(ListCollectionNamesAsync));
        }
    }

    /// <inheritdoc />
    public override async Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var index = IndexName(name);
        var indexes = await RespireVectorDataOperations.ExecuteAsync(
            _client.Search.ListIndexesAsync(cancellationToken), nameof(CollectionExistsAsync), name).ConfigureAwait(false);
        return indexes.Contains(index, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public override async Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await RespireVectorDataOperations.DeleteCollectionAsync(
            _client, IndexName(name), DocumentPrefix(name), name, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null) return null;
        if (serviceType == typeof(VectorStoreMetadata)) return new VectorStoreMetadata { VectorStoreSystemName = "redis" };
        if (serviceType == typeof(IRespireClient)) return _client;
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    internal string IndexName(string name) => _namespace + "i:" + EncodeName(name);
    internal string DocumentPrefix(string name) => _namespace + "d:" + EncodeName(name) + ":";

    internal static string EncodeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Convert.ToBase64String(Utf8.GetBytes(name)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static string DecodeName(string encoded)
        => Utf8.GetString(Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4)));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }
}
