using System.Globalization;

namespace Respire.Search;

/// <summary>A generated field selector for one document's query.</summary>
public class RespireSearchQueryField<TFields>
{
    /// <summary>Creates a selector from schema metadata.</summary>
    public RespireSearchQueryField(RespireSearchField definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
    }

    /// <summary>The indexed field metadata.</summary>
    public RespireSearchField Definition { get; }

    /// <summary>The query alias, independent of the hash field or JSON path.</summary>
    public string Name => Definition.Alias ?? Definition.Identifier;

    internal void RequireIndexed()
    {
        if (Definition.NoIndex) throw new InvalidOperationException($"Field '{Name}' is not indexed.");
    }
}

/// <summary>A TEXT field with escaped phrase matching.</summary>
public sealed class RespireSearchTextField<TFields>(RespireSearchField definition) : RespireSearchQueryField<TFields>(definition)
{
    /// <summary>Matches a quoted text term or phrase using the index's stemming and language rules.</summary>
    public RespireSearchExpression Matches(string text)
    {
        RequireIndexed();
        return RespireSearchQueryBuilder.TextField(Name, text);
    }
}

/// <summary>A TAG field with escaped value matching.</summary>
public sealed class RespireSearchTagField<TFields>(RespireSearchField definition) : RespireSearchQueryField<TFields>(definition)
{
    /// <summary>Matches a tag using the index's case-sensitivity rules.</summary>
    public RespireSearchExpression EqualTo(string value)
    {
        RequireIndexed();
        return RespireSearchQueryBuilder.Tag(Name, value);
    }
}

/// <summary>A NUMERIC field accepting the mapped property's non-nullable CLR type.</summary>
/// <remarks>Redis stores numeric fields as doubles; server precision limits still apply.</remarks>
public sealed class RespireSearchNumericField<TFields, TValue>(RespireSearchField definition) : RespireSearchQueryField<TFields>(definition)
    where TValue : struct, IComparable<TValue>, IFormattable
{
    /// <summary>Matches an inclusive range.</summary>
    public RespireSearchExpression Between(TValue minimum, TValue maximum)
    {
        if (minimum.CompareTo(maximum) > 0) throw new ArgumentOutOfRangeException(nameof(minimum));
        return Range(Format(minimum), Format(maximum));
    }

    /// <summary>Matches a numeric value.</summary>
    public RespireSearchExpression EqualTo(TValue value) => Between(value, value);

    /// <summary>Matches values strictly greater than the bound.</summary>
    public RespireSearchExpression GreaterThan(TValue value) => Range("(" + Format(value), "+inf");

    /// <summary>Matches values greater than or equal to the bound.</summary>
    public RespireSearchExpression GreaterThanOrEqualTo(TValue value) => Range(Format(value), "+inf");

    /// <summary>Matches values strictly less than the bound.</summary>
    public RespireSearchExpression LessThan(TValue value) => Range("-inf", "(" + Format(value));

    /// <summary>Matches values less than or equal to the bound.</summary>
    public RespireSearchExpression LessThanOrEqualTo(TValue value) => Range("-inf", Format(value));

    /// <summary>Builds a strictly greater-than predicate.</summary>
    public static RespireSearchExpression operator >(RespireSearchNumericField<TFields, TValue> field, TValue value)
        => field.GreaterThan(value);

    /// <summary>Builds a strictly less-than predicate.</summary>
    public static RespireSearchExpression operator <(RespireSearchNumericField<TFields, TValue> field, TValue value)
        => field.LessThan(value);

    /// <summary>Builds an inclusive lower-bound predicate.</summary>
    public static RespireSearchExpression operator >=(RespireSearchNumericField<TFields, TValue> field, TValue value)
        => field.GreaterThanOrEqualTo(value);

    /// <summary>Builds an inclusive upper-bound predicate.</summary>
    public static RespireSearchExpression operator <=(RespireSearchNumericField<TFields, TValue> field, TValue value)
        => field.LessThanOrEqualTo(value);

    private RespireSearchExpression Range(string minimum, string maximum)
    {
        RequireIndexed();
        return RespireSearchExpression.FromBuilder($"@{RespireSearchQueryBuilder.EscapeField(Name)}:[{minimum} {maximum}]");
    }

    private static string Format(TValue value)
    {
        var text = value.ToString(null, CultureInfo.InvariantCulture);
        return text switch
        {
            "NaN" => throw new ArgumentOutOfRangeException(nameof(value)),
            "Infinity" => "+inf",
            "-Infinity" => "-inf",
            _ => text.Replace("E+", "E", StringComparison.Ordinal),
        };
    }
}

/// <summary>A VECTOR field with generated dimension metadata.</summary>
public sealed class RespireSearchVectorField<TFields>(RespireSearchField definition) : RespireSearchQueryField<TFields>(definition)
{
    internal RespireVectorSearchRequest CreateRequest(ReadOnlyMemory<byte> vector, int count, RespireSearchExpression? filter)
    {
        RespireSearchVectorValidation.ValidateHashLength(vector.Length, Definition.Vector!);
        return new(Name, vector, count) { Filter = filter };
    }
}
