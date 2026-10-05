using System.Globalization;
using Respire.Protocol;

namespace Respire.Search;

/// <summary>Options for adding or updating an autocomplete suggestion.</summary>
public sealed record RespireSearchSuggestionAddOptions
{
    /// <summary>Adds to the existing score instead of replacing it.</summary>
    public bool Increment { get; init; }

    /// <summary>Optional binary payload. Null omits PAYLOAD; empty memory sends an empty payload.</summary>
    public ReadOnlyMemory<byte>? Payload { get; init; }
}

/// <summary>Options for retrieving autocomplete suggestions.</summary>
public sealed record RespireSearchSuggestionOptions
{
    /// <summary>Includes prefixes at Levenshtein distance one.</summary>
    public bool Fuzzy { get; init; }

    /// <summary>Requests each suggestion's server-calculated match score.</summary>
    public bool WithScores { get; init; }

    /// <summary>Requests each suggestion's optional binary payload.</summary>
    public bool WithPayloads { get; init; }

    /// <summary>Maximum results. Must be positive; null uses the server default (5).</summary>
    public int? Max { get; init; }
}

/// <summary>An owned autocomplete suggestion. Score is null when not requested; Payload is null when not requested or absent.</summary>
public sealed record RespireSearchSuggestion(string Text, double? Score, ReadOnlyMemory<byte>? Payload);

public sealed partial class RespireSearchClient
{
    /// <summary>Adds or updates a suggestion with FT.SUGADD and returns the dictionary's current entry count.</summary>
    /// <remarks>The dictionary is a Redis key, independent of Search indexes. NaN is rejected; infinite weights are passed to the server. The caller must keep payload memory unchanged until completion.</remarks>
    public async ValueTask<long> AddSuggestionAsync(RespireKey key, string suggestion, double score,
        RespireSearchSuggestionAddOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (double.IsNaN(score)) throw new ArgumentOutOfRangeException(nameof(score), "A suggestion score cannot be NaN.");
        var arguments = new List<RespireValue>(3);
        if (options?.Increment == true) arguments.Add("INCR");
        if (options?.Payload is { } payload)
        {
            arguments.Add("PAYLOAD");
            arguments.Add(payload);
        }
        using var result = await _commands.AddSuggestionAsync(key, suggestion, score, [.. arguments], cancellationToken).ConfigureAwait(false);
        return ReadSuggestionCount(result, "FT.SUGADD");
    }

    /// <summary>Deletes a suggestion with FT.SUGDEL. Returns false when the dictionary or suggestion is absent.</summary>
    public async ValueTask<bool> DeleteSuggestionAsync(RespireKey key, string suggestion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        using var result = await _commands.DeleteSuggestionAsync(key, suggestion, cancellationToken).ConfigureAwait(false);
        return ReadSuggestionCount(result, "FT.SUGDEL") switch
        {
            0 => false,
            1 => true,
            _ => throw RespireSearchReply.Unexpected("FT.SUGDEL", "a deletion flag other than 0 or 1"),
        };
    }

    /// <summary>Returns a suggestion dictionary's entry count with FT.SUGLEN, or zero when absent.</summary>
    public async ValueTask<long> GetSuggestionCountAsync(RespireKey key, CancellationToken cancellationToken = default)
    {
        using var result = await _commands.GetSuggestionCountAsync(key, cancellationToken).ConfigureAwait(false);
        return ReadSuggestionCount(result, "FT.SUGLEN");
    }

    /// <summary>Returns owned autocomplete suggestions from FT.SUGGET. An empty prefix is allowed.</summary>
    /// <remarks>Routes by dictionary key without cross-node fan-out. Key-prefixed views retain the Search command restriction.</remarks>
    public async ValueTask<IReadOnlyList<RespireSearchSuggestion>> GetSuggestionsAsync(RespireKey key, string prefix,
        RespireSearchSuggestionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (options?.Max is <= 0) throw new ArgumentOutOfRangeException(nameof(options), options.Max, "Max must be positive.");
        var withScores = options?.WithScores == true;
        var withPayloads = options?.WithPayloads == true;
        var arguments = new List<RespireValue>(5);
        if (options?.Fuzzy == true) arguments.Add("FUZZY");
        if (withScores) arguments.Add("WITHSCORES");
        if (withPayloads) arguments.Add("WITHPAYLOADS");
        if (options?.Max is { } max)
        {
            arguments.Add("MAX");
            arguments.Add(max);
        }
        using var result = await _commands.GetSuggestionsAsync(key, prefix, [.. arguments], cancellationToken).ConfigureAwait(false);
        if (result.Type != RespDataType.Array || result.IsNull)
            throw RespireSearchReply.Unexpected("FT.SUGGET", "an array was expected");
        var width = 1 + (withScores ? 1 : 0) + (withPayloads ? 1 : 0);
        if (result.Count % width != 0)
            throw RespireSearchReply.Unexpected("FT.SUGGET", "an incomplete suggestion entry");
        var suggestions = new RespireSearchSuggestion[result.Count / width];
        for (var i = 0; i < suggestions.Length; i++)
        {
            var offset = i * width;
            var text = ReadSuggestionString(result[offset++]).AsString();
            double? score = withScores ? ReadSuggestionScore(result[offset++]) : null;
            ReadOnlyMemory<byte>? payload = null;
            if (withPayloads && !result[offset].IsNull)
                payload = ReadSuggestionString(result[offset]).AsBytes();
            suggestions[i] = new(text, score, payload);
        }
        return suggestions;
    }

    private static long ReadSuggestionCount(RespireResult result, string command)
    {
        if (result.Type != RespDataType.Integer || result.AsInteger() < 0)
            throw RespireSearchReply.Unexpected(command, "a nonnegative integer was expected");
        return result.AsInteger();
    }

    private static RespireResult ReadSuggestionString(RespireResult result)
    {
        if (result.IsNull || result.Type is not (RespDataType.BulkString or RespDataType.SimpleString))
            throw RespireSearchReply.Unexpected("FT.SUGGET", "a string was expected");
        return result;
    }

    private static double ReadSuggestionScore(RespireResult result)
    {
        if (result.Type == RespDataType.Double) return result.AsDouble();
        var text = ReadSuggestionString(result).AsString();
        if (text.Equals("inf", StringComparison.OrdinalIgnoreCase) || text.Equals("+inf", StringComparison.OrdinalIgnoreCase))
            return double.PositiveInfinity;
        if (text.Equals("-inf", StringComparison.OrdinalIgnoreCase)) return double.NegativeInfinity;
        if (text.Equals("nan", StringComparison.OrdinalIgnoreCase)) return double.NaN;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw RespireSearchReply.Unexpected("FT.SUGGET", "a numeric score was expected");
    }
}
