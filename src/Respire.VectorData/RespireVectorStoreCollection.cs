using System.Buffers.Binary;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.VectorData;
using Respire.Search;

namespace Respire.VectorData;

/// <summary>String-keyed Redis hash records and unfiltered FLOAT32 KNN search.</summary>
public sealed class RespireVectorStoreCollection<TRecord> : VectorStoreCollection<string, TRecord> where TRecord : class
{
    // One script replaces the complete hash atomically, including absent optional fields.
    private static readonly RespireScript Replace = RespireScript.Create("redis.call('DEL',KEYS[1]); for i=1,#ARGV,2 do redis.call('HSET',KEYS[1],ARGV[i],ARGV[i+1]); end; return 1");
    private readonly IRespireClient _client;
    private readonly string _index;
    private readonly string _prefix;
    private readonly RespireVectorDataHashMapper<TRecord> _mapper;
    private readonly RespireVectorDataVectorField[] _vectors;
    private readonly RespireSearchField[] _fields;
    private volatile bool _disposed;

    internal RespireVectorStoreCollection(IRespireClient client, string name, string index, string prefix, RespireVectorDataHashMapper<TRecord> mapper)
    {
        _client = client;
        Name = name;
        _index = index;
        _prefix = prefix;
        _mapper = mapper;
        _vectors = mapper.VectorFields.ToArray();
        if (_vectors.Length == 0) throw new ArgumentException("At least one vector field is required.", nameof(mapper));
        var names = new HashSet<string>(StringComparer.Ordinal);
        var properties = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<RespireSearchField>();
        foreach (var vector in _vectors)
        {
            ArgumentNullException.ThrowIfNull(vector);
            ValidateFieldName(vector.StorageName);
            ArgumentException.ThrowIfNullOrWhiteSpace(vector.PropertyName);
            if (vector.Dimensions <= 0 || vector.Dimensions > int.MaxValue / sizeof(float))
                throw new ArgumentException("Vector dimensions must be positive and fit in a byte buffer.", nameof(mapper));
            if (!Enum.IsDefined(vector.Algorithm) || !Enum.IsDefined(vector.DistanceMetric))
                throw new ArgumentException("Unknown vector algorithm or distance metric.", nameof(mapper));
            if (!names.Add(vector.StorageName) || !properties.Add(vector.PropertyName))
                throw new ArgumentException("Vector field/property names must be unique.", nameof(mapper));
            fields.Add(new(vector.StorageName, RespireSearchFieldType.Vector)
            {
                Vector = new(vector.Algorithm, RespireSearchVectorType.Float32, vector.Dimensions, vector.DistanceMetric),
            });
        }
        foreach (var field in mapper.DataFields)
        {
            ArgumentNullException.ThrowIfNull(field);
            ValidateFieldName(field.Identifier);
            ValidateScalarField(field);
            if (!names.Add(field.Identifier)) throw new ArgumentException("Schema field names must be unique.", nameof(mapper));
            fields.Add(field);
        }
        _fields = fields.ToArray();
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override async Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var indexes = await RespireVectorDataOperations.ExecuteAsync(
            _client.Search.ListIndexesAsync(cancellationToken), nameof(CollectionExistsAsync), Name).ConfigureAwait(false);
        return indexes.Contains(_index, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public override async Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        if (await CollectionExistsAsync(cancellationToken).ConfigureAwait(false)) return;
        await RespireVectorDataOperations.ExecuteAsync(
            _client.Search.CreateIndexAsync(_index, new() { Prefixes = [_prefix], Fields = _fields }, cancellationToken),
            nameof(EnsureCollectionExistsAsync), Name, "Index already exists").ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await RespireVectorDataOperations.DeleteCollectionAsync(
            _client, _index, _prefix, Name, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>Server and transport failures are wrapped in VectorStoreException. Mapper and vector validation errors
    /// propagate unchanged. An incomplete HGETALL field/value pair throws InvalidOperationException.</remarks>
    public override async Task<TRecord?> GetAsync(string key, RecordRetrievalOptions? options = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        using var result = await RespireVectorDataOperations.ExecuteAsync(
            _client.ExecuteAsync(RespireCommands.Hash.HGETALL, [RecordKey(key)], cancellationToken: cancellationToken),
            nameof(GetAsync), Name).ConfigureAwait(false);
        if (result.Count == 0) return null;
        var fields = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        if (result.Count % 2 != 0) throw new InvalidOperationException("HGETALL returned an incomplete field/value pair.");
        for (var i = 0; i < result.Count; i += 2) fields.Add(result[i].AsString(), result[i + 1].AsBytes());
        foreach (var vector in _vectors)
        {
            if (options?.IncludeVectors != true) fields.Remove(vector.StorageName);
            else if (fields.TryGetValue(vector.StorageName, out var bytes)) ValidateVector(vector, bytes);
        }
        return _mapper.Read(key, fields);
    }

    /// <inheritdoc />
    /// <remarks>Reads records sequentially. A failure ends enumeration after any records already yielded.</remarks>
    public override async IAsyncEnumerable<TRecord> GetAsync(IEnumerable<string> keys, RecordRetrievalOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var key in keys)
        {
            var record = await GetAsync(key, options, cancellationToken).ConfigureAwait(false);
            if (record is not null) yield return record;
        }
    }

    /// <inheritdoc />
    public override Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return DeleteCoreAsync(key, cancellationToken);
    }

    private async Task DeleteCoreAsync(string key, CancellationToken cancellationToken)
        => _ = await RespireVectorDataOperations.ExecuteAsync(_client.Keys.DeleteAsync([RecordKey(key)], cancellationToken), nameof(DeleteAsync), Name).ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>Deletes records sequentially without a transaction. A failure leaves earlier deletions applied.</remarks>
    public override async Task DeleteAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var key in keys) await DeleteAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        var key = RecordKey(_mapper.GetKey(record));
        var fields = _mapper.Write(record);
        if (fields.Count == 0) throw new ArgumentException("At least one hash field is required.", nameof(record));
        foreach (var vector in _vectors)
        {
            if (fields.TryGetValue(vector.StorageName, out var bytes)) ValidateVector(vector, bytes);
        }
        var args = new RespireValue[checked(fields.Count * 2)];
        var i = 0;
        foreach (var field in fields)
        {
            ArgumentException.ThrowIfNullOrEmpty(field.Key);
            args[i++] = field.Key;
            args[i++] = field.Value;
        }
        using var result = await RespireVectorDataOperations.ExecuteAsync(
            _client.Scripts.ExecuteAsync(Replace, [key], args, cancellationToken), nameof(UpsertAsync), Name).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>Upserts records sequentially without a transaction. A failure leaves earlier upserts applied.</remarks>
    public override async Task UpsertAsync(IEnumerable<TRecord> records, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var record in records) await UpsertAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<TRecord> GetAsync(Expression<Func<TRecord, bool>> filter, int top, FilteredRecordRetrievalOptions<TRecord>? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Expression filters are not supported by the initial hash connector.");

    /// <inheritdoc />
    /// <remarks>Retrieves hashes in concurrent batches of up to 32 after searching; this is not a transactional snapshot.
    /// ScoreThreshold filters the selected Skip/top page without replacing hits. Hashes deleted before retrieval are
    /// omitted, so either condition can return fewer than top records.</remarks>
    public override async IAsyncEnumerable<VectorSearchResult<TRecord>> SearchAsync<TInput>(TInput searchValue, int top, VectorSearchOptions<TRecord>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(searchValue);
        cancellationToken.ThrowIfCancellationRequested();
        if (top <= 0) throw new ArgumentOutOfRangeException(nameof(top));
        if (options?.Filter is not null) throw new NotSupportedException("Expression filters are not supported by the initial hash connector.");
        var skip = options?.Skip ?? 0;
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options?.ScoreThreshold is { } scoreThreshold && !double.IsFinite(scoreThreshold)) throw new ArgumentOutOfRangeException(nameof(options));
        var vector = SelectVector(options?.VectorProperty);
        ReadOnlyMemory<float> values = searchValue switch
        {
            ReadOnlyMemory<float> memory => memory,
            Memory<float> memory => memory,
            float[] array => array,
            _ => throw new NotSupportedException("Search input must be a FLOAT32 array or memory. Embedding generation is not supported."),
        };
        var bytes = RespireVectorDataFloat32.Encode(values.Span);
        ValidateVector(vector, bytes);
        var request = new RespireVectorSearchRequest(vector.StorageName, bytes, checked(skip + top));
        var queryOptions = new RespireSearchQueryOptions { Limit = (skip, top), ReturnFields = ["vector_score"] };
        var result = await RespireVectorDataOperations.ExecuteAsync(
            _client.Search.VectorSearchAsync(_index, request, queryOptions, cancellationToken), nameof(SearchAsync), Name).ConfigureAwait(false);
        var hits = new List<(string Key, double Score)>();
        foreach (var document in result.Documents)
        {
            var score = RespireVectorDataOperations.ReadSearchScore(document, _prefix, Name);
            if (options?.ScoreThreshold is { } threshold && score > threshold) continue;
            var key = RespireVectorDataOperations.DecodeName(document.Id[_prefix.Length..], nameof(SearchAsync), Name);
            hits.Add((key, score));
        }
        const int batchSize = 32;
        var retrievalOptions = new RecordRetrievalOptions { IncludeVectors = options?.IncludeVectors ?? false };
        for (var offset = 0; offset < hits.Count; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(batchSize, hits.Count - offset);
            var reads = new Task<TRecord?>[count];
            for (var i = 0; i < count; i++)
                reads[i] = GetAsync(hits[offset + i].Key, retrievalOptions, cancellationToken);
            // Observe every read before yielding, including when enumeration stops early or one read fails.
            var records = await Task.WhenAll(reads).ConfigureAwait(false);
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (records[i] is { } record) yield return new(record, hits[offset + i].Score);
            }
        }
    }

    private RespireVectorDataVectorField SelectVector(Expression<Func<TRecord, object?>>? property)
    {
        if (property is null)
        {
            if (_vectors.Length != 1) throw new InvalidOperationException("Select a vector property when the model has multiple vectors.");
            return _vectors[0];
        }
        var body = property.Body is UnaryExpression { NodeType: ExpressionType.Convert } conversion ? conversion.Operand : property.Body;
        if (body is MemberExpression { Expression: ParameterExpression } member && member.Expression == property.Parameters[0])
            return _vectors.FirstOrDefault(v => v.PropertyName == member.Member.Name) ?? throw new ArgumentException("The selected property is not a mapped vector.", nameof(property));
        throw new ArgumentException("Select a direct mapped vector property.", nameof(property));
    }

    private string RecordKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _prefix + RespireVectorStore.EncodeName(key);
    }

    private static void ValidateVector(RespireVectorDataVectorField field, ReadOnlyMemory<byte> vector)
    {
        if (vector.Length != field.Dimensions * sizeof(float)) throw new ArgumentException("Vector byte length does not match the mapped dimensions.", nameof(vector));
        for (var i = 0; i < vector.Length; i += sizeof(float))
            if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(vector.Span[i..]))) throw new ArgumentException("Vector elements must be finite.", nameof(vector));
    }

    private static void ValidateFieldName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name == "vector_score" || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw new ArgumentException("Schema names must contain only ASCII letters, digits and underscores and cannot be vector_score.", nameof(name));
    }

    private static void ValidateScalarField(RespireSearchField field)
    {
        if (!Enum.IsDefined(field.Type) || field.Type == RespireSearchFieldType.Vector || field.Vector is not null || field.Alias is not null || field.Options is { Count: > 0 })
            throw new ArgumentException("Scalar fields require a supported scalar type without aliases, vector schemas or raw options.", nameof(field));
        if (field.Type == RespireSearchFieldType.GeoShape && field.Sortable)
            throw new ArgumentException("GEOSHAPE fields do not support SORTABLE.", nameof(field));
        if (field.Type != RespireSearchFieldType.Text && (field.Weight is not null || field.NoStem || field.Phonetic is not null))
            throw new ArgumentException("Weight, stemming and phonetic options require a text field.", nameof(field));
        if (field.Weight is { } weight && (!double.IsFinite(weight) || weight < 0))
            throw new ArgumentException("Text weights must be finite and non-negative.", nameof(field));
        if (field.Phonetic is { } phonetic && !Enum.IsDefined(phonetic))
            throw new ArgumentException("Unknown phonetic matcher.", nameof(field));
        if (field.Type != RespireSearchFieldType.Tag && (field.Separator is not null || field.CaseSensitive))
            throw new ArgumentException("Separator and case options require a tag field.", nameof(field));
        if (field.Separator is < ' ' or > '~')
            throw new ArgumentException("TAG separators must be printable ASCII characters.", nameof(field));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null) return null;
        if (serviceType == typeof(VectorStoreCollectionMetadata)) return new VectorStoreCollectionMetadata { VectorStoreSystemName = "redis", CollectionName = Name };
        if (serviceType == typeof(IRespireClient)) return _client;
        return serviceType.IsInstanceOfType(this) ? this : null;
    }
    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }
}
