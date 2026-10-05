using Respire.Protocol;

namespace Respire.Search;

public sealed partial class RespireSearchClient
{
    /// <summary>Creates or extends a synonym group with FT.SYNUPDATE. Existing members are retained.</summary>
    /// <param name="index">Search index name.</param>
    /// <param name="groupId">Synonym group identifier.</param>
    /// <param name="terms">Nonempty terms, copied before sending. Without skipInitialScan, the first term cannot be SKIPINITIALSCAN.</param>
    /// <param name="skipInitialScan">Skips reindexing existing documents; only subsequently indexed documents get the new synonym mappings.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    public async ValueTask UpdateSynonymsAsync(string index, string groupId, IReadOnlyList<string> terms,
        bool skipInitialScan = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(terms);
        var count = terms.Count;
        if (count == 0) throw new ArgumentException("At least one synonym term is required.", nameof(terms));
        var offset = skipInitialScan ? 1 : 0;
        var arguments = new string[checked(count + offset)];
        if (skipInitialScan) arguments[0] = "SKIPINITIALSCAN";
        for (var i = 0; i < count; i++)
        {
            var term = terms[i];
            if (string.IsNullOrWhiteSpace(term)) throw new ArgumentException("Synonym terms cannot be null or whitespace.", nameof(terms));
            arguments[i + offset] = term;
        }
        if (!skipInitialScan && arguments[0].Equals("SKIPINITIALSCAN", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Place the literal SKIPINITIALSCAN term after another term, or explicitly enable skipInitialScan.", nameof(terms));
        using var result = await _commands.UpdateSynonymsAsync(index, groupId, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns an owned mapping of each synonym term to all its group IDs with FT.SYNDUMP.</summary>
    public async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetSynonymsAsync(string index, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        using var result = await _commands.GetSynonymsAsync(index, cancellationToken).ConfigureAwait(false);
        const string command = "FT.SYNDUMP";
        if (result.IsNull || result.Type is not (RespDataType.Array or RespDataType.Map) || (result.Count & 1) != 0)
            throw RespireSearchReply.Unexpected(command, "term/group pairs were expected");
        var terms = new Dictionary<string, IReadOnlyList<string>>(result.Count / 2, StringComparer.Ordinal);
        for (var i = 0; i < result.Count; i += 2)
        {
            var term = RespireSearchReply.ReadString(result[i], command);
            var groups = RespireSearchReply.ReadStringCollection(result[i + 1], command);
            if (!terms.TryAdd(term, groups))
                throw RespireSearchReply.Unexpected(command, "a duplicate synonym term");
        }
        return terms;
    }

    /// <summary>Returns owned distinct values indexed in a TAG field with FT.TAGVALS, without paging or sorting.</summary>
    /// <remarks>Values retain the server's normalization and order. Redis documents FT.TAGVALS as deprecated; Redis 8.10 still supports it.</remarks>
    public async ValueTask<IReadOnlyList<string>> GetTagValuesAsync(string index, string field, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        using var result = await _commands.GetTagValuesAsync(index, field, cancellationToken).ConfigureAwait(false);
        return RespireSearchReply.ReadStringCollection(result, "FT.TAGVALS");
    }
}
