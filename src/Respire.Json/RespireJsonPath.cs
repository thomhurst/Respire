namespace Respire.Json;

/// <summary>A RedisJSON path. The default path selects the legacy root path <c>.</c>.</summary>
/// <remarks>
/// Paths that start with <c>$</c> use JSONPath and return an array of every match. Other paths use the
/// legacy syntax and return one value. Equality compares the path sent to Redis, so
/// <c>default(RespireJsonPath)</c> equals <see cref="Root"/>.
/// </remarks>
public readonly struct RespireJsonPath : IEquatable<RespireJsonPath>
{
    private readonly string? _value;

    /// <summary>Creates a non-empty RedisJSON path.</summary>
    /// <exception cref="ArgumentException">The path is null, empty, or whitespace.</exception>
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

    /// <summary>Creates a non-empty RedisJSON path. Equivalent to the constructor.</summary>
    /// <exception cref="ArgumentException">The path is null, empty, or whitespace.</exception>
    public static RespireJsonPath From(string value) => new(value);

    /// <summary>Converts a string to a RedisJSON path.</summary>
    /// <exception cref="ArgumentException">The path is null, empty, or whitespace.</exception>
    public static implicit operator RespireJsonPath(string value) => new(value);

    /// <summary>Tests whether two paths send the same value.</summary>
    public static bool operator ==(RespireJsonPath left, RespireJsonPath right) => left.Equals(right);

    /// <summary>Tests whether two paths send different values.</summary>
    public static bool operator !=(RespireJsonPath left, RespireJsonPath right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(RespireJsonPath other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RespireJsonPath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
