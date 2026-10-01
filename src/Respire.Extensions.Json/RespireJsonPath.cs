namespace Respire.Extensions.Json;

/// <summary>A RedisJSON path. The default path selects the legacy root path <c>.</c>.</summary>
public readonly record struct RespireJsonPath
{
    private readonly string? _value;

    /// <summary>Creates a non-empty RedisJSON path.</summary>
    public RespireJsonPath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    /// <summary>Root path using the legacy single-value response shape.</summary>
    public static RespireJsonPath Root => new(".");

    /// <summary>Root path using JSONPath and its aggregate response shape.</summary>
    public static RespireJsonPath JsonPathRoot => new("$");

    /// <summary>Path sent to Redis.</summary>
    public string Value => _value ?? ".";

    /// <summary>Whether this path uses the JSONPath response shape.</summary>
    public bool UsesJsonPath => Value.StartsWith('$');

    /// <summary>Converts a string to a RedisJSON path.</summary>
    public static implicit operator RespireJsonPath(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
