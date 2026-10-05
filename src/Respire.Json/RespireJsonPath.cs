namespace Respire.Json;

/// <summary>A RedisJSON path. The default path selects the legacy root path <c>.</c>.</summary>
/// <remarks>
/// The string constructor treats paths starting with <c>$</c> as JSONPath; all others are legacy.
/// Use <see cref="Projection"/> for Redis 8.10 expressions returning an array of scalar results.
/// Use <see cref="DirectArray"/> for collection projections returning one direct array, even with a leading <c>$</c>.
/// Legacy paths return one value. Equality compares the path text and response shape, so
/// <c>default(RespireJsonPath)</c> equals <see cref="Root"/>.
/// </remarks>
public readonly struct RespireJsonPath : IEquatable<RespireJsonPath>
{
    private readonly string? _value;
    private readonly bool _usesJsonPath;

    /// <summary>Creates a non-empty RedisJSON path.</summary>
    /// <exception cref="ArgumentException">The path is null, empty, or whitespace.</exception>
    public RespireJsonPath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
        _usesJsonPath = value.StartsWith('$');
    }

    private RespireJsonPath(string value, bool usesJsonPath) : this(value) => _usesJsonPath = usesJsonPath;

    /// <summary>Declares a single-value reply without modifying the path text.</summary>
    /// <remarks>Overrides decoding without validating syntax, including for <c>$</c> paths. For collection projections, <see cref="DirectArray"/> makes the intent explicit.</remarks>
    public static RespireJsonPath Legacy(string path) => new(path, usesJsonPath: false);

    /// <summary>Declares a projection reply containing one direct JSON array, without a matches wrapper.</summary>
    /// <remarks>
    /// Use array result metadata for expressions such as <c>$.obj.keys()</c> and <c>$.items.append(9)</c>.
    /// This is single-value decoding, equivalent to <see cref="Legacy"/>. It preserves the expression
    /// and does not validate its syntax or result type.
    /// </remarks>
    public static RespireJsonPath DirectArray(string expression) => Legacy(expression);

    /// <summary>Declares an array of matches without modifying the path text.</summary>
    /// <remarks>Overrides decoding without validating syntax, including for paths without <c>$</c>. The caller must select the shape returned by Redis.</remarks>
    public static RespireJsonPath JsonPath(string path) => new(path, usesJsonPath: true);

    /// <summary>Declares array-of-matches decoding for a Redis 8.10 projection expression.</summary>
    /// <remarks>
    /// Use this for expressions such as <c>sum($.items)</c>, <c>($.price + 1)</c>, or
    /// <c>items.sum()</c>. The expression is sent unchanged; Redis validates its syntax.
    /// Collection projections such as <c>$.obj.keys()</c> and <c>$.items.append(9)</c> return a direct
    /// array instead of a matches wrapper. Use <see cref="DirectArray"/> with array metadata for those results.
    /// </remarks>
    public static RespireJsonPath Projection(string expression) => JsonPath(expression);

    /// <summary>Root path using the legacy single-value response shape.</summary>
    public static RespireJsonPath Root => new(".");

    /// <summary>Root path using JSONPath and its aggregate response shape.</summary>
    public static RespireJsonPath JsonPathRoot => new("$");

    /// <summary>Path sent to Redis.</summary>
    public string Value => _value ?? ".";

    /// <summary>Whether this path uses the JSONPath response shape.</summary>
    public bool UsesJsonPath => _usesJsonPath;

    /// <summary>The declared response shape, captured once when the path is constructed.</summary>
    public RespireJsonResponseShape ResponseShape => _usesJsonPath
        ? RespireJsonResponseShape.MatchedValues : RespireJsonResponseShape.SingleValue;

    /// <summary>Creates a non-empty RedisJSON path. Equivalent to the constructor.</summary>
    /// <exception cref="ArgumentException">The path is null, empty, or whitespace.</exception>
    public static RespireJsonPath From(string value) => new(value);

    /// <summary>Converts a string to a RedisJSON path.</summary>
    /// <exception cref="ArgumentException">The path is null, empty, or whitespace.</exception>
    public static implicit operator RespireJsonPath(string value) => new(value);

    /// <summary>Tests whether two paths have the same text and response shape.</summary>
    public static bool operator ==(RespireJsonPath left, RespireJsonPath right) => left.Equals(right);

    /// <summary>Tests whether two paths differ in text or response shape.</summary>
    public static bool operator !=(RespireJsonPath left, RespireJsonPath right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(RespireJsonPath other)
        => string.Equals(Value, other.Value, StringComparison.Ordinal) && UsesJsonPath == other.UsesJsonPath;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RespireJsonPath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(Value), UsesJsonPath);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>How a typed JSON read interprets the outermost serialized value.</summary>
public enum RespireJsonResponseShape
{
    /// <summary>The entire JSON reply is one value, including when that value is itself an array.</summary>
    SingleValue,
    /// <summary>The reply is an array of matched or computed values.</summary>
    MatchedValues,
}
