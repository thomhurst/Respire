using System.Globalization;
using Respire.Protocol;

namespace Respire.Networking;

// Retained fields are copied before the receive loop releases the RESP frame.
internal sealed record MaintenanceNotification(string Kind, long SequenceId, long? Seconds = null,
    RespireEndpoint? Target = null, MaintenanceSlotMigration[]? Migrations = null)
{
    internal const string SlotMigratedKind = "SMIGRATED";

    internal bool IsCompletion => Kind is "MIGRATED" or "FAILED_OVER" or SlotMigratedKind;
    // A completed Cluster slot migration that carries source/target/slot triplets.
    internal bool IsSlotMigration => Kind == SlotMigratedKind;
    internal string Family => Kind switch
    {
        "MIGRATED" => "MIGRATING",
        "FAILED_OVER" => "FAILING_OVER",
        "SMIGRATED" => "SMIGRATING",
        _ => Kind,
    };

    // A cheap check of the kind alone, so the receive loop can read the slot fence before
    // Parse scans and copies a large triplet list.
    internal static bool IsSlotMigrationPush(in RespValue value)
    {
        if (value.Type != RespDataType.Push) return false;
        var items = value.AsArray();
        return items.Length > 0 && IsString(items[0]) && items[0].AsSpan().SequenceEqual("SMIGRATED"u8);
    }

    internal static MaintenanceNotification? Parse(in RespValue value)
    {
        if (value.Type != RespDataType.Push) return null;
        var items = value.AsArray();
        if (items.Length < 2 || !IsString(items[0]) || !IsMaintenanceKind(items[0].AsSpan())
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

    private static bool IsMaintenanceKind(ReadOnlySpan<byte> kind)
        => kind.SequenceEqual("MOVING"u8)
            || kind.SequenceEqual("MIGRATING"u8)
            || kind.SequenceEqual("MIGRATED"u8)
            || kind.SequenceEqual("FAILING_OVER"u8)
            || kind.SequenceEqual("FAILED_OVER"u8)
            || kind.SequenceEqual("SMIGRATING"u8)
            || kind.SequenceEqual("SMIGRATED"u8);

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
        // Scan the wire bytes: a large slot list must not allocate a string per range.
        var text = value.AsSpan();
        if (text.IsEmpty || text.Length > 100_000) return false;
        while (true)
        {
            var comma = text.IndexOf((byte)',');
            var range = comma < 0 ? text : text[..comma];
            var dash = range.IndexOf((byte)'-');
            if (!TrySlot(dash < 0 ? range : range[..dash], out var start)
                || (dash >= 0 && (!TrySlot(range[(dash + 1)..], out var end) || end < start))) return false;
            if (comma < 0) return true;
            text = text[(comma + 1)..];
        }
    }

    private static bool TrySlot(ReadOnlySpan<byte> text, out int slot)
    {
        slot = 0;
        if (text.IsEmpty) return false;
        foreach (var digit in text)
        {
            if (digit is < (byte)'0' or > (byte)'9') return false;
            slot = slot * 10 + (digit - '0');
            if (slot >= 16384) return false;
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
