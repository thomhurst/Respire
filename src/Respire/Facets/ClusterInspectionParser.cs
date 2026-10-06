using System.Globalization;
using Respire.Protocol;

namespace Respire;

internal static class ClusterInspectionParser
{
    private static string FieldContext(string? field)
        => field is null ? "Cluster inspection value" : $"Cluster inspection field '{field}'";

    internal static string Text(in RespValue value, string? field = null)
    {
        if (value.Type is not (RespDataType.SimpleString or RespDataType.BulkString or RespDataType.VerbatimString))
            throw new RespireProtocolException($"{FieldContext(field)} must be a string.");
        return value.AsString();
    }

    private static long Integer(in RespValue value, string? field = null)
    {
        if (value.Type != RespDataType.Integer) throw new RespireProtocolException($"{FieldContext(field)} must be an integer.");
        return value.AsInteger();
    }

    internal static long NonnegativeInteger(in RespValue value, string? field = null)
    {
        var number = Integer(in value, field);
        if (number < 0) throw new RespireProtocolException($"{FieldContext(field)} must not be negative.");
        return number;
    }

    internal static int Slot(in RespValue value) => CheckedSlot(Integer(in value));

    private static int CheckedSlot(long value)
        => value is >= 0 and < 16384 ? (int)value : throw new RespireProtocolException("Cluster slot must be between 0 and 16383.");

    private static ReadOnlySpan<RespValue> Array(in RespValue value)
    {
        if (value.Type != RespDataType.Array) throw new RespireProtocolException("Cluster inspection list must be an array.");
        return value.AsArray();
    }

    private static Dictionary<string, RespValue> Fields(in RespValue value)
    {
        if (value.Type is not (RespDataType.Array or RespDataType.Map))
            throw new RespireProtocolException("Cluster inspection fields must be a map or an array of pairs.");
        var items = value.AsArray();
        if (items.Length % 2 != 0) throw new RespireProtocolException("Cluster inspection field/value pair is incomplete.");
        var result = new Dictionary<string, RespValue>(items.Length / 2, StringComparer.Ordinal);
        for (var index = 0; index < items.Length; index += 2)
            if (!result.TryAdd(Text(in items[index]), items[index + 1]))
                throw new RespireProtocolException("Cluster inspection contains duplicate fields.");
        return result;
    }

    private static RespValue Take(Dictionary<string, RespValue> fields, string name)
        => fields.Remove(name, out var value) ? value : throw new RespireProtocolException($"Cluster inspection is missing field '{name}'.");

    private static string? OptionalText(Dictionary<string, RespValue> fields, string name)
        => fields.Remove(name, out var value) && !value.IsNull ? Text(in value, name) : null;

    private static long? OptionalCount(Dictionary<string, RespValue> fields, string name)
        => fields.Remove(name, out var value) ? NonnegativeInteger(in value, name) : null;

    private static int? OptionalPort(Dictionary<string, RespValue> fields, string name)
    {
        var value = OptionalCount(fields, name);
        if (value > 65535) throw new RespireProtocolException($"Cluster port field '{name}' must be between 0 and 65535.");
        return (int?)value;
    }

    private static Dictionary<string, RespireResult> OwnRemaining(Dictionary<string, RespValue> fields)
    {
        var result = new Dictionary<string, RespireResult>(fields.Count, StringComparer.Ordinal);
        foreach (var (name, value) in fields) result.Add(name, new RespireResult(value.ToOwned()));
        return result;
    }

    internal static RespireClusterShard[] Shards(in RespValue value)
    {
        var items = Array(in value);
        var result = new RespireClusterShard[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            var fields = Fields(in items[index]);
            var slots = SlotRanges(Take(fields, "slots"));
            var nodes = Array(Take(fields, "nodes"));
            var members = new RespireClusterShardNode[nodes.Length];
            for (var member = 0; member < nodes.Length; member++) members[member] = ShardNode(in nodes[member]);
            result[index] = new(slots, members, OwnRemaining(fields));
        }
        return result;
    }

    private static RespireClusterSlotRange[] SlotRanges(in RespValue value)
    {
        var slots = Array(in value);
        if (slots.Length % 2 != 0) throw new RespireProtocolException("Cluster slot ranges must contain start/end pairs.");
        var result = new RespireClusterSlotRange[slots.Length / 2];
        for (var index = 0; index < result.Length; index++)
            result[index] = Range(Slot(in slots[index * 2]), Slot(in slots[index * 2 + 1]));
        return result;
    }

    private static RespireClusterSlotRange Range(int start, int end)
        => start <= end ? new(start, end) : throw new RespireProtocolException("Cluster slot range is reversed.");

    private static RespireClusterShardNode ShardNode(in RespValue value)
    {
        var fields = Fields(in value);
        var id = Text(Take(fields, "id"), "id");
        var role = Text(Take(fields, "role"), "role");
        var health = Text(Take(fields, "health"), "health");
        var offset = Integer(Take(fields, "replication-offset"), "replication-offset");
        var endpoint = OptionalText(fields, "endpoint");
        var ip = OptionalText(fields, "ip");
        var hostname = OptionalText(fields, "hostname");
        var port = OptionalPort(fields, "port");
        var tlsPort = OptionalPort(fields, "tls-port");
        return new(id, role, health, offset, endpoint, ip, hostname, port, tlsPort, OwnRemaining(fields));
    }

    internal static RespireClusterLink[] Links(in RespValue value)
    {
        var items = Array(in value);
        var result = new RespireClusterLink[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            var fields = Fields(in items[index]);
            var direction = Text(Take(fields, "direction"), "direction");
            var node = Text(Take(fields, "node"), "node");
            var created = NonnegativeInteger(Take(fields, "create-time"), "create-time");
            var events = Text(Take(fields, "events"), "events");
            var allocated = NonnegativeInteger(Take(fields, "send-buffer-allocated"), "send-buffer-allocated");
            var used = NonnegativeInteger(Take(fields, "send-buffer-used"), "send-buffer-used");
            result[index] = new(direction, node, created, events, allocated, used, OwnRemaining(fields));
        }
        return result;
    }

    internal static RespireClusterSlotStats[] SlotStats(in RespValue value)
    {
        var items = Array(in value);
        var result = new RespireClusterSlotStats[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            var row = Array(in items[index]);
            if (row.Length != 2) throw new RespireProtocolException("Cluster slot statistics must contain a slot and a field map.");
            var slot = Slot(in row[0]);
            var fields = Fields(in row[1]);
            var keys = NonnegativeInteger(Take(fields, "key-count"), "key-count");
            var memory = OptionalCount(fields, "memory-bytes");
            var cpu = OptionalCount(fields, "cpu-usec");
            var inbound = OptionalCount(fields, "network-bytes-in");
            var outbound = OptionalCount(fields, "network-bytes-out");
            result[index] = new(slot, keys, memory, cpu, inbound, outbound, OwnRemaining(fields));
        }
        return result;
    }

    internal static RespireClusterInfo Info(in RespValue value)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Text(in value).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon < 1 || !fields.TryAdd(line[..colon], line[(colon + 1)..]))
                throw new RespireProtocolException("CLUSTER INFO contains a malformed or duplicate field.");
        }
        if (!fields.TryGetValue("cluster_state", out var state)) throw new RespireProtocolException("CLUSTER INFO is missing cluster_state.");
        return new(state, InfoCount(fields, "cluster_slots_assigned"), InfoCount(fields, "cluster_slots_ok"),
            InfoCount(fields, "cluster_slots_pfail"), InfoCount(fields, "cluster_slots_fail"),
            InfoCount(fields, "cluster_known_nodes"), InfoCount(fields, "cluster_size"),
            InfoEpoch(fields, "cluster_current_epoch"), InfoEpoch(fields, "cluster_my_epoch"), fields);
    }

    private static long? InfoCount(Dictionary<string, string> fields, string name)
        => fields.TryGetValue(name, out var value) ? TextCount(value, name) : null;

    private static ulong? InfoEpoch(Dictionary<string, string> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value)) return null;
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
            throw new RespireProtocolException($"{FieldContext(name)} must be an unsigned 64-bit integer.");
        return epoch;
    }

    private static long TextCount(string value, string? field = null)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= 0
            ? count : throw new RespireProtocolException($"{FieldContext(field)} must be a nonnegative integer.");

    private static ulong ConfigurationEpoch(string value)
        => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            ? epoch : throw new RespireProtocolException("Cluster configuration epoch must be an unsigned 64-bit integer.");

    internal static RespireClusterNode[] Nodes(in RespValue value)
    {
        var lines = Text(in value).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new RespireClusterNode[lines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            var fields = lines[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 8) throw new RespireProtocolException("CLUSTER NODES contains an incomplete row.");
            List<RespireClusterSlotRange> slots = [];
            List<RespireClusterSlotTransition> transitions = [];
            List<string> additional = [];
            foreach (var token in fields.AsSpan(8)) ParseSlotToken(token, slots, transitions, additional);
            result[index] = new(fields[0], fields[1], fields[2].Split(','), fields[3] == "-" ? null : fields[3],
                TextCount(fields[4], "ping-sent"), TextCount(fields[5], "pong-recv"), ConfigurationEpoch(fields[6]), fields[7],
                slots.ToArray(), transitions.ToArray(), additional.ToArray());
        }
        return result;
    }

    private static void ParseSlotToken(string token, List<RespireClusterSlotRange> slots,
        List<RespireClusterSlotTransition> transitions, List<string> additional)
    {
        if (token.StartsWith('['))
        {
            var separator = token.IndexOf("->-", StringComparison.Ordinal);
            var direction = "migrating";
            if (separator < 0)
            {
                separator = token.IndexOf("-<-", StringComparison.Ordinal);
                direction = "importing";
            }
            if (separator > 1 && token.EndsWith(']') && separator + 3 < token.Length - 1)
            {
                transitions.Add(new(CheckedSlot(TextCount(token[1..separator])), direction, token[(separator + 3)..^1]));
                return;
            }
            // Preserve future annotations instead of treating them as owned slots.
            additional.Add(token);
            return;
        }
        if (token.Length > 0 && char.IsAsciiDigit(token[0]))
        {
            var dash = token.IndexOf('-');
            var start = CheckedSlot(TextCount(dash < 0 ? token : token[..dash]));
            var end = dash < 0 ? start : CheckedSlot(TextCount(token[(dash + 1)..]));
            slots.Add(Range(start, end));
            return;
        }
        additional.Add(token);
    }
}
