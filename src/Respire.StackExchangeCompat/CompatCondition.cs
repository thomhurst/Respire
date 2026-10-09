using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using StackExchange.Redis;

namespace Respire.StackExchangeCompat;

// Condition has no public visitor, and ConditionResult has no public factory or setter.
// This bridge audits the private shapes from 3.3.1 and 3.4.0, preserving fields for trimming.
// Unknown shapes fail closed rather than decoding ToString(), which loses binary key/value data.
internal sealed class CompatCondition(RespireKey key, RespireCommand command, RespireValue[] arguments,
    Func<RedisResult, bool> evaluate, ConditionResult result)
{
    internal RespireKey Key { get; } = key;
    internal ConditionResult Result { get; } = result;

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, "StackExchange.Redis.Condition+ExistsCondition", "StackExchange.Redis")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, "StackExchange.Redis.Condition+EqualsCondition", "StackExchange.Redis")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, "StackExchange.Redis.Condition+LengthCondition", "StackExchange.Redis")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, "StackExchange.Redis.Condition+ListCondition", "StackExchange.Redis")]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicFields, "StackExchange.Redis.Condition+SortedSetRangeLengthCondition", "StackExchange.Redis")]
    internal static CompatCondition Create(Condition condition)
    {
        if (condition.GetType().DeclaringType != typeof(Condition)) throw Unsupported();
        var key = Copy((byte[]?)Read<RedisKey>(condition, "key"));
        var result = CreateResult(condition);
        switch (condition.GetType().Name)
        {
            case "ExistsCondition":
            {
                var value = Copy(Read<RedisValue>(condition, "expectedValue"));
                var type = Read<RedisType>(condition, "type");
                var expected = Read<bool>(condition, "expectedResult");
                var command = value.IsNull ? RespireCommands.Key.EXISTS : type switch
                {
                    RedisType.Hash => RespireCommands.Hash.HEXISTS,
                    RedisType.Set => RespireCommands.Set.SISMEMBER,
                    RedisType.SortedSet => RespireCommands.SortedSet.ZSCORE,
                    _ => throw Unsupported(),
                };
                return Build(command, value.IsNull ? [key] : [key, value], reply =>
                    (type == RedisType.SortedSet ? !reply.IsNull : (long)reply != 0) == expected);
            }
            case "EqualsCondition":
            {
                var member = Copy(Read<RedisValue>(condition, "memberName"));
                var expectedValue = Copy(Read<RedisValue>(condition, "expectedValue"));
                var equal = Read<bool>(condition, "expectedEqual");
                var type = Read<RedisType>(condition, "type");
                var command = type switch
                {
                    RedisType.SortedSet => RespireCommands.SortedSet.ZSCORE,
                    RedisType.Hash when member.IsNull => RespireCommands.String.GET,
                    RedisType.Hash => RespireCommands.Hash.HGET,
                    _ => throw Unsupported(),
                };
                return Build(command, member.IsNull ? [key] : [key, member], reply =>
                {
                    RedisValue actual = RedisValue.Null;
                    if (!reply.IsNull)
                        actual = type == RedisType.SortedSet ? (RedisValue)(double)reply : (byte[]?)reply;
                    return (actual == expectedValue) == equal;
                });
            }
            case "ListCondition":
            {
                var expected = Read<bool>(condition, "expectedResult");
                var value = Read<RedisValue?>(condition, "expectedValue");
                var snapshot = value.HasValue ? Copy(value.Value) : (RedisValue?)null;
                return Build(RespireCommands.List.LINDEX, [key, Read<long>(condition, "index")], reply =>
                {
                    RedisValue actual = reply.IsNull ? RedisValue.Null : (byte[]?)reply;
                    return snapshot.HasValue ? (actual == snapshot.Value) == expected : actual.IsNull != expected;
                });
            }
            case "LengthCondition":
            {
                var command = Read<RedisType>(condition, "type") switch
                {
                    RedisType.Hash => RespireCommands.Hash.HLEN,
                    RedisType.String => RespireCommands.String.STRLEN,
                    RedisType.List => RespireCommands.List.LLEN,
                    RedisType.Set => RespireCommands.Set.SCARD,
                    RedisType.SortedSet => RespireCommands.SortedSet.ZCARD,
                    RedisType.Stream => RespireCommands.Stream.XLEN,
                    _ => throw Unsupported(),
                };
                return Length(command, [key]);
            }
            case "SortedSetRangeLengthCondition":
                return Length(RespireCommands.SortedSet.ZCOUNT,
                    [key, Score(Read<RedisValue>(condition, "min")), Score(Read<RedisValue>(condition, "max"))]);
            default: throw Unsupported();
        }

        CompatCondition Build(RespireCommand command, RedisValue[] arguments, Func<RedisResult, bool> evaluate)
            => new(new RespireKey((byte[]?)key!), command, arguments.Select(static argument => argument.ToRespireValue()).ToArray(), evaluate, result);

        CompatCondition Length(RespireCommand command, RedisValue[] arguments)
        {
            var expected = Read<long>(condition, "expectedLength");
            var comparison = Read<int>(condition, "compareToResult");
            return Build(command, arguments, reply => expected.CompareTo((long)reply) == comparison);
        }
    }

    internal async Task<bool> EvaluateAsync(IRespireClient client, CancellationToken token)
    {
        var satisfied = false;
        try
        {
            using var reply = await client.ExecuteAsync(command, arguments, cancellationToken: token).ConfigureAwait(false);
            satisfied = evaluate(reply.ToStackExchangeResult());
        }
        catch (RespireServerException) { /* Upstream condition errors are unsatisfied conditions. */ }
        SetSatisfied(Result, satisfied);
        return satisfied;
    }

    private static RedisValue Copy(RedisValue value) => value.IsNull ? RedisValue.Null : ((byte[]?)value)!.ToArray();
    private static RedisValue Score(RedisValue value)
    {
        var score = (double)value;
        if (double.IsNegativeInfinity(score)) return "-inf";
        if (double.IsPositiveInfinity(score)) return "+inf";
        return score.ToString("R", CultureInfo.InvariantCulture);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Create preserves all fields of each explicitly supported upstream nested type.")]
    private static T Read<T>(Condition condition, string field)
    {
        var member = condition.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
        if (member is null || member.FieldType != typeof(T)) throw Unsupported();
        return (T)member.GetValue(condition)!;
    }

    private static NotSupportedException Unsupported() => Compatibility.Unsupported(
        "Condition shape; supported: key/hash/set/sorted-set existence, string/hash/sorted-set equality, "
        + "list index, lengths and sorted-set score-range lengths (StackExchange.Redis 3.3.1/3.4.0). "
        + "Use native WATCH for other conditions");

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern ConditionResult CreateResult(Condition condition);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetSatisfied")]
    private static extern void SetSatisfied(ConditionResult result, bool satisfied);
}
