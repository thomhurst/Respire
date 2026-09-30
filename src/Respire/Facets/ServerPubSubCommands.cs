using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>An owned channel name and its node-local subscriber count.</summary>
public readonly record struct RespireChannelSubscriberCount(RespireChannel Channel, long Subscribers);

public partial interface IServerCommands
{
    /// <summary>Lists literal channels on one execution node. Set sharded for PUBSUB SHARDCHANNELS (Redis 7+).</summary>
    /// <remarks>Pattern and channel bytes are not key-prefixed. Pattern subscriptions are excluded.</remarks>
    ValueTask<RespireChannel[]> PubSubChannelsAsync(RespireChannel? pattern = null, bool sharded = false, CancellationToken cancellationToken = default);
    /// <summary>Counts literal subscribers on one execution node. Set sharded for PUBSUB SHARDNUMSUB (Redis 7+).</summary>
    /// <remarks>Names are copied before asynchronous work. Subscription kind does not alter the supplied bytes.</remarks>
    ValueTask<RespireChannelSubscriberCount[]> PubSubSubscriberCountsAsync(ReadOnlySpan<RespireChannel> channels, bool sharded = false, CancellationToken cancellationToken = default);
    /// <summary>Counts unique subscribed patterns on one execution node. Redis: PUBSUB NUMPAT.</summary>
    ValueTask<long> PubSubPatternCountAsync(CancellationToken cancellationToken = default);
    /// <summary>Lists channels separately on every discovered Cluster node, including replicas; one result on standalone.</summary>
    ValueTask<RespireServerResult<RespireChannel[]>[]> PubSubChannelsOnAllNodesAsync(RespireChannel? pattern = null, bool sharded = false, CancellationToken cancellationToken = default);
    /// <summary>Counts subscribers separately on every discovered node; results are not aggregated.</summary>
    ValueTask<RespireServerResult<RespireChannelSubscriberCount[]>[]> PubSubSubscriberCountsOnAllNodesAsync(ReadOnlySpan<RespireChannel> channels, bool sharded = false, CancellationToken cancellationToken = default);
    /// <summary>Counts unique patterns separately on every discovered node; results are not aggregated.</summary>
    ValueTask<RespireServerResult<long>[]> PubSubPatternCountOnAllNodesAsync(CancellationToken cancellationToken = default);
}

internal sealed partial class ServerCommands
{
    // Channel names and patterns are never routing keys, including for sharded introspection.
    private static readonly Verb PubSubChannels = new(-1, "PUBSUB", "CHANNELS");
    private static readonly Verb PubSubShardChannels = new(-1, "PUBSUB", "SHARDCHANNELS");
    private static readonly Verb PubSubNumSub = new(-1, "PUBSUB", "NUMSUB");
    private static readonly Verb PubSubShardNumSub = new(-1, "PUBSUB", "SHARDNUMSUB");
    private static readonly Verb PubSubNumPat = new(-1, "PUBSUB", "NUMPAT");
    private readonly record struct PubSubCall<T>(string Operation, CmdN Command, ResponseConverter<ServerCommands, T> Convert);
    private static readonly PubSubCall<long> PatternCountCall = new("PUBSUB NUMPAT", new CmdN(PubSubNumPat, []),
        static (ServerCommands _, in RespValue value) => ParseSubscriptionCount(in value));

    public ValueTask<RespireChannel[]> PubSubChannelsAsync(RespireChannel? pattern = null, bool sharded = false, CancellationToken cancellationToken = default)
        => ExecutePubSubCallAsync(CreateChannelsCall(pattern, sharded), cancellationToken);

    public ValueTask<RespireChannelSubscriberCount[]> PubSubSubscriberCountsAsync(ReadOnlySpan<RespireChannel> channels, bool sharded = false, CancellationToken cancellationToken = default)
        => ExecutePubSubCallAsync(CreateCountsCall(channels, sharded), cancellationToken);

    public ValueTask<long> PubSubPatternCountAsync(CancellationToken cancellationToken = default)
        => ExecutePubSubCallAsync(PatternCountCall, cancellationToken);

    public ValueTask<RespireServerResult<RespireChannel[]>[]> PubSubChannelsOnAllNodesAsync(RespireChannel? pattern = null, bool sharded = false, CancellationToken cancellationToken = default)
        => ExecutePubSubCallOnAllNodesAsync(CreateChannelsCall(pattern, sharded), cancellationToken);

    public ValueTask<RespireServerResult<RespireChannelSubscriberCount[]>[]> PubSubSubscriberCountsOnAllNodesAsync(ReadOnlySpan<RespireChannel> channels, bool sharded = false, CancellationToken cancellationToken = default)
        => ExecutePubSubCallOnAllNodesAsync(CreateCountsCall(channels, sharded), cancellationToken);

    public ValueTask<RespireServerResult<long>[]> PubSubPatternCountOnAllNodesAsync(CancellationToken cancellationToken = default)
        => ExecutePubSubCallOnAllNodesAsync(PatternCountCall, cancellationToken);

    private ValueTask<T> ExecutePubSubCallAsync<T>(PubSubCall<T> call, CancellationToken cancellationToken)
        => ConvertAsync(call.Operation, call.Command, cancellationToken, call.Convert);

    private ValueTask<RespireServerResult<T>[]> ExecutePubSubCallOnAllNodesAsync<T>(PubSubCall<T> call, CancellationToken cancellationToken)
        => FanOutAsync(call.Operation, call.Command, cancellationToken, call.Convert);

    private static PubSubCall<RespireChannel[]> CreateChannelsCall(RespireChannel? pattern, bool sharded)
        => new(sharded ? "PUBSUB SHARDCHANNELS" : "PUBSUB CHANNELS",
            new CmdN(sharded ? PubSubShardChannels : PubSubChannels, PatternArguments(pattern)),
            sharded ? static (ServerCommands _, in RespValue value) => ParseChannels(in value, true)
                : static (ServerCommands _, in RespValue value) => ParseChannels(in value, false));

    private static PubSubCall<RespireChannelSubscriberCount[]> CreateCountsCall(ReadOnlySpan<RespireChannel> channels, bool sharded)
        => new(sharded ? "PUBSUB SHARDNUMSUB" : "PUBSUB NUMSUB",
            new CmdN(sharded ? PubSubShardNumSub : PubSubNumSub, ChannelArguments(channels)),
            sharded ? static (ServerCommands _, in RespValue value) => ParseSubscriberCounts(in value, true)
                : static (ServerCommands _, in RespValue value) => ParseSubscriberCounts(in value, false));

    private static RespireValue[] PatternArguments(RespireChannel? pattern)
        => pattern is { } value ? [value.AsValue()] : [];

    private static RespireValue[] ChannelArguments(ReadOnlySpan<RespireChannel> channels)
    {
        var arguments = new RespireValue[channels.Length];
        for (var index = 0; index < channels.Length; index++) arguments[index] = channels[index].AsValue();
        return arguments;
    }

    private static RespireChannel[] ParseChannels(in RespValue value, bool sharded)
    {
        if (value.Type is not (RespDataType.Array or RespDataType.Set))
            throw new RespireProtocolException("PUBSUB channels must be an array.");
        var items = value.AsArray();
        var result = new RespireChannel[items.Length];
        for (var index = 0; index < items.Length; index++) result[index] = ParseChannel(in items[index], sharded);
        return result;
    }

    private static RespireChannel ParseChannel(in RespValue value, bool sharded)
    {
        if (value.Type is not (RespDataType.BulkString or RespDataType.SimpleString))
            throw new RespireProtocolException("PUBSUB channel names must be strings.");
        var channel = RespireChannel.FromOwnedBytes(value.AsSpan().ToArray());
        return sharded ? RespireChannel.Sharded(channel) : channel;
    }

    private static long ParseSubscriptionCount(in RespValue value)
    {
        if (value.Type != RespDataType.Integer || value.AsInteger() < 0)
            throw new RespireProtocolException("PUBSUB counts must be nonnegative integers.");
        return value.AsInteger();
    }

    private static RespireChannelSubscriberCount[] ParseSubscriberCounts(in RespValue value, bool sharded)
    {
        if (value.Type is not (RespDataType.Array or RespDataType.Map))
            throw new RespireProtocolException("PUBSUB subscriber counts must contain channel/count pairs.");
        var items = value.AsArray();
        if (items.Length % 2 != 0)
            throw new RespireProtocolException("PUBSUB subscriber counts must contain complete channel/count pairs.");
        var result = new RespireChannelSubscriberCount[items.Length / 2];
        for (var index = 0; index < result.Length; index++)
            result[index] = new(ParseChannel(in items[index * 2], sharded), ParseSubscriptionCount(in items[index * 2 + 1]));
        return result;
    }
}
