using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>Measurements collected by Redis 8.6+ HOTKEYS.</summary>
[Flags]
public enum RespireHotKeysMetrics
{
    /// <summary>Command execution time in microseconds.</summary>
    Cpu = 1,
    /// <summary>Request and response bytes.</summary>
    Network = 2,
}

/// <summary>Options for a new node-local HOTKEYS session. Null options preserve server defaults.</summary>
public sealed record RespireHotKeysOptions
{
    /// <summary>CPU, network, or both; defaults to both measurements.</summary>
    public RespireHotKeysMetrics Metrics { get; init; } = RespireHotKeysMetrics.Cpu | RespireHotKeysMetrics.Network;
    /// <summary>Top-key count, 1 through 64; omitted defaults to 10.</summary>
    public int? Count { get; init; }
    /// <summary>Whole seconds, 1 through 1,000,000. Omit to run until explicitly stopped.</summary>
    public int? DurationSeconds { get; init; }
    /// <summary>Sample one in this many commands, 1 through int.MaxValue; omitted defaults to 1.</summary>
    public int? SampleRatio { get; init; }
    /// <summary>Distinct literal slots, 0 through 16383. Empty omits SLOTS. Redis requires Cluster and local ownership.</summary>
    /// <remarks>Copied into the command before asynchronous work; do not mutate concurrently with the call.</remarks>
    public ReadOnlyMemory<int> Slots { get; init; }

    internal CmdN BuildCommand()
    {
        if (Metrics == 0 || (Metrics & ~(RespireHotKeysMetrics.Cpu | RespireHotKeysMetrics.Network)) != 0)
            throw new ArgumentOutOfRangeException(nameof(Metrics));
        if (Count is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(Count));
        if (DurationSeconds is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(DurationSeconds));
        if (SampleRatio is < 1) throw new ArgumentOutOfRangeException(nameof(SampleRatio));
        if (Slots.Length > ClusterHash.SlotCount) throw new ArgumentOutOfRangeException(nameof(Slots));
        if (!Slots.IsEmpty)
        {
            var seen = new HashSet<int>();
            foreach (var slot in Slots.Span)
                if ((uint)slot >= ClusterHash.SlotCount || !seen.Add(slot))
                    throw new ArgumentException("Slots must be distinct integers from 0 through 16383.", nameof(Slots));
        }

        var metricCount = Metrics == (RespireHotKeysMetrics.Cpu | RespireHotKeysMetrics.Network) ? 2 : 1;
        List<RespireValue> arguments = ["METRICS", metricCount];
        if ((Metrics & RespireHotKeysMetrics.Cpu) != 0) arguments.Add("CPU");
        if ((Metrics & RespireHotKeysMetrics.Network) != 0) arguments.Add("NET");
        if (Count is { } count) { arguments.Add("COUNT"); arguments.Add(count); }
        if (DurationSeconds is { } seconds) { arguments.Add("DURATION"); arguments.Add(seconds); }
        if (SampleRatio is { } ratio) { arguments.Add("SAMPLE"); arguments.Add(ratio); }
        if (!Slots.IsEmpty)
        {
            arguments.Add("SLOTS"); arguments.Add(Slots.Length);
            foreach (var slot in Slots.Span) arguments.Add(slot);
        }
        return new(HotKeysCommands.Start, arguments.ToArray());
    }
}

/// <summary>An inclusive slot range returned by HOTKEYS GET.</summary>
public readonly record struct RespireHotKeysSlotRange(int Start, int End);
/// <summary>A binary key and its estimated CPU execution microseconds.</summary>
public sealed record RespireHotKeyCpuEntry(byte[] Key, long Microseconds);
/// <summary>A binary key and its estimated request/response bytes.</summary>
public sealed record RespireHotKeyNetworkEntry(byte[] Key, long Bytes);

/// <summary>One owned HOTKEYS snapshot. Null optional measurements mean the server omitted them.</summary>
/// <remarks>Arrays belong to the caller and are mutable; record equality does not compare array contents.
/// AdditionalFields have GC-owned storage and need no disposal. No key prefix is removed from reported keys.</remarks>
public sealed record RespireHotKeysSnapshot(
    bool TrackingActive, long SampleRatio, RespireHotKeysSlotRange[] SelectedSlots,
    long CollectionStartUnixMilliseconds, long CollectionDurationMilliseconds,
    long AllCommandsAllSlotsMicroseconds, long NetworkBytesAllCommandsAllSlots,
    long? SampledCommandsSelectedSlotsMicroseconds, long? AllCommandsSelectedSlotsMicroseconds,
    long? NetworkBytesSampledCommandsSelectedSlots, long? NetworkBytesAllCommandsSelectedSlots,
    long? TotalCpuUserMilliseconds, long? TotalCpuSystemMilliseconds, long? TotalNetworkBytes,
    RespireHotKeyCpuEntry[]? ByCpuTime, RespireHotKeyNetworkEntry[]? ByNetworkBytes,
    IReadOnlyDictionary<string, RespireResult> AdditionalFields);

internal static class HotKeysCommands
{
    internal static readonly Verb Start = new(-1, "HOTKEYS", "START");
    internal static readonly Verb Get = new(-1, "HOTKEYS", "GET");
    internal static readonly Verb Stop = new(-1, "HOTKEYS", "STOP");
    internal static readonly Verb Reset = new(-1, "HOTKEYS", "RESET");
}
