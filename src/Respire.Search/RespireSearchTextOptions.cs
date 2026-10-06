namespace Respire.Search;

/// <summary>HIGHLIGHT modifiers. Returned markup is server-generated and is not HTML-escaped.</summary>
public sealed record RespireSearchHighlightOptions
{
    /// <summary>Text fields to highlight. Empty uses all returned text fields.</summary>
    public IReadOnlyList<string> Fields
    {
        get;
        init => field = RespireSearchOptionArguments.SnapshotFields(value);
    } = [];

    /// <summary>Opening and closing tags. Null uses the server's default tags.</summary>
    public (string Open, string Close)? Tags { get; init; }

    internal void AddArguments(List<RespireValue> args)
    {
        args.Add("HIGHLIGHT");
        RespireSearchOptionArguments.AddFields(args, "FIELDS", Fields);
        if (Tags is { } tags)
        {
            ArgumentNullException.ThrowIfNull(tags.Open);
            ArgumentNullException.ThrowIfNull(tags.Close);
            args.Add("TAGS");
            args.Add(tags.Open);
            args.Add(tags.Close);
        }
    }
}

/// <summary>SUMMARIZE modifiers. The returned string retains the server's fragment separators.</summary>
public sealed record RespireSearchSummaryOptions
{
    /// <summary>Text fields to summarize. Empty uses all returned text fields.</summary>
    public IReadOnlyList<string> Fields
    {
        get;
        init => field = RespireSearchOptionArguments.SnapshotFields(value);
    } = [];

    /// <summary>Maximum fragment count. Null uses the server default.</summary>
    public int? Fragments { get; init; }

    /// <summary>Target fragment length in words. Null uses the server default.</summary>
    public int? Length { get; init; }

    /// <summary>Fragment separator. Null uses the server default; an empty separator is allowed.</summary>
    public string? Separator { get; init; }

    internal void AddArguments(List<RespireValue> args)
    {
        if (Fragments is <= 0) throw new ArgumentOutOfRangeException(nameof(Fragments));
        if (Length is <= 0) throw new ArgumentOutOfRangeException(nameof(Length));
        args.Add("SUMMARIZE");
        RespireSearchOptionArguments.AddFields(args, "FIELDS", Fields);
        if (Fragments is { } fragments)
        {
            args.Add("FRAGS");
            args.Add(fragments);
        }
        if (Length is { } length)
        {
            args.Add("LEN");
            args.Add(length);
        }
        if (Separator is not null)
        {
            args.Add("SEPARATOR");
            args.Add(Separator);
        }
    }
}

internal static class RespireSearchOptionArguments
{
    internal static IReadOnlyList<string> SnapshotFields(IReadOnlyList<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return Array.AsReadOnly(fields.ToArray());
    }

    internal static void AddFields(List<RespireValue> args, string token, IReadOnlyList<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count == 0) return;
        args.Add(token);
        args.Add(fields.Count);
        foreach (var field in fields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field, nameof(fields));
            args.Add(field);
        }
    }

    internal static void AddText(List<RespireValue> args, string token, string? value)
    {
        if (value is null) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        args.Add(token);
        args.Add(value);
    }
}
