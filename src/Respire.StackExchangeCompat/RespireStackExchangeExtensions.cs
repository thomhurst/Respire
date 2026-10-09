using Respire.Protocol;
using StackExchange.Redis;

namespace Respire.StackExchangeCompat;

/// <summary>Converts StackExchange.Redis boundary values for incremental native API migration.</summary>
/// <remarks>This package does not yet implement StackExchange.Redis client interfaces.</remarks>
public static class RespireStackExchangeExtensions
{
    /// <summary>Converts a non-null key without decoding binary content.</summary>
    /// <remarks>Binary storage can be borrowed. Keep it unchanged until the native command completes.</remarks>
    public static RespireKey ToRespireKey(this RedisKey key)
    {
        byte[]? bytes = key;
        ArgumentNullException.ThrowIfNull(bytes, nameof(key));
        return new RespireKey(bytes);
    }

    /// <summary>Converts a value using StackExchange.Redis's exact wire representation.</summary>
    /// <remarks>Null remains an absent native argument. Binary storage can be borrowed;
    /// keep it unchanged until the native command completes.</remarks>
    public static RespireValue ToRespireValue(this RedisValue value)
        => value.IsNull ? RespireValue.Null : new RespireValue((ReadOnlyMemory<byte>)value);

    /// <summary>Copies a supported reply into a standalone StackExchange.Redis result.</summary>
    /// <remarks>The caller still owns and must dispose the native root result. The returned result
    /// remains valid afterward. Error elements, verbatim strings and out-of-band shapes are rejected
    /// because the public boundary cannot faithfully preserve all their semantics.</remarks>
    public static RedisResult ToStackExchangeResult(this RespireResult result)
    {
        var type = result.Type;
        if (result.IsNull)
        {
            return result.NullWireType switch
            {
                RespDataType.Array => RedisResult.Create((RedisResult[])null!, ResultType.Array),
                RespDataType.BulkString or RespDataType.Null => RedisResult.Create(RedisValue.Null),
                var wireType => throw Unsupported(wireType ?? type),
            };
        }

        switch (type)
        {
            case RespDataType.SimpleString:
                return RedisResult.Create(result.AsBytes(), ResultType.SimpleString);
            case RespDataType.BulkString:
                return RedisResult.Create(result.AsBytes(), ResultType.BulkString);
            case RespDataType.Integer:
                return RedisResult.Create(result.AsInteger(), ResultType.Integer);
            case RespDataType.Boolean:
                return RedisResult.Create(result.AsBoolean(), ResultType.Boolean);
            case RespDataType.Double:
                return RedisResult.Create(result.AsDouble(), ResultType.Double);
            case RespDataType.BigNumber:
                return RedisResult.Create(result.AsBytes(), ResultType.BigInteger);
            case RespDataType.Array:
            case RespDataType.Map:
            case RespDataType.Set:
                var children = new RedisResult[result.Count];
                for (var index = 0; index < children.Length; index++)
                {
                    children[index] = result[index].ToStackExchangeResult();
                }
                return RedisResult.Create(children, type switch
                {
                    RespDataType.Map => ResultType.Map,
                    RespDataType.Set => ResultType.Set,
                    _ => ResultType.Array,
                });
            default:
                throw Unsupported(type);
        }
    }

    /// <summary>Runs a native command with StackExchange.Redis arguments and copies its reply.</summary>
    /// <remarks>Routing, flags, cancellation and exceptions retain native semantics. The bridge
    /// disposes the native reply, but never the caller's client. This allocates boundary arrays
    /// and result copies; use native typed commands for ordinary application code.</remarks>
    public static ValueTask<RedisResult> ExecuteStackExchangeAsync(
        this IRespireClient client,
        RespireCommand command,
        ReadOnlySpan<RedisValue> arguments,
        RespireCommandFlags flags = RespireCommandFlags.None,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var nativeArguments = new RespireValue[arguments.Length];
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index].IsNull)
            {
                throw new ArgumentException("Null values cannot be sent as Redis arguments.", nameof(arguments));
            }
            nativeArguments[index] = arguments[index].ToRespireValue();
        }
        return ExecuteAndConvertAsync(client, command, nativeArguments, flags, cancellationToken);
    }

    private static async ValueTask<RedisResult> ExecuteAndConvertAsync(
        IRespireClient client,
        RespireCommand command,
        RespireValue[] arguments,
        RespireCommandFlags flags,
        CancellationToken cancellationToken)
    {
        using var result = await client.ExecuteAsync(command, arguments, flags, cancellationToken).ConfigureAwait(false);
        return result.ToStackExchangeResult();
    }

    private static NotSupportedException Unsupported(RespDataType type)
        => new($"The {type} reply cannot be represented faithfully by this compatibility boundary. "
            + "Use native ExecuteAsync and RespireResult for this reply.");
}
