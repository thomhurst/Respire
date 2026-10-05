namespace Respire.Json;

/// <summary>A RedisJSON path. The default path selects the legacy root path <c>.</c>.</summary>
/// <remarks>
/// Paths that start with <c>$</c>, prefix functions, and grouped or rooted unary expressions use
/// array replies. Use <see cref="Projection"/> for other Redis 8.10 projection expressions.
/// Legacy paths return one value. Equality compares the path text and response shape, so
/// <c>default(RespireJsonPath)</c> equals <see cref="Root"/>.
/// </remarks>
public readonly struct RespireJsonPath : IEquatable<RespireJsonPath>
{
    private readonly string? _value;
    private readonly bool _projection;

    /// <summary>Creates a non-empty RedisJSON path.</summary>
    /// <exception cref="ArgumentException">The path is null, empty, or whitespace.</exception>
    public RespireJsonPath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    private RespireJsonPath(string value, bool projection) : this(value) => _projection = projection;

    /// <summary>Creates a Redis 8.10 projection expression with an array response shape.</summary>
    /// <remarks>
    /// Use this for expressions written in legacy notation, such as <c>items.sum()</c> or
    /// <c>price + 1</c>. The expression is sent unchanged; Redis validates its syntax.
    /// </remarks>
    public static RespireJsonPath Projection(string expression) => new(expression, projection: true);

    /// <summary>Root path using the legacy single-value response shape.</summary>
    public static RespireJsonPath Root => new(".");

    /// <summary>Root path using JSONPath and its aggregate response shape.</summary>
    public static RespireJsonPath JsonPathRoot => new("$");

    /// <summary>Path sent to Redis.</summary>
    public string Value => _value ?? ".";

    /// <summary>Whether this path uses the JSONPath response shape.</summary>
    public bool UsesJsonPath
    {
        get
        {
            if (_projection) return true;
            var path = Value.AsSpan();
            if (path[0] is '$' or '(') return true;
            if (path[0] is '+' or '-')
            {
                path = path[1..].TrimStart(' ');
                return !path.IsEmpty && path[0] is '$' or '(';
            }

            // Redis 8.10 prefix projections such as sum($.items) also wrap their result in an array.
            if (!char.IsAsciiLetter(path[0]) && path[0] != '_') return false;
            var index = 1;
            while (index < path.Length && (char.IsAsciiLetterOrDigit(path[index]) || path[index] == '_')) index++;
            path = path[index..].TrimStart(' ');
            return !path.IsEmpty && path[0] == '(';
        }
    }

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
