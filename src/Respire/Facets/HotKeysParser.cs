using Respire.Protocol;

namespace Respire;

internal static class HotKeysParser
{
    internal static RespireHotKeysSnapshot[]? Parse(in RespValue value)
    {
        if (value.IsNull) return null;
        var rows = Array(in value);
        var snapshots = new RespireHotKeysSnapshot[rows.Length];
        for (var index = 0; index < rows.Length; index++) snapshots[index] = Snapshot(in rows[index]);
        return snapshots;
    }

    private static RespireHotKeysSnapshot Snapshot(in RespValue value)
    {
        if (value.Type is not (RespDataType.Array or RespDataType.Map))
            throw new RespireProtocolException("HOTKEYS snapshot must contain field/value pairs.");
        var pairs = value.AsArray();
        if (pairs.Length % 2 != 0) throw new RespireProtocolException("HOTKEYS snapshot has an incomplete field.");
        var fields = new Dictionary<string, RespValue>(StringComparer.Ordinal);
        for (var index = 0; index < pairs.Length; index += 2)
        {
            RequireString(in pairs[index]);
            if (!fields.TryAdd(pairs[index].AsString(), pairs[index + 1]))
                throw new RespireProtocolException("HOTKEYS snapshot contains a duplicate field.");
        }

        var active = Number(Take(fields, "tracking-active"));
        if (active > 1) throw new RespireProtocolException("HOTKEYS tracking-active must be 0 or 1.");
        var ratio = Number(Take(fields, "sample-ratio"));
        if (ratio == 0) throw new RespireProtocolException("HOTKEYS sample-ratio must be positive.");
        var selected = Slots(Take(fields, "selected-slots"));
        var start = Number(Take(fields, "collection-start-time-unix-ms"));
        var duration = Number(Take(fields, "collection-duration-ms"));
        var allCpu = Number(Take(fields, "all-commands-all-slots-us"));
        var allNetwork = Number(Take(fields, "net-bytes-all-commands-all-slots"));
        var sampledCpu = OptionalNumber(fields, "sampled-commands-selected-slots-us");
        var selectedCpu = OptionalNumber(fields, "all-commands-selected-slots-us");
        var sampledNetwork = OptionalNumber(fields, "net-bytes-sampled-commands-selected-slots");
        var selectedNetwork = OptionalNumber(fields, "net-bytes-all-commands-selected-slots");
        var user = OptionalNumber(fields, "total-cpu-time-user-ms");
        var system = OptionalNumber(fields, "total-cpu-time-sys-ms");
        var totalNetwork = OptionalNumber(fields, "total-net-bytes");
        RespireHotKeyCpuEntry[]? cpu = null;
        if (fields.Remove("by-cpu-time-us", out var cpuValue))
        {
            var entries = RankedPairs(in cpuValue);
            cpu = new RespireHotKeyCpuEntry[entries.Length / 2];
            for (var index = 0; index < cpu.Length; index++)
                cpu[index] = new(Key(in entries[index * 2]), Number(in entries[index * 2 + 1]));
        }
        RespireHotKeyNetworkEntry[]? network = null;
        if (fields.Remove("by-net-bytes", out var networkValue))
        {
            var entries = RankedPairs(in networkValue);
            network = new RespireHotKeyNetworkEntry[entries.Length / 2];
            for (var index = 0; index < network.Length; index++)
                network[index] = new(Key(in entries[index * 2]), Number(in entries[index * 2 + 1]));
        }
        var additional = new Dictionary<string, RespireResult>(fields.Count, StringComparer.Ordinal);
        foreach (var field in fields) additional.Add(field.Key, new(field.Value.ToOwned()));
        return new(active == 1, ratio, selected, start, duration, allCpu, allNetwork, sampledCpu, selectedCpu,
            sampledNetwork, selectedNetwork, user, system, totalNetwork, cpu, network, additional);
    }

    internal static bool Stopped(in RespValue value) => !value.IsNull && Ok(in value);

    internal static bool Ok(in RespValue value)
    {
        if (value.Type != RespDataType.SimpleString || !value.AsSpan().SequenceEqual("OK"u8))
            throw new RespireProtocolException("HOTKEYS mutation must return OK.");
        return true;
    }

    private static RespValue Take(Dictionary<string, RespValue> fields, string name)
        => fields.Remove(name, out var value) ? value : throw new RespireProtocolException($"HOTKEYS snapshot is missing {name}.");

    private static long? OptionalNumber(Dictionary<string, RespValue> fields, string name)
        => fields.Remove(name, out var value) ? Number(in value) : null;

    private static long Number(in RespValue value)
    {
        if (value.Type != RespDataType.Integer || value.AsInteger() < 0)
            throw new RespireProtocolException("HOTKEYS measurement must be a nonnegative integer.");
        return value.AsInteger();
    }

    private static RespireHotKeysSlotRange[] Slots(in RespValue value)
    {
        var rows = Array(in value);
        var ranges = new RespireHotKeysSlotRange[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            var row = Array(in rows[index]);
            if (row.Length is not (1 or 2)) throw new RespireProtocolException("HOTKEYS slot range must have one or two slots.");
            var start = Number(in row[0]);
            var end = row.Length == 1 ? start : Number(in row[1]);
            if (start > end || end >= 16384) throw new RespireProtocolException("HOTKEYS slot range is invalid.");
            ranges[index] = new((int)start, (int)end);
        }
        return ranges;
    }

    private static ReadOnlySpan<RespValue> RankedPairs(in RespValue value)
    {
        var pairs = Array(in value);
        if (pairs.Length % 2 != 0) throw new RespireProtocolException("HOTKEYS ranking has an incomplete key/measurement pair.");
        return pairs;
    }

    private static ReadOnlySpan<RespValue> Array(in RespValue value)
    {
        if (value.Type != RespDataType.Array) throw new RespireProtocolException("HOTKEYS value must be an array.");
        return value.AsArray();
    }

    private static byte[] Key(in RespValue value)
    {
        RequireString(in value);
        return value.AsSpan().ToArray();
    }

    private static void RequireString(in RespValue value)
    {
        if (value.Type is not (RespDataType.BulkString or RespDataType.SimpleString or RespDataType.VerbatimString))
            throw new RespireProtocolException("HOTKEYS key or field name must be a string.");
    }
}
