namespace Respire.Extensions.Json;

/// <summary>Formatting options and paths for JSON.GET.</summary>
public sealed class RespireJsonGetOptions
{
    private readonly IReadOnlyList<RespireJsonPath> _paths = [RespireJsonPath.Root];

    /// <summary>Paths to return. Defaults to the legacy root path <c>.</c>.</summary>
    /// <remarks>Do not mix legacy and JSONPath (<c>$</c>) paths; Redis rejects the request.</remarks>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="ArgumentException">The value is empty.</exception>
    public IReadOnlyList<RespireJsonPath> Paths
    {
        get => _paths;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Count == 0) throw new ArgumentException("At least one JSON path is required.", nameof(Paths));
            _paths = value;
        }
    }

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
