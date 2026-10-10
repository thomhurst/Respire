using System.Buffers.Binary;
using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.VectorData;
using Respire.Json;
using Respire.Search;

namespace Respire.VectorData;

/// <summary>String-keyed Redis hash or JSON records, FLOAT32 KNN search and full-text/vector hybrid search.</summary>
public sealed partial class RespireVectorStoreCollection<TRecord> : VectorStoreCollection<string, TRecord>, IKeywordHybridSearchable<TRecord> where TRecord : class
{
    // One script replaces the complete hash atomically, including absent optional fields.
    private static readonly RespireScript Replace = RespireScript.Create("redis.call('DEL',KEYS[1]); for i=1,#ARGV,2 do redis.call('HSET',KEYS[1],ARGV[i],ARGV[i+1]); end; return 1");
    private static readonly UTF8Encoding FilterUtf8 = new(false, true);
    // JSON.SET replaces the root without deleting first, so a server rejection preserves the previous record.
    private static readonly RespireScript ReplaceJson = RespireScript.Create("redis.call('JSON.SET',KEYS[1],'$',ARGV[1]); redis.call('PERSIST',KEYS[1]); return 1");
    private readonly IRespireClient _client;
    private readonly string _index;
    private readonly string _prefix;
    private readonly RespireVectorDataMapper<TRecord> _mapper;
    private readonly RespireVectorDataJsonMapper<TRecord>? _jsonMapper;
    private readonly string[][] _vectorPaths;
    private readonly RespireVectorDataVectorField[] _vectors;
    private readonly RespireSearchField[] _fields;
    private readonly RespireVectorDataFilterField[] _filterFields;
    private readonly RespireVectorDataTextField[] _textFields;
    private volatile bool _disposed;

    internal RespireVectorStoreCollection(IRespireClient client, string name, string index, string prefix, RespireVectorDataMapper<TRecord> mapper)
    {
        _client = client;
        Name = name;
        _index = index;
        _prefix = prefix;
        _mapper = mapper;
        _jsonMapper = mapper as RespireVectorDataJsonMapper<TRecord>;
        _vectors = mapper.VectorFields.ToArray();
        _vectorPaths = new string[_vectors.Length][];
        if (_vectors.Length == 0) throw new ArgumentException("At least one vector field is required.", nameof(mapper));
        var names = new HashSet<string>(StringComparer.Ordinal);
        var properties = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<RespireSearchField>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
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
            var identifier = vector.StorageName;
            if (_jsonMapper is not null)
            {
                identifier = vector.JsonPath ?? "$." + vector.StorageName;
                _vectorPaths[fields.Count] = RespireVectorDataJsonPaths.Parse(identifier);
                RespireVectorDataJsonPaths.ValidateMetadata(_jsonMapper.JsonTypeInfo, _vectorPaths[fields.Count]);
                if (!paths.Add(identifier)) throw new ArgumentException("JSON schema paths must be unique.", nameof(mapper));
            }
            fields.Add(new(identifier, RespireSearchFieldType.Vector, Alias: _jsonMapper is null ? null : vector.StorageName)
            {
                Vector = new(vector.Algorithm, RespireSearchVectorType.Float32, vector.Dimensions, vector.DistanceMetric),
            });
        }
        foreach (var field in mapper.DataFields)
        {
            ArgumentNullException.ThrowIfNull(field);
            if (_jsonMapper is null) ValidateFieldName(field.Identifier);
            else
            {
                RespireVectorDataJsonPaths.ValidateMetadata(_jsonMapper.JsonTypeInfo, RespireVectorDataJsonPaths.Parse(field.Identifier));
                ValidateFieldName(field.Alias ?? throw new ArgumentException("JSON scalar fields require a query alias.", nameof(mapper)));
                if (!paths.Add(field.Identifier)) throw new ArgumentException("JSON schema paths must be unique.", nameof(mapper));
            }
            ValidateScalarField(field, allowAlias: _jsonMapper is not null);
            if (!names.Add(field.Alias ?? field.Identifier)) throw new ArgumentException("Schema field names must be unique.", nameof(mapper));
            fields.Add(field);
        }
        _filterFields = mapper is RespireVectorDataHashMapper<TRecord> hashMapper ? hashMapper.FilterFields.ToArray() : [];
        foreach (var filter in _filterFields)
        {
            ArgumentNullException.ThrowIfNull(filter);
            ValidateFieldName(filter.StorageName);
            ArgumentException.ThrowIfNullOrWhiteSpace(filter.PropertyName);
            if (!Enum.IsDefined(filter.Kind) || !names.Add(filter.StorageName) || !properties.Add(filter.PropertyName))
                throw new ArgumentException("Filter fields require unique schema/property names and a supported kind.", nameof(mapper));
            var tag = filter.Kind is RespireVectorDataFilterKind.String or RespireVectorDataFilterKind.StringCollection;
            fields.Add(new(filter.StorageName, tag ? RespireSearchFieldType.Tag : RespireSearchFieldType.Numeric)
            {
                CaseSensitive = tag,
                Options = ["INDEXMISSING"],
            });
        }
        _fields = fields.ToArray();
        _textFields = mapper.TextFields.ToArray();
        var textProperties = new HashSet<string>(StringComparer.Ordinal);
        var textNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in _textFields)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentException.ThrowIfNullOrWhiteSpace(text.PropertyName);
            ValidateFieldName(text.StorageName);
            if (!textProperties.Add(text.PropertyName) || !textNames.Add(text.StorageName)
                || !_fields.Any(field => field.Type == RespireSearchFieldType.Text && !field.NoIndex && (field.Alias ?? field.Identifier) == text.StorageName))
                throw new ArgumentException("Text mappings require unique properties and existing indexed TEXT fields in DataFields.", nameof(mapper));
        }
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
            _client.Search.CreateIndexAsync(_index, new() { Source = _jsonMapper is null ? RespireSearchSource.Hash : RespireSearchSource.Json, Prefixes = [_prefix], Fields = _fields }, cancellationToken),
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
        if (_jsonMapper is not null)
        {
            using var json = await RespireVectorDataOperations.ExecuteAsync(
                _client.Json.Commands.GetAsync(RecordKey(key), ["."], cancellationToken), nameof(GetAsync), Name).ConfigureAwait(false);
            if (json.IsNull) return null;
            if (options?.IncludeVectors == true) RespireVectorDataJsonPaths.ValidateVectors(json.AsBytes(), _vectors, _vectorPaths, _jsonMapper.JsonTypeInfo.Options.MaxDepth);
            return _jsonMapper.Read(json.AsSpan(), options?.IncludeVectors == true, _vectorPaths);
        }
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
        return ((RespireVectorDataHashMapper<TRecord>)_mapper).Read(key, fields);
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
        if (_jsonMapper is not null)
        {
            var json = _jsonMapper.Write(record);
            RespireVectorDataJsonPaths.ValidateVectors(json, _vectors, _vectorPaths, _jsonMapper.JsonTypeInfo.Options.MaxDepth);
            using var replaced = await RespireVectorDataOperations.ExecuteAsync(
                _client.Scripts.ExecuteAsync(ReplaceJson, [key], [json], cancellationToken), nameof(UpsertAsync), Name).ConfigureAwait(false);
            return;
        }
        var fields = ((RespireVectorDataHashMapper<TRecord>)_mapper).Write(record);
        if (fields.Count == 0) throw new ArgumentException("At least one hash field is required.", nameof(record));
        foreach (var vector in _vectors)
        {
            if (fields.TryGetValue(vector.StorageName, out var bytes)) ValidateVector(vector, bytes);
        }
        ValidateFilterValues(fields);
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
    public override async IAsyncEnumerable<TRecord> GetAsync(Expression<Func<TRecord, bool>> filter, int top, FilteredRecordRetrievalOptions<TRecord>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (top <= 0) throw new ArgumentOutOfRangeException(nameof(top));
        if (_jsonMapper is not null) throw new NotSupportedException("Expression filters and filtered retrieval are unsupported for JSON storage.");
        var skip = options?.Skip ?? 0;
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options?.OrderBy is not null) throw new NotSupportedException("Filtered retrieval ordering is unsupported.");
        var query = new RespireSearchQuery(new RespireVectorDataFilter<TRecord>(_filterFields).Translate(filter),
            new() { Dialect = 2, NoContent = true, Limit = (skip, top) });
        var result = await RespireVectorDataOperations.ExecuteAsync(
            _client.Search.SearchAsync(_index, query, cancellationToken), nameof(GetAsync), Name).ConfigureAwait(false);
        var keys = result.Documents.Select(document => SearchKey(document.Id, nameof(GetAsync))).ToArray();
        await foreach (var record in GetAsync(keys, new RecordRetrievalOptions { IncludeVectors = options?.IncludeVectors ?? false }, cancellationToken).ConfigureAwait(false))
            yield return record;
    }

    /// <inheritdoc />
    /// <remarks>Retrieves records in concurrent batches of up to 32 after searching; this is not a transactional snapshot.
    /// ScoreThreshold filters the selected Skip/top page without replacing hits. Records deleted before retrieval are
    /// omitted, so either condition can return fewer than top records.</remarks>
    public override async IAsyncEnumerable<VectorSearchResult<TRecord>> SearchAsync<TInput>(TInput searchValue, int top, VectorSearchOptions<TRecord>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(searchValue);
        cancellationToken.ThrowIfCancellationRequested();
        if (top <= 0) throw new ArgumentOutOfRangeException(nameof(top));
        if (_jsonMapper is not null && options?.Filter is not null)
            throw new NotSupportedException("Expression filters and filtered retrieval are unsupported for JSON storage.");
        var filter = options?.Filter is { } expression ? new RespireVectorDataFilter<TRecord>(_filterFields).Translate(expression) : (RespireSearchExpression?)null;
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
        var request = new RespireVectorSearchRequest(vector.StorageName, bytes, checked(skip + top)) { Filter = filter };
        var queryOptions = new RespireSearchQueryOptions { Limit = (skip, top), ReturnFields = ["vector_score"] };
        var result = await RespireVectorDataOperations.ExecuteAsync(
            _client.Search.VectorSearchAsync(_index, request, queryOptions, cancellationToken), nameof(SearchAsync), Name).ConfigureAwait(false);
        var hits = new List<(string Key, double Score)>();
        foreach (var document in result.Documents)
        {
            var score = RespireVectorDataOperations.ReadSearchScore(document, _prefix, Name);
            // Redis IP is 1 - dot(a,b); VectorData's negative dot product is -dot(a,b).
            // Normalize before applying the public ScoreThreshold, preserving nearest-first order.
            if (vector.DistanceMetric == RespireSearchDistanceMetric.InnerProduct) score -= 1;
            if (options?.ScoreThreshold is { } threshold && score > threshold) continue;
            var key = RespireVectorDataOperations.DecodeName(document.Id[_prefix.Length..], nameof(SearchAsync), Name);
            hits.Add((key, score));
        }
        await foreach (var hit in ReadHitsAsync(hits, options?.IncludeVectors ?? false, cancellationToken).ConfigureAwait(false))
            yield return hit;
    }

    private async IAsyncEnumerable<VectorSearchResult<TRecord>> ReadHitsAsync(List<(string Key, double Score)> hits, bool includeVectors, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int batchSize = 32;
        var retrievalOptions = new RecordRetrievalOptions { IncludeVectors = includeVectors };
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
            if (_vectors.Length != 1) throw new InvalidOperationException($"The '{typeof(TRecord).Name}' type has multiple vector properties, please specify your chosen property via options.");
            return _vectors[0];
        }
        var body = property.Body is UnaryExpression { NodeType: ExpressionType.Convert } conversion ? conversion.Operand : property.Body;
        if (body is MemberExpression { Expression: ParameterExpression } direct && direct.Expression == property.Parameters[0])
            return _vectors.FirstOrDefault(v => v.PropertyName == direct.Member.Name) ?? throw new ArgumentException("The selected property is not a mapped vector.", nameof(property));
        if (_jsonMapper is null) throw new ArgumentException("Select a direct mapped vector property.", nameof(property));
        var members = new Stack<string>();
        while (body is MemberExpression member)
        {
            members.Push(member.Member.Name);
            body = member.Expression;
        }
        if (body == property.Parameters[0] && members.Count > 0)
        {
            var name = string.Join('.', members);
            return _vectors.FirstOrDefault(v => v.PropertyName == name) ?? throw new ArgumentException("The selected property is not a mapped vector.", nameof(property));
        }
        throw new ArgumentException("Select a mapped vector property; JSON mappings also support nested member paths.", nameof(property));
    }

    private string RecordKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _prefix + RespireVectorStore.EncodeName(key);
    }

    private string SearchKey(string id, string operation)
    {
        if (!id.StartsWith(_prefix, StringComparison.Ordinal)) throw new VectorStoreException("Search returned a document outside the collection.", new InvalidOperationException("Unexpected document prefix."))
        { OperationName = operation, CollectionName = Name, VectorStoreSystemName = "redis" };
        return RespireVectorDataOperations.DecodeName(id[_prefix.Length..], operation, Name);
    }

    private void ValidateFilterValues(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> fields)
    {
        foreach (var filter in _filterFields)
        {
            if (!fields.TryGetValue(filter.StorageName, out var bytes)) continue;
            var text = FilterUtf8.GetString(bytes.Span);
            if (filter.Kind is RespireVectorDataFilterKind.String or RespireVectorDataFilterKind.StringCollection)
            {
                if (filter.Kind == RespireVectorDataFilterKind.StringCollection && text.Length == 0) continue;
                var tokens = filter.Kind == RespireVectorDataFilterKind.String ? new[] { text } : text.Split(',');
                foreach (var token in tokens)
                {
                    if (RespireVectorDataFilterEncoding.EncodeTag(RespireVectorDataFilterEncoding.DecodeTag(token)) != token)
                        throw new ArgumentException($"Filter field '{filter.StorageName}' requires canonical encoded TAG values.", nameof(fields));
                }
            }
            else if (filter.Kind == RespireVectorDataFilterKind.Boolean ? text is not "0" and not "1"
                : !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                throw new ArgumentException($"Filter field '{filter.StorageName}' requires a finite invariant number or Boolean 0/1.", nameof(fields));
        }
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

    private static void ValidateScalarField(RespireSearchField field, bool allowAlias = false)
    {
        if (!Enum.IsDefined(field.Type) || field.Type == RespireSearchFieldType.Vector || field.Vector is not null || (!allowAlias && field.Alias is not null) || field.Options is { Count: > 0 })
            throw new ArgumentException("Scalar fields require a supported scalar type without vector schemas or raw options; aliases require JSON storage.", nameof(field));
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
