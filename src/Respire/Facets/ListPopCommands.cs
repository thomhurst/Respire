using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

public partial interface IListCommands
{
    /// <summary>
    /// Pops up to count values from the first nonempty list in input order. Null when no list
    /// is ready before waitFor expires. Without waitFor, returns immediately. Redis: LMPOP / BLMPOP (7.0+).
    /// </summary>
    /// <remarks>Keys must share a Cluster slot. Count must be positive. Timeout.InfiniteTimeSpan waits indefinitely.</remarks>
    ValueTask<RespireListPopManyResult?> PopManyAsync(
        ReadOnlySpan<RespireKey> keys, long count = 1, ListSide side = ListSide.Left,
        TimeSpan? waitFor = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for and pops one value from the first nonempty list in input order. Null on timeout.
    /// Uses a dedicated pooled connection. Redis: BLPOP / BRPOP.
    /// </summary>
    /// <remarks>Keys must share a Cluster slot. Timeout.InfiniteTimeSpan waits indefinitely.</remarks>
    ValueTask<RespireListPopResult?> PopAsync(
        ReadOnlySpan<RespireKey> keys, TimeSpan waitFor, ListSide side = ListSide.Left,
        CancellationToken cancellationToken = default);
}

internal sealed partial class ListCommands
{
    public ValueTask<RespireListPopManyResult?> PopManyAsync(
        ReadOnlySpan<RespireKey> keys, long count = 1, ListSide side = ListSide.Left,
        TimeSpan? waitFor = null, CancellationToken cancellationToken = default)
    {
        var (operation, command) = PopManyCommand(client, keys, count, side, waitFor);
        return waitFor.HasValue
            ? PopManyBlockingAsync(operation, command, cancellationToken)
            : client.ConvertResponseAsync(operation, command, cancellationToken, client,
                static (RespireClient c, in RespValue reply) => ParsePopMany(in reply, c.KeyPrefix));
    }

    public ValueTask<RespireListPopResult?> PopAsync(
        ReadOnlySpan<RespireKey> keys, TimeSpan waitFor, ListSide side = ListSide.Left,
        CancellationToken cancellationToken = default)
    {
        _ = SideToken(side);
        ValidateWait(waitFor);
        var operation = side == ListSide.Left ? "BLPOP" : "BRPOP";
        var arguments = new RespireValue[keys.Length + 1];
        CopyPopKeys(client, keys, arguments, operation);
        arguments[^1] = ToSeconds(waitFor);
        return PopOneBlockingAsync(operation,
            new CmdN(side == ListSide.Left ? Verbs.BLPop : Verbs.BRPop, arguments), cancellationToken);
    }

    private async ValueTask<RespireListPopManyResult?> PopManyBlockingAsync(
        string operation, CmdN command, CancellationToken cancellationToken)
    {
        using var reply = await client.SendBlockingAsync(operation, command, cancellationToken).ConfigureAwait(false);
        return ParsePopMany(in reply, client.KeyPrefix);
    }

    private async ValueTask<RespireListPopResult?> PopOneBlockingAsync(
        string operation, CmdN command, CancellationToken cancellationToken)
    {
        using var reply = await client.SendBlockingAsync(operation, command, cancellationToken).ConfigureAwait(false);
        if (reply.IsNull)
        {
            return null;
        }
        var elements = reply.AsArray();
        if (elements.Length != 2)
        {
            throw new RespireProtocolException("Expected a selected list key and one popped value.");
        }
        return new RespireListPopResult(ParsePoppedKey(in elements[0], client.KeyPrefix), elements[1].AsString());
    }

    internal static (string Operation, CmdN Command) PopManyCommand(
        RespireClient client, ReadOnlySpan<RespireKey> keys, long count, ListSide side, TimeSpan? waitFor)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var sideToken = SideToken(side);
        if (waitFor is { } wait)
        {
            ValidateWait(wait);
        }
        var blocking = waitFor.HasValue;
        var operation = blocking ? "BLMPOP" : "LMPOP";
        var arguments = new RespireValue[keys.Length + (blocking ? 5 : 4)];
        var index = 0;
        if (waitFor is { } timeout)
        {
            arguments[index++] = ToSeconds(timeout);
        }
        arguments[index++] = keys.Length;
        CopyPopKeys(client, keys, arguments.AsSpan(index, keys.Length), operation);
        index += keys.Length;
        arguments[index++] = sideToken;
        arguments[index++] = "COUNT";
        arguments[index] = count;
        return (operation, new CmdN(blocking ? Verbs.BLMPop : Verbs.LMPop, arguments));
    }

    private static void CopyPopKeys(
        RespireClient client, ReadOnlySpan<RespireKey> keys, Span<RespireValue> destination, string operation)
    {
        if (keys.IsEmpty)
        {
            throw new ArgumentException("At least one key is required.", nameof(keys));
        }
        int? slot = null;
        for (var index = 0; index < keys.Length; index++)
        {
            var key = client.Key(in keys[index]);
            if (client.Core.Cluster is not null && key.TryGetClusterSlot(out var keySlot))
            {
                if (slot is { } expected && keySlot != expected)
                {
                    throw new RespireServerException("CROSSSLOT Keys in request don't hash to the same slot", operation);
                }
                slot = keySlot;
            }
            destination[index] = key;
        }
    }

    private static string SideToken(ListSide side) => side switch
    {
        ListSide.Left => "LEFT",
        ListSide.Right => "RIGHT",
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, null),
    };

    private static void ValidateWait(TimeSpan waitFor)
    {
        if (waitFor < TimeSpan.Zero && waitFor != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(waitFor), waitFor, "Wait must be nonnegative or Timeout.InfiniteTimeSpan.");
        }
    }

    internal static RespireListPopManyResult? ParsePopMany(in RespValue reply, string? prefix)
    {
        if (reply.IsNull)
        {
            return null;
        }
        var elements = reply.AsArray();
        if (elements.Length != 2)
        {
            throw new RespireProtocolException("Expected a selected list key and an array of popped values.");
        }
        return new RespireListPopManyResult(ParsePoppedKey(in elements[0], prefix), ResponseReader.StringArray(in elements[1]));
    }

    private static RespireKey ParsePoppedKey(in RespValue value, string? prefix)
    {
        var bytes = value.AsSpan();
        if (prefix is not null)
        {
            var prefixBytes = Encoding.UTF8.GetBytes(prefix);
            if (!bytes.StartsWith(prefixBytes))
            {
                throw new RespireProtocolException("Returned list key does not start with the client key prefix.");
            }
            bytes = bytes[prefixBytes.Length..];
        }
        return new RespireKey(bytes.ToArray());
    }
}
