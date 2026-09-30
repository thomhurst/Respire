using System.Globalization;
using Respire.Protocol;

namespace Respire.Networking;

// Retained fields are copied before the receive loop releases the RESP frame.
internal sealed record MaintenanceNotification(string Kind, long SequenceId, long? Seconds = null,
    RespireEndpoint? Target = null, MaintenanceSlotMigration[]? Migrations = null)
{
    internal bool IsCompletion => Kind is "MIGRATED" or "FAILED_OVER" or "SMIGRATED";
    internal string Family => Kind switch
    {
        "MIGRATED" => "MIGRATING",
        "FAILED_OVER" => "FAILING_OVER",
        "SMIGRATED" => "SMIGRATING",
        _ => Kind,
    };

    internal static MaintenanceNotification? Parse(in RespValue value)
    {
        if (value.Type != RespDataType.Push) return null;
        var items = value.AsArray();
        if (items.Length < 2 || !IsString(items[0]) || items[0].AsSpan().Length > 12
            || items[1].Type != RespDataType.Integer) return null;
        var kind = items[0].AsString();
        var sequence = items[1].AsInteger();
        if (sequence < 0) return null;
        switch (kind)
        {
            case "MOVING":
                if (items.Length != 4 || !TrySeconds(items[2], out var grace)) return null;
                RespireEndpoint? target = null;
                if (!items[3].IsNull)
                {
                    if (!TryEndpoint(items[3], out var endpoint)) return null;
                    target = endpoint;
                }
                return new(kind, sequence, grace, target);
            case "MIGRATING":
            case "FAILING_OVER":
                if (items.Length is < 3 or > 4 || !TrySeconds(items[2], out var lead)
                    || (items.Length == 4 && !ValidShards(items[3]))) return null;
                return new(kind, sequence, lead);
            case "MIGRATED":
            case "FAILED_OVER":
                if (items.Length is < 2 or > 3 || (items.Length == 3 && !ValidShards(items[2]))) return null;
                return new(kind, sequence);
            case "SMIGRATING":
                if (items.Length < 3) return null;
                for (var i = 2; i < items.Length; i++)
                    if (!ValidSlots(items[i])) return null;
                return new(kind, sequence);
            case "SMIGRATED":
                if (items.Length != 3 || items[2].Type != RespDataType.Array) return null;
                var triplets = items[2].AsArray();
                if (triplets.Length is 0 or > 16384) return null;
                var migrations = new MaintenanceSlotMigration[triplets.Length];
                for (var i = 0; i < triplets.Length; i++)
                {
                    if (triplets[i].Type != RespDataType.Array) return null;
                    var triplet = triplets[i].AsArray();
                    if (triplet.Length != 3 || !TryEndpoint(triplet[0], out var source)
                        || !TryEndpoint(triplet[1], out var destination) || !ValidSlots(triplet[2])) return null;
                    migrations[i] = new(source, destination, triplet[2].AsString()!);
                }
                return new(kind, sequence, Migrations: migrations);
            default:
                return null;
        }
    }

    private static bool IsString(in RespValue value)
        => value.Type is RespDataType.SimpleString or RespDataType.BulkString;

    private static bool TrySeconds(in RespValue value, out long seconds)
    {
        seconds = value.Type == RespDataType.Integer ? value.AsInteger() : -1;
        return seconds >= 0;
    }

    private static bool ValidShards(in RespValue value)
    {
        if (IsString(value)) return true;
        if (value.Type != RespDataType.Array) return false;
        foreach (var shard in value.AsArray())
            if (!IsString(shard) && shard.Type != RespDataType.Integer) return false;
        return true;
    }

    private static bool ValidSlots(in RespValue value)
    {
        if (!IsString(value)) return false;
        var text = value.AsString();
        if (string.IsNullOrEmpty(text) || text.Length > 100_000) return false;
        foreach (var range in text.Split(','))
        {
            var dash = range.IndexOf('-');
            var startText = dash < 0 ? range.AsSpan() : range.AsSpan(0, dash);
            if (!int.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                || start is < 0 or >= 16384) return false;
            if (dash >= 0 && (!int.TryParse(range.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var end)
                || end < start || end >= 16384)) return false;
        }
        return true;
    }

    private static bool TryEndpoint(in RespValue value, out RespireEndpoint endpoint)
    {
        endpoint = default;
        if (!IsString(value)) return false;
        var text = value.AsString();
        if (string.IsNullOrWhiteSpace(text)) return false;
        // The wire endpoint includes a port. Split at the last colon to accept both bracketed
        // and unbracketed IPv6 without mistaking its final component for a default-port host.
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535) return false;
        var host = text[..colon];
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || host.Contains('[') || host.Contains(']')) return false;
        endpoint = new(host, port);
        return true;
    }
}

internal readonly record struct MaintenanceSlotMigration(RespireEndpoint Source, RespireEndpoint Target, string Slots);
