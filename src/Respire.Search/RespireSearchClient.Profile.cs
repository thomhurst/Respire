using Respire.Protocol;

namespace Respire.Search;

public sealed partial class RespireSearchClient
{
    /// <summary>Runs SEARCH with profiling, reusing the existing query and option encoding.</summary>
    /// <remarks>LIMITED omits reader details. Profiling adds server work and is intended for diagnosis.</remarks>
    public async ValueTask<RespireSearchProfileResult<RespireSearchResult>> ProfileSearchAsync(
        string index, RespireSearchQuery query, bool limited = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var options = query.Options ?? RespireSearchQueryOptions.Default;
        using var reply = await _commands.ProfileAsync(RequireName(index), "SEARCH", ProfileOptions(limited),
            [query.Expression.Value, .. options.ToArguments()], cancellationToken).ConfigureAwait(false);
        var parts = ReadProfileEnvelope(reply);
        return new(RespireSearchResult.Parse(parts.Result, options.NoContent, options.WithScores), parts.Profile);
    }

    /// <summary>Runs AGGREGATE with profiling, reusing the existing pipeline and option encoding.</summary>
    /// <remarks>LIMITED omits reader details. This method does not create an aggregation cursor.</remarks>
    public async ValueTask<RespireSearchProfileResult<RespireSearchAggregateResult>> ProfileAggregateAsync(
        string index, RespireSearchExpression expression, RespireSearchAggregateOptions? options = null,
        bool limited = false, CancellationToken cancellationToken = default)
    {
        expression.RequireValid(nameof(expression));
        using var reply = await _commands.ProfileAsync(RequireName(index), "AGGREGATE", ProfileOptions(limited),
            [expression.Value, .. (options ?? RespireSearchAggregateOptions.Default).ToArguments()], cancellationToken).ConfigureAwait(false);
        var parts = ReadProfileEnvelope(reply);
        return new(RespireSearchAggregateResult.Parse(parts.Result), parts.Profile);
    }

    /// <summary>Runs HYBRID with profiling. Requires Redis 8.4 or later with Search.</summary>
    /// <remarks>Uses the existing hybrid encoding. Unsupported commands and other server errors pass through unchanged.</remarks>
    public async ValueTask<RespireSearchProfileResult<RespireSearchResult>> ProfileHybridSearchAsync(
        string index, RespireHybridSearchQuery query, bool limited = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        using var reply = await _commands.ProfileAsync(RequireName(index), "HYBRID", ProfileOptions(limited),
            query.ToArguments(), cancellationToken).ConfigureAwait(false);
        // Redis 8.10 appends the profile to the flat RESP2 hybrid reply, while RESP3 adds a Profile field.
        if (reply.Type == RespDataType.Array && (reply.Count & 1) != 0 && reply.Count >= 3)
            return new(RespireSearchResult.ParseHybrid(reply, reply.Count - 1), RespireSearchProfileNode.Parse(reply[^1]));
        if (reply.Type == RespDataType.Map && TryReadProfileField(reply, "Profile", out var profile) &&
            !TryReadProfileField(reply, "Results", out _))
            return new(RespireSearchResult.ParseHybrid(reply), RespireSearchProfileNode.Parse(profile));
        var parts = ReadProfileEnvelope(reply);
        return new(RespireSearchResult.ParseHybrid(parts.Result), parts.Profile);
    }

    private static string[] ProfileOptions(bool limited) => limited ? ["LIMITED", "QUERY"] : ["QUERY"];

    private static (RespireResult Result, RespireSearchProfileNode Profile) ReadProfileEnvelope(RespireResult reply)
    {
        if (reply.Type == RespDataType.Array && reply.Count == 2)
            return (reply[0], RespireSearchProfileNode.Parse(reply[1]));
        if (reply.Type == RespDataType.Map && TryReadProfileField(reply, "Results", out var result) &&
            TryReadProfileField(reply, "Profile", out var profile))
            return (result, RespireSearchProfileNode.Parse(profile));
        throw RespireSearchReply.Unexpected("FT.PROFILE", "an invalid result/profile envelope");
    }

    private static bool TryReadProfileField(RespireResult reply, string name, out RespireResult value)
    {
        RespireSearchReply.RequirePairs(reply, "FT.PROFILE");
        value = default;
        var found = false;
        for (var i = 0; i < reply.Count; i += 2)
        {
            if (reply[i].IsNull || reply[i].Type is not (RespDataType.BulkString or RespDataType.SimpleString))
                throw RespireSearchReply.Unexpected("FT.PROFILE", "a non-string envelope field");
            if (reply[i].AsString() != name) continue;
            if (found) throw RespireSearchReply.Unexpected("FT.PROFILE", "a duplicate envelope field " + name);
            value = reply[i + 1];
            found = true;
        }
        return found;
    }
}
