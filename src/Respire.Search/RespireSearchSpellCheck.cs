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
        var arguments = new List<RespireValue>();
        if (Distance is { } distance) { arguments.Add("DISTANCE"); arguments.Add(distance); }
        AddDictionaries(IncludeDictionaries, "INCLUDE", nameof(IncludeDictionaries));
        AddDictionaries(ExcludeDictionaries, "EXCLUDE", nameof(ExcludeDictionaries));
        if (Dialect is { } dialect) { arguments.Add("DIALECT"); arguments.Add(dialect); }
        return [.. arguments];

        void AddDictionaries(IReadOnlyList<string> dictionaries, string mode, string parameter)
        {
            ArgumentNullException.ThrowIfNull(dictionaries, parameter);
            foreach (var dictionary in dictionaries)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(dictionary, parameter);
                arguments.Add("TERMS"); arguments.Add(mode); arguments.Add(dictionary);
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
        if (result.IsNull || result.Type is not (RespDataType.Array or RespDataType.Set))
            throw RespireSearchReply.Unexpected("FT.DICTDUMP", "an array or set was expected");
        var terms = new string[result.Count];
        for (var i = 0; i < terms.Length; i++) terms[i] = ReadSpellString(result[i], "FT.DICTDUMP");
        return terms;
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
    {
        const string command = "FT.SPELLCHECK";
        var resp3 = result.Type == RespDataType.Map;
        if (resp3)
        {
            // Redis 8.10 wraps a term-to-suggestions map in a single "results" member.
            if (result.Count != 2 || ReadSpellString(result[0], command) != "results")
                throw RespireSearchReply.Unexpected(command, "a results map was expected");
            result = result[1];
            if (result.Type != RespDataType.Map || (result.Count & 1) != 0)
                throw RespireSearchReply.Unexpected(command, "a term map was expected");
        }
        else RequireSpellArray(result, command);

        var corrections = new RespireSearchSpellingCorrection[resp3 ? result.Count / 2 : result.Count];
        for (var i = 0; i < corrections.Length; i++)
        {
            string term;
            RespireResult suggestions;
            if (resp3)
            {
                term = ReadSpellString(result[i * 2], command);
                suggestions = result[i * 2 + 1];
            }
            else
            {
                var entry = result[i];
                RequireSpellArray(entry, command);
                if (entry.Count != 3 || ReadSpellString(entry[0], command) != "TERM")
                    throw RespireSearchReply.Unexpected(command, "a three-element TERM entry was expected");
                term = ReadSpellString(entry[1], command);
                suggestions = entry[2];
            }
            RequireSpellArray(suggestions, command);
            var owned = new RespireSearchSpellingSuggestion[suggestions.Count];
            for (var j = 0; j < owned.Length; j++)
            {
                var pair = suggestions[j];
                if (pair.Type != (resp3 ? RespDataType.Map : RespDataType.Array) || pair.IsNull || pair.Count != 2)
                    throw RespireSearchReply.Unexpected(command, "a suggestion/score pair was expected");
                var text = ReadSpellString(pair[resp3 ? 0 : 1], command);
                var scoreValue = pair[resp3 ? 1 : 0];
                double score;
                if (scoreValue.Type == RespDataType.Double) score = scoreValue.AsDouble();
                else if (!double.TryParse(ReadSpellString(scoreValue, command), NumberStyles.Float, CultureInfo.InvariantCulture, out score))
                    throw RespireSearchReply.Unexpected(command, "a numeric score was expected");
                if (!double.IsFinite(score) || score < 0)
                    throw RespireSearchReply.Unexpected(command, "a finite nonnegative score was expected");
                owned[j] = new(text, score);
            }
            corrections[i] = new(term, owned);
        }
        return corrections;
    }

    private static void RequireSpellArray(RespireResult value, string command)
    {
        if (value.Type != RespDataType.Array || value.IsNull)
            throw RespireSearchReply.Unexpected(command, "an array was expected");
    }

    private static string ReadSpellString(RespireResult value, string command)
    {
        if (value.IsNull || value.Type is not (RespDataType.BulkString or RespDataType.SimpleString))
            throw RespireSearchReply.Unexpected(command, "a string was expected");
        return value.AsString();
    }
}
