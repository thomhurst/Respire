using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.VectorData;
using Respire.Search;

namespace Respire.VectorData;

public sealed partial class RespireVectorStoreCollection<TRecord>
{
    /// <summary>Fuses full-text and FLOAT32 vector rankings using server-side FT.HYBRID and reciprocal rank fusion.</summary>
    /// <remarks>Requires Redis Search with FT.HYBRID (Redis 8.4+). Scores are unchanged server RRF scores, higher is better.
    /// The minimum fusion window is 20 and expands to cover Skip + top. ScoreThreshold is a minimum score on the selected page.
    /// Retrieval has the same batching and snapshot limitations as SearchAsync. JSON expression filters remain unsupported.</remarks>
    public async IAsyncEnumerable<VectorSearchResult<TRecord>> HybridSearchAsync<TInput>(TInput searchValue, ICollection<string> keywords, int top,
        HybridSearchOptions<TRecord>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default) where TInput : notnull
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(searchValue);
        ArgumentNullException.ThrowIfNull(keywords);
        cancellationToken.ThrowIfCancellationRequested();
        if (top <= 0) throw new ArgumentOutOfRangeException(nameof(top));
        if (keywords.Count == 0) throw new ArgumentException("At least one nonblank keyword is required.", nameof(keywords));
        foreach (var keyword in keywords) ArgumentException.ThrowIfNullOrWhiteSpace(keyword, nameof(keywords));
        var skip = options?.Skip ?? 0;
        if (skip < 0 || options?.ScoreThreshold is { } threshold && !double.IsFinite(threshold))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (_jsonMapper is not null && options?.Filter is not null)
            throw new NotSupportedException("Expression filters and filtered retrieval are unsupported for JSON storage.");
        var text = SelectText(options?.AdditionalProperty);
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
        var expression = RespireSearchQueryBuilder.Or(keywords.Select(keyword => RespireSearchQueryBuilder.TextField(text.StorageName, keyword)).ToArray());
        var filter = options?.Filter is { } predicate ? new RespireVectorDataFilter<TRecord>(_filterFields).Translate(predicate) : (RespireSearchExpression?)null;
        if (filter is { } prefilter) expression = RespireSearchQueryBuilder.And(expression, prefilter);
        var window = Math.Max(20, checked(skip + top));
        var query = new RespireHybridSearchQuery(expression, vector.StorageName, bytes, window, top)
        {
            Skip = skip, VectorFilter = filter, RrfWindow = window, LoadFields = ["__key", "__score"],
        };
        // The Search wrapper discovers missing commands through COMMAND INFO, and keeps ACL, index and syntax failures distinct.
        var result = await RespireVectorDataOperations.ExecuteAsync(
            _client.Search.HybridSearchAsync(_index, query, cancellationToken), nameof(HybridSearchAsync), Name).ConfigureAwait(false);
        var hits = new List<(string Key, double Score)>();
        foreach (var document in result.Documents)
        {
            var key = SearchKey(document.Id, nameof(HybridSearchAsync));
            var score = RespireVectorDataOperations.ReadHybridScore(document, Name);
            if (options?.ScoreThreshold is { } minimum && score < minimum) continue;
            hits.Add((key, score));
        }
        await foreach (var hit in ReadHitsAsync(hits, options?.IncludeVectors ?? false, cancellationToken).ConfigureAwait(false))
            yield return hit;
    }

    private RespireVectorDataTextField SelectText(Expression<Func<TRecord, object?>>? property)
    {
        if (property is null)
        {
            if (_textFields.Length != 1) throw new InvalidOperationException("Declare one TextFields mapping or select a mapped text property via AdditionalProperty.");
            return _textFields[0];
        }
        var body = property.Body is UnaryExpression { NodeType: ExpressionType.Convert } conversion ? conversion.Operand : property.Body;
        var members = new Stack<string>();
        while (body is MemberExpression member) { members.Push(member.Member.Name); body = member.Expression!; }
        if (body == property.Parameters[0])
        {
            var name = string.Join('.', members);
            return _textFields.FirstOrDefault(field => field.PropertyName == name)
                ?? throw new ArgumentException("The selected property is not a mapped text property.", nameof(property));
        }
        throw new ArgumentException("Select a mapped text property or nested member path.", nameof(property));
    }
}
