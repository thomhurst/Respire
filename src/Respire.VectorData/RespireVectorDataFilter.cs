using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Respire.Search;

namespace Respire.VectorData;

// Expression metadata identifies properties; only explicit mapper declarations select storage fields.
// Captured FieldInfo values are already rooted by the expression. Record members are never invoked.
internal sealed class RespireVectorDataFilter<TRecord>(IReadOnlyList<RespireVectorDataFilterField> fields) where TRecord : class
{
    // A contradiction remains empty even if a mapper indexes this text token.
    private const string None = "(__respire_filter_none__ -__respire_filter_none__)";
    private ParameterExpression _record = null!;

    internal RespireSearchExpression Translate(Expression<Func<TRecord, bool>> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _record = filter.Parameters[0];
        return RespireSearchExpression.FromRaw(Visit(filter.Body));
    }

    private string Visit(Expression expression)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Method is { } method && !(method.DeclaringType == typeof(string) && method.Name is "op_Equality" or "op_Inequality"))
                throw Unsupported(expression, "Custom operators are unsupported.");
            if (binary.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
                return Combine(Visit(binary.Left), Visit(binary.Right), binary.NodeType == ExpressionType.AndAlso);
            if (binary.NodeType is ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual)
            {
                if (TryField(binary.Left, out var field, out var type))
                    return Compare(field, type, binary.NodeType, Value(binary.Right));
                if (TryField(binary.Right, out field, out type))
                    return Compare(field, type, Reverse(binary.NodeType), Value(binary.Left));
            }
        }
        if (expression is UnaryExpression { NodeType: ExpressionType.Not, Method: null } not) return Negate(Visit(not.Operand));
        if (TryField(expression, out var boolean, out var booleanType) && booleanType == typeof(bool))
            return Compare(boolean, booleanType, ExpressionType.Equal, true);
        if (expression is MethodCallExpression call)
        {
            if (Contains(call, out var source, out var item))
            {
                if (TryField(source, out var field, out var type))
                {
                    ValidateCollection(field, type);
                    return Tag(field, Value(item) as string ?? throw Unsupported(item, "Collection membership requires a nonnull string."));
                }
                if (TryField(item, out field, out type)) return In(field, type, source);
            }
            if (call.Method.DeclaringType == typeof(Enumerable) && call.Method.Name == nameof(Enumerable.Any) && call.Arguments.Count == 2
                && TryField(call.Arguments[0], out var collection, out var collectionType)
                && call.Arguments[1] is LambdaExpression lambda
                && lambda.Body is MethodCallExpression inner && Contains(inner, out var values, out var element)
                && element == lambda.Parameters[0])
            {
                ValidateCollection(collection, collectionType);
                return In(collection, typeof(string), values, membership: true);
            }
        }
        if (expression is ConstantExpression { Value: bool constant }) return constant ? "*" : None;
        throw Unsupported(expression);
    }

    private static string In(RespireVectorDataFilterField field, Type type, Expression source, bool membership = false)
    {
        var value = Value(UnwrapSpan(source));
        // Never enumerate arbitrary user IEnumerable implementations during translation.
        if (value is not Array && (value is null || !SupportedList(value.GetType())))
            throw Unsupported(source, "Membership values must be an array or a supported List<T>.");
        var alternatives = new List<string>();
        foreach (var item in (IEnumerable)value)
            alternatives.Add(membership ? Tag(field, item as string ?? throw Unsupported(source, "Null collection elements are unsupported.")) : Compare(field, type, ExpressionType.Equal, item));
        return alternatives.Count == 0 ? None : "(" + string.Join(" | ", alternatives) + ")";
    }

    private static string Compare(RespireVectorDataFilterField field, Type type, ExpressionType operation, object? value)
    {
        var nullable = !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (field.Kind == RespireVectorDataFilterKind.StringCollection) throw new NotSupportedException("Collection fields support membership only.");
        ValidateType(field, type);
        if (value is null)
        {
            var missing = nullable ? $"ismissing(@{field.StorageName})" : None;
            return operation switch
            {
                ExpressionType.Equal => missing,
                ExpressionType.NotEqual => Negate(missing),
                _ => None,
            };
        }
        string equality;
        if (field.Kind == RespireVectorDataFilterKind.String)
        {
            if (operation is not ExpressionType.Equal and not ExpressionType.NotEqual || value is not string text)
                throw new NotSupportedException("String filters support ordinal equality/inequality only.");
            equality = Tag(field, text);
        }
        else
        {
            double number;
            if (field.Kind == RespireVectorDataFilterKind.Boolean)
            {
                if (value is not bool boolean || operation is not ExpressionType.Equal and not ExpressionType.NotEqual)
                    throw new NotSupportedException("Boolean filters support equality/inequality only.");
                number = boolean ? 1 : 0;
            }
            else
            {
                if (!NumericType(value.GetType())) throw new NotSupportedException("Numeric filters require supported finite numeric constants.");
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (!double.IsFinite(number)) throw new NotSupportedException("Numeric filter constants must be finite.");
            }
            var query = operation switch
            {
                ExpressionType.LessThan => RespireSearchQueryBuilder.NumericRange(field.StorageName, double.NegativeInfinity, number, exclusiveMaximum: true),
                ExpressionType.LessThanOrEqual => RespireSearchQueryBuilder.NumericRange(field.StorageName, double.NegativeInfinity, number),
                ExpressionType.GreaterThan => RespireSearchQueryBuilder.NumericRange(field.StorageName, number, double.PositiveInfinity, exclusiveMinimum: true),
                ExpressionType.GreaterThanOrEqual => RespireSearchQueryBuilder.NumericRange(field.StorageName, number, double.PositiveInfinity),
                _ => RespireSearchQueryBuilder.NumericRange(field.StorageName, number, number),
            };
            equality = query.Value;
        }
        return operation == ExpressionType.NotEqual ? Negate(equality) : equality;
    }

    private bool TryField(Expression expression, out RespireVectorDataFilterField field, out Type type)
    {
        expression = Strip(expression);
        type = expression.Type;
        if (expression is MemberExpression member && member.Expression == _record)
        {
            field = fields.FirstOrDefault(candidate => candidate.PropertyName == member.Member.Name)
                ?? throw Unsupported(expression, "Property has no explicit filter mapping.");
            return true;
        }
        field = null!;
        return false;
    }

    private static void ValidateType(RespireVectorDataFilterField field, Type type)
    {
        if (field.Kind switch
        {
            RespireVectorDataFilterKind.String => type == typeof(string),
            RespireVectorDataFilterKind.Numeric => NumericType(type),
            RespireVectorDataFilterKind.Boolean => type == typeof(bool),
            _ => false,
        }) return;
        throw new NotSupportedException($"Property type '{type}' does not match filter mapping '{field.PropertyName}'.");
    }

    private static bool NumericType(Type type) => type == typeof(byte) || type == typeof(sbyte) || type == typeof(short)
        || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(float) || type == typeof(double);

    private static void ValidateCollection(RespireVectorDataFilterField field, Type type)
    {
        if (field.Kind != RespireVectorDataFilterKind.StringCollection || type != typeof(string[]) && type != typeof(List<string>))
            throw new NotSupportedException("Collection filters require an explicitly mapped string array or List<string>.");
    }

    private static string Tag(RespireVectorDataFilterField field, string value)
        => RespireSearchQueryBuilder.Tag(field.StorageName, RespireVectorDataFilterEncoding.EncodeTag(value)).Value;

    private static string Negate(string query) => query switch { "*" => None, None => "*", _ => $"-({query})" };

    private static string Combine(string left, string right, bool and)
    {
        if (left == (and ? None : "*") || right == (and ? None : "*")) return and ? None : "*";
        if (left == (and ? "*" : None)) return right;
        if (right == (and ? "*" : None)) return left;
        return $"({left}{(and ? " " : " | ")}{right})";
    }

    private static ExpressionType Reverse(ExpressionType operation) => operation switch
    {
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        _ => operation,
    };

    private static Expression Strip(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert, Method: null } conversion)
        {
            var target = Nullable.GetUnderlyingType(conversion.Type) ?? conversion.Type;
            var nullableSource = Nullable.GetUnderlyingType(conversion.Operand.Type);
            var source = nullableSource ?? conversion.Operand.Type;
            if (nullableSource is not null && Nullable.GetUnderlyingType(conversion.Type) is null
                || target != source
                && !(target == typeof(double) && NumericType(source))
                && !(target == typeof(int) && (source == typeof(byte) || source == typeof(sbyte) || source == typeof(short) || source == typeof(ushort))))
                throw Unsupported(expression, "Only nullable lifting, small-integer promotion and lossless promotion to double are supported.");
            expression = conversion.Operand;
        }
        return expression;
    }

    private static object? Value(Expression expression)
    {
        expression = Strip(expression);
        if (expression is ConstantExpression constant) return constant.Value;
        if (expression is MemberExpression { Member: FieldInfo field } member)
            return field.GetValue(member.Expression is null ? null : Value(member.Expression));
        if (expression is MemberExpression { Member.Name: "Value", Expression: { } operand }
            && Nullable.GetUnderlyingType(operand.Type) is not null)
            return Value(operand) ?? throw new InvalidOperationException("Nullable object must have a value.");
        if (expression is NewArrayExpression { NodeType: ExpressionType.NewArrayInit } array)
            return array.Expressions.Select(Value).ToArray();
        if (expression is MethodCallExpression { Method.DeclaringType: { } declaring, Method.Name: "Empty", Arguments.Count: 0 } && declaring == typeof(Array))
            return Array.Empty<object>();
        throw Unsupported(expression, "Filter constants may use literals, arrays and captured fields; getters and method execution are unsupported.");
    }

    private static bool Contains(MethodCallExpression call, out Expression source, out Expression item)
    {
        if (call.Method.Name == nameof(Enumerable.Contains))
        {
            if (call.Method.DeclaringType == typeof(Enumerable) || call.Method.DeclaringType == typeof(MemoryExtensions))
            {
                if (call.Arguments.Count == 3 && Value(call.Arguments[2]) is not null)
                    throw Unsupported(call, "Custom membership comparers are unsupported.");
                if (call.Arguments.Count is 2 or 3)
                {
                    source = UnwrapSpan(call.Arguments[0]); item = call.Arguments[1]; return true;
                }
            }
            if (call.Object is { } instance && call.Arguments.Count == 1
                && call.Method.DeclaringType is { } declaring && SupportedList(declaring))
            {
                source = instance; item = call.Arguments[0]; return true;
            }
        }
        source = item = null!;
        return false;
    }

    private static bool SupportedList(Type type) => type == typeof(List<string>) || type == typeof(List<int>)
        || type == typeof(List<double>) || type == typeof(List<float>) || type == typeof(List<bool>);

    private static Expression UnwrapSpan(Expression expression)
    {
        if (expression is UnaryExpression { Method.Name: "op_Implicit" } conversion
            && conversion.Method.DeclaringType is { IsGenericType: true } declaring && declaring.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>))
            return conversion.Operand;
        if (expression is MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } call
            && call.Method.DeclaringType is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>))
            return call.Arguments[0];
        return expression;
    }

    private static NotSupportedException Unsupported(Expression expression, string? reason = null)
        => new($"Unsupported filter expression '{expression.NodeType}': {expression}. {reason}");
}
