using System.Globalization;
using Respire.Protocol;

namespace Respire;

internal static class ClusterAdministrationParser
{
    internal static RespireClusterEpochResult Epoch(in RespValue value)
    {
        var text = ClusterInspectionParser.Text(in value);
        var separator = text.IndexOf(' ');
        if (separator < 0 || !ulong.TryParse(text.AsSpan(separator + 1), NumberStyles.None,
            CultureInfo.InvariantCulture, out var epoch))
            throw new RespireProtocolException("CLUSTER BUMPEPOCH must return a status and unsigned epoch.");
        return text.AsSpan(0, separator) switch
        {
            "BUMPED" => new(true, epoch),
            "STILL" => new(false, epoch),
            _ => throw new RespireProtocolException("CLUSTER BUMPEPOCH returned an unknown status."),
        };
    }

    internal static RespireClusterNode[] Replicas(in RespValue value)
    {
        var rows = Array(in value);
        var result = new RespireClusterNode[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            try
            {
                var nodes = ClusterInspectionParser.Nodes(in rows[index]);
                if (nodes.Length != 1) throw new RespireProtocolException("CLUSTER REPLICAS requires one node per row.");
                result[index] = nodes[0];
            }
            catch (RespireProtocolException error)
            {
                throw new RespireProtocolException($"CLUSTER REPLICAS row {index}: {error.Message}", error);
            }
        }
        return result;
    }

    internal static RespireClusterSlotMapping[] Slots(in RespValue value)
    {
        var rows = Array(in value);
        var result = new RespireClusterSlotMapping[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            try
            {
                var row = Array(in rows[index]);
                if (row.Length < 3) throw new RespireProtocolException("CLUSTER SLOTS requires a range and primary.");
                var start = ClusterInspectionParser.Slot(in row[0]);
                var end = ClusterInspectionParser.Slot(in row[1]);
                if (start > end) throw new RespireProtocolException("CLUSTER SLOTS range must not be reversed.");
                var primary = Node(in row[2]);
                var replicas = new RespireClusterSlotNode[row.Length - 3];
                for (var member = 0; member < replicas.Length; member++) replicas[member] = Node(in row[member + 3]);
                result[index] = new(new(start, end), primary, replicas);
            }
            catch (RespireProtocolException error)
            {
                throw new RespireProtocolException($"CLUSTER SLOTS row {index}: {error.Message}", error);
            }
        }
        return result;
    }

    private static RespireClusterSlotNode Node(in RespValue value)
    {
        var fields = Array(in value);
        if (fields.Length < 2) throw new RespireProtocolException("CLUSTER SLOTS node requires an endpoint and port.");
        var endpoint = fields[0].IsNull ? null : ClusterInspectionParser.Text(in fields[0]);
        var port = ClusterInspectionParser.NonnegativeInteger(in fields[1]);
        if (port > 65535) throw new RespireProtocolException("CLUSTER SLOTS port must be between 0 and 65535.");
        var id = fields.Length < 3 ? null : ClusterInspectionParser.Text(in fields[2]);
        var metadata = new Dictionary<string, RespireResult>(StringComparer.Ordinal);
        if (fields.Length >= 4)
        {
            if (fields[3].Type is not (RespDataType.Array or RespDataType.Map))
                throw new RespireProtocolException("CLUSTER SLOTS metadata requires a map or array of pairs.");
            var pairs = fields[3].AsArray();
            if (pairs.Length % 2 != 0) throw new RespireProtocolException("CLUSTER SLOTS metadata contains an incomplete pair.");
            for (var index = 0; index < pairs.Length; index += 2)
            {
                var name = ClusterInspectionParser.Text(in pairs[index]);
                if (!metadata.TryAdd(name, new RespireResult(pairs[index + 1].ToOwned())))
                    throw new RespireProtocolException("CLUSTER SLOTS metadata contains duplicate fields.");
            }
        }
        var additional = new RespireResult[Math.Max(0, fields.Length - 4)];
        for (var index = 0; index < additional.Length; index++) additional[index] = new(fields[index + 4].ToOwned());
        return new(endpoint, (int)port, id, metadata, additional);
    }

    private static ReadOnlySpan<RespValue> Array(in RespValue value)
        => value.Type == RespDataType.Array ? value.AsArray()
            : throw new RespireProtocolException("Cluster administration list must be an array.");
}
