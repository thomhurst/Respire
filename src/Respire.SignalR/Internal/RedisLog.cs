// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Respire.SignalR.Internal;

// Keep logger definitions shared across closed hub types.
// We'd end up creating separate instances of all the LoggerMessage.Define values for each Hub.
internal static partial class RedisLog
{
    [LoggerMessage(3, LogLevel.Trace, "Subscribing to channel: {Channel}.", EventName = "Subscribing")]
    public static partial void Subscribing(ILogger logger, string channel);

    [LoggerMessage(4, LogLevel.Trace, "Received message from Redis channel {Channel}.", EventName = "ReceivedFromChannel")]
    public static partial void ReceivedFromChannel(ILogger logger, string channel);

    [LoggerMessage(5, LogLevel.Trace, "Publishing message to Redis channel {Channel}.", EventName = "PublishToChannel")]
    public static partial void PublishToChannel(ILogger logger, string channel);

    [LoggerMessage(6, LogLevel.Trace, "Unsubscribing from channel: {Channel}.", EventName = "Unsubscribe")]
    public static partial void Unsubscribe(ILogger logger, string channel);

    [LoggerMessage(10, LogLevel.Debug, "Failed writing message.", EventName = "FailedWritingMessage")]
    public static partial void FailedWritingMessage(ILogger logger, Exception exception);

    [LoggerMessage(11, LogLevel.Warning, "Error processing message for internal server message.", EventName = "InternalMessageFailed")]
    public static partial void InternalMessageFailed(ILogger logger, Exception exception);

    [LoggerMessage(12, LogLevel.Error, "Received a client result for protocol {HubProtocol} which is not supported by this server. This likely means you have different versions of your server deployed.", EventName = "MismatchedServers")]
    public static partial void MismatchedServers(ILogger logger, string hubProtocol);

    [LoggerMessage(13, LogLevel.Error, "Error forwarding client result with ID '{InvocationID}' to server.", EventName = "ErrorForwardingResult")]
    public static partial void ErrorForwardingResult(ILogger logger, string invocationId, Exception ex);

    [LoggerMessage(15, LogLevel.Warning, "Error parsing client result with protocol {HubProtocol}.", EventName = "ErrorParsingResult")]
    public static partial void ErrorParsingResult(ILogger logger, string hubProtocol, Exception? ex);

    [LoggerMessage(16, LogLevel.Warning, "SignalR delivery gap on {Channel}: {Reason}; {DroppedMessages} known dropped messages. Redis pub/sub cannot replay lost messages.")]
    internal static partial void DeliveryGap(ILogger logger, string channel, RespireSubscriptionGapReason reason, long droppedMessages);

    [LoggerMessage(17, LogLevel.Error, "SignalR backplane subscription ended on {Channel}: {Reason}.")]
    internal static partial void SubscriptionEnded(ILogger logger, string channel, RespireSubscriptionEndReason reason);
}
