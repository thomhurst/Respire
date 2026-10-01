namespace Respire.Extensions.Json;

/// <summary>Formatting options and paths for JSON.GET.</summary>
public sealed class RespireJsonGetOptions
{
    /// <summary>Paths to return. Defaults to the legacy root path <c>.</c>.</summary>
    public IReadOnlyList<RespireJsonPath> Paths { get; init; } = [RespireJsonPath.Root];

    /// <summary>Optional indentation string.</summary>
    public string? Indent { get; init; }

    /// <summary>Optional newline string.</summary>
    public string? NewLine { get; init; }

    /// <summary>Optional space string.</summary>
    public string? Space { get; init; }

    /// <summary>Requests unescaped output.</summary>
    public bool NoEscape { get; init; }

    internal string[] ToArguments()
    {
        ArgumentNullException.ThrowIfNull(Paths);
        if (Paths.Count == 0) throw new ArgumentException("At least one JSON path is required.", nameof(Paths));

        var arguments = new List<string>(Paths.Count + 6);
        Add("INDENT", Indent);
        Add("NEWLINE", NewLine);
        Add("SPACE", Space);
        if (NoEscape) arguments.Add("NOESCAPE");
        foreach (var path in Paths) arguments.Add(path.Value);
        return arguments.ToArray();

        void Add(string option, string? value)
        {
            if (value is null) return;
            arguments.Add(option);
            arguments.Add(value);
        }
    }
}
