using System.Buffers.Text;
using System.Text;

namespace Respire.Internal;

internal enum SentinelEventKind : byte
{
    None,
    SwitchMaster,
    MasterDown,
    ReplicaDown,
}

/// <summary>
/// A Sentinel failover event that concerns the configured service. Relevant master events
/// allocate their endpoints; events for other services return <see cref="SentinelEventKind.None"/> without allocation.
/// </summary>
/// <remarks>
/// Sentinel publishes these payloads (sentinel.c, the <c>%@</c> event format):
/// <list type="bullet">
/// <item><c>+switch-master &lt;master-name&gt; &lt;old-ip&gt; &lt;old-port&gt; &lt;new-ip&gt; &lt;new-port&gt;</c></item>
/// <item><c>+sdown|+odown master &lt;master-name&gt; &lt;ip&gt; &lt;port&gt;</c>, with <c>#quorum n/m</c> appended to <c>+odown</c></item>
/// <item><c>+sdown|+odown slave &lt;ip:port&gt; &lt;ip&gt; &lt;port&gt; @ &lt;master-name&gt; &lt;master-ip&gt; &lt;master-port&gt;</c></item>
/// </list>
/// A switch event whose endpoints do not parse is still reported, with null endpoints, so the
/// router falls back to untargeted rediscovery; ROLE validation selects the primary either way.
/// </remarks>
internal readonly record struct SentinelEvent(
    SentinelEventKind Kind, RespireEndpoint? OldPrimary = null, RespireEndpoint? NewPrimary = null)
{
    internal static SentinelEvent Parse(ReadOnlySpan<byte> channel, ReadOnlySpan<byte> text, ReadOnlySpan<byte> serviceName)
    {
        if (channel.SequenceEqual("+switch-master"u8))
        {
            if (!NextToken(ref text, out var name) || !name.SequenceEqual(serviceName)) return default;
            var oldPrimary = NextEndpoint(ref text);
            var newPrimary = NextEndpoint(ref text);
            return new(SentinelEventKind.SwitchMaster, oldPrimary, newPrimary);
        }

        if (!channel.SequenceEqual("+sdown"u8) && !channel.SequenceEqual("+odown"u8)) return default;
        if (!NextToken(ref text, out var instanceType)) return default;
        if (instanceType.SequenceEqual("master"u8))
        {
            if (!NextToken(ref text, out var master) || !master.SequenceEqual(serviceName)) return default;
            return new(SentinelEventKind.MasterDown, NextEndpoint(ref text));
        }
        if (!instanceType.SequenceEqual("slave"u8)) return default;
        // Skip the replica name, IP and port, then expect "@ <master-name>".
        for (var skipped = 0; skipped < 3; skipped++)
            if (!NextToken(ref text, out _)) return default;
        return NextToken(ref text, out var separator) && separator.SequenceEqual("@"u8)
            && NextToken(ref text, out var owner) && owner.SequenceEqual(serviceName)
            ? new(SentinelEventKind.ReplicaDown) : default;
    }

    private static RespireEndpoint? NextEndpoint(ref ReadOnlySpan<byte> text)
    {
        if (!NextToken(ref text, out var host) || !NextToken(ref text, out var portText)) return null;
        if (!Utf8Parser.TryParse(portText, out int port, out var consumed) || consumed != portText.Length
            || port is < 1 or > 65535) return null;
        return new RespireEndpoint(Encoding.UTF8.GetString(host), port);
    }

    private static bool NextToken(ref ReadOnlySpan<byte> text, out ReadOnlySpan<byte> token)
    {
        text = text.TrimStart((byte)' ');
        if (text.IsEmpty)
        {
            token = default;
            return false;
        }
        var end = text.IndexOf((byte)' ');
        if (end < 0)
        {
            token = text;
            text = default;
        }
        else
        {
            token = text[..end];
            text = text[(end + 1)..];
        }
        return true;
    }
}
