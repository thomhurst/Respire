using System.Globalization;
using Respire.Protocol;

namespace Respire.Search;

/// <summary>Options for FT.SPELLCHECK.</summary>
public sealed record RespireSearchSpellCheckOptions
{
    /// <summary>Maximum Levenshtein distance, from 1 through 4. Null uses the server default (1).</summary>
    public int? Distance { get; init; }

    /// <summary>Dictionaries whose terms supply additional suggestions.</summary>
    public IReadOnlyList<string> IncludeDictionaries { get; init; } = [];

    /// <summary>Dictionaries whose matching query terms are excluded from spellchecking.</summary>
    public IReadOnlyList<string> ExcludeDictionaries { get; init; } = [];

    /// <summary>Query dialect, from 1 through 4 on Redis 8.10. Null uses the server default. Requires Search 2.4.3 or later.</summary>
    public int? Dialect { get; init; }

    internal RespireValue[] ToArguments()
    {
        if (Distance is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(Distance), Distance, "Distance must be between 1 and 4.");
        if (Dialect is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(Dialect), Dialect, "Dialect must be between 1 and 4.");
        ArgumentNullException.ThrowIfNull(IncludeDictionaries);
        ArgumentNullException.ThrowIfNull(ExcludeDictionaries);
        var count = checked(3 * (IncludeDictionaries.Count + ExcludeDictionaries.Count)
            + (Distance.HasValue ? 2 : 0) + (Dialect.HasValue ? 2 : 0));
        if (count == 0) return [];
        var arguments = new RespireValue[count];
        var offset = 0;
        if (Distance is { } distance)
        {
            arguments[offset++] = "DISTANCE";
            arguments[offset++] = distance;
        }
        AddDictionaries(IncludeDictionaries, "INCLUDE", nameof(IncludeDictionaries));
        AddDictionaries(ExcludeDictionaries, "EXCLUDE", nameof(ExcludeDictionaries));
        if (Dialect is { } dialect)
        {
            arguments[offset++] = "DIALECT";
            arguments[offset] = dialect;
        }
        return arguments;

        void AddDictionaries(IReadOnlyList<string> dictionaries, string mode, string parameter)
        {
            foreach (var dictionary in dictionaries)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(dictionary, parameter);
                arguments[offset++] = "TERMS";
                arguments[offset++] = mode;
                arguments[offset++] = dictionary;
            }
        }
    }
}

/// <summary>An owned spelling suggestion and its server-calculated score.</summary>
public sealed record RespireSearchSpellingSuggestion(string Term, double Score);

/// <summary>An owned misspelled query term and its associated suggestions, which may be empty.</summary>
public sealed record RespireSearchSpellingCorrection(string Term, IReadOnlyList<RespireSearchSpellingSuggestion> Suggestions);

public sealed partial class RespireSearchClient
{
    /// <summary>Adds dictionary terms with FT.DICTADD and returns the number newly inserted.</summary>
    /// <remarks>Search 1.4 or later. Dictionaries are independent of indexes; commands route by dictionary name without fan-out.</remarks>
    public async ValueTask<long> AddDictionaryTermsAsync(string dictionary, IReadOnlyList<string> terms, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionary);
        using var result = await _commands.DictionaryAddAsync(dictionary, CopyDictionaryTerms(terms), cancellationToken).ConfigureAwait(false);
        return ReadSuggestionCount(result, "FT.DICTADD");
    }

    /// <summary>Deletes dictionary terms with FT.DICTDEL and returns the number removed.</summary>
    public async ValueTask<long> DeleteDictionaryTermsAsync(string dictionary, IReadOnlyList<string> terms, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionary);
        using var result = await _commands.DictionaryDeleteAsync(dictionary, CopyDictionaryTerms(terms), cancellationToken).ConfigureAwait(false);
        return ReadSuggestionCount(result, "FT.DICTDEL");
    }

    /// <summary>Returns owned dictionary terms with FT.DICTDUMP. Term order is unspecified.</summary>
    public async ValueTask<IReadOnlyList<string>> DumpDictionaryAsync(string dictionary, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionary);
        using var result = await _commands.DictionaryDumpAsync(dictionary, cancellationToken).ConfigureAwait(false);
        return RespireSearchReply.ReadStringCollection(result, "FT.DICTDUMP");
    }

    /// <summary>Returns owned spelling corrections with FT.SPELLCHECK (Search 1.4 or later).</summary>
    /// <remarks>Routes by index name and relies on the server coordinator for cross-shard search. Dictionary names must be available on the receiving server. Key-prefixed views reject these commands.</remarks>
    public async ValueTask<IReadOnlyList<RespireSearchSpellingCorrection>> SpellCheckAsync(string index, string query,
        RespireSearchSpellCheckOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        ArgumentNullException.ThrowIfNull(query);
        using var result = await _commands.SpellCheckAsync(index, query, options?.ToArguments() ?? [], cancellationToken).ConfigureAwait(false);
        return ParseSpellCheck(result);
    }

    private static string[] CopyDictionaryTerms(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count == 0) throw new ArgumentException("At least one dictionary term is required.", nameof(terms));
        var copy = new string[terms.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            ArgumentException.ThrowIfNullOrEmpty(terms[i], nameof(terms));
            copy[i] = terms[i];
        }
        return copy;
    }

    private static IReadOnlyList<RespireSearchSpellingCorrection> ParseSpellCheck(RespireResult result)
        => result.Type == RespDataType.Map ? ParseSpellCheckResp3(result) : ParseSpellCheckResp2(result);

    private static RespireSearchSpellingCorrection[] ParseSpellCheckResp2(RespireResult result)
    {
        const string command = "FT.SPELLCHECK";
        RequireSpellArray(result, command);
        var corrections = new RespireSearchSpellingCorrection[result.Count];
        for (var i = 0; i < corrections.Length; i++)
        {
            var entry = result[i];
            RequireSpellArray(entry, command);
            if (entry.Count != 3 || RespireSearchReply.ReadString(entry[0], command) != "TERM")
                throw RespireSearchReply.Unexpected(command, "a three-element TERM entry was expected");
            corrections[i] = new(RespireSearchReply.ReadString(entry[1], command),
                ReadSpellingSuggestions(entry[2], RespDataType.Array, termIndex: 1, scoreIndex: 0));
        }
        return corrections;
    }

    private static RespireSearchSpellingCorrection[] ParseSpellCheckResp3(RespireResult result)
    {
        const string command = "FT.SPELLCHECK";
        // Redis 8.10 wraps a term-to-suggestions map in a single "results" member.
        if (result.Count != 2 || RespireSearchReply.ReadString(result[0], command) != "results")
            throw RespireSearchReply.Unexpected(command, "a results map was expected");
        var terms = result[1];
        if (terms.Type != RespDataType.Map || (terms.Count & 1) != 0)
            throw RespireSearchReply.Unexpected(command, "a term map was expected");
        var corrections = new RespireSearchSpellingCorrection[terms.Count / 2];
        for (var i = 0; i < corrections.Length; i++)
        {
            corrections[i] = new(RespireSearchReply.ReadString(terms[i * 2], command),
                ReadSpellingSuggestions(terms[i * 2 + 1], RespDataType.Map, termIndex: 0, scoreIndex: 1));
        }
        return corrections;
    }

    private static RespireSearchSpellingSuggestion[] ReadSpellingSuggestions(
        RespireResult result, RespDataType pairType, int termIndex, int scoreIndex)
    {
        const string command = "FT.SPELLCHECK";
        RequireSpellArray(result, command);
        var suggestions = new RespireSearchSpellingSuggestion[result.Count];
        for (var i = 0; i < suggestions.Length; i++)
        {
            var pair = result[i];
            if (pair.Type != pairType || pair.IsNull || pair.Count != 2)
                throw RespireSearchReply.Unexpected(command, "a suggestion/score pair was expected");
            suggestions[i] = new(RespireSearchReply.ReadString(pair[termIndex], command), ReadSpellingScore(pair[scoreIndex]));
        }
        return suggestions;
    }

    private static double ReadSpellingScore(RespireResult value)
    {
        const string command = "FT.SPELLCHECK";
        double score;
        if (value.Type == RespDataType.Double) score = value.AsDouble();
        else if (!double.TryParse(RespireSearchReply.ReadString(value, command), NumberStyles.Float, CultureInfo.InvariantCulture, out score))
            throw RespireSearchReply.Unexpected(command, "a numeric score was expected");
        if (!double.IsFinite(score) || score < 0)
            throw RespireSearchReply.Unexpected(command, "a finite nonnegative score was expected");
        return score;
    }

    private static void RequireSpellArray(RespireResult value, string command)
    {
        if (value.Type != RespDataType.Array || value.IsNull)
            throw RespireSearchReply.Unexpected(command, "an array was expected");
    }
}
