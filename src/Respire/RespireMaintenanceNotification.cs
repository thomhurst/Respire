using System.Globalization;
using Respire.Protocol;

namespace Respire;

/// <summary>Kind of Redis server maintenance notification.</summary>
public enum RespireMaintenanceNotificationType
{
    /// <summary>Redis endpoint is moving to a new address.</summary>
    Moving,
    /// <summary>Shard migration is about to begin.</summary>
    Migrating,
    /// <summary>Shard migration has completed.</summary>
    Migrated,
    /// <summary>Shard failover is about to begin.</summary>
    FailingOver,
    /// <summary>Shard failover has completed.</summary>
    FailedOver,
    /// <summary>Cluster slot migration is about to begin.</summary>
    Smigrating,
    /// <summary>Cluster slot migration has completed.</summary>
    Smigrated,
}

/// <summary>A validated Redis maintenance notification copied from its RESP3 push frame.</summary>
/// <param name="Type">Notification kind.</param>
/// <param name="SequenceId">Server sequence identifier.</param>
/// <param name="Details">Remaining notification fields as text, in wire order.</param>
public sealed record RespireMaintenanceNotification(
    RespireMaintenanceNotificationType Type,
    long SequenceId,
    IReadOnlyList<string> Details)
{
    internal bool StartsMaintenance => Type is RespireMaintenanceNotificationType.Moving
        or RespireMaintenanceNotificationType.Migrating
        or RespireMaintenanceNotificationType.FailingOver
        or RespireMaintenanceNotificationType.Smigrating;

    internal bool EndsMaintenance => Type is RespireMaintenanceNotificationType.Migrated
        or RespireMaintenanceNotificationType.FailedOver
        or RespireMaintenanceNotificationType.Smigrated;

    internal static bool TryCreate(in RespValue value, out RespireMaintenanceNotification? notification)
    {
        notification = null;
        if (value.Type != RespDataType.Push)
            return false;

        var elements = value.AsArray();
        if (elements.Length < 3 || !TryReadText(in elements[0], out var name)
            || !TryReadSequence(in elements[1], out var sequenceId)
            || !TryGetType(name, out var type)
            || elements.Length < MinimumLength(type))
            return false;

        var details = new List<string>(elements.Length - 2);
        for (var index = 2; index < elements.Length; index++)
        {
            if (!TryAppendDetails(in elements[index], details, depth: 0) || details.Count > 128)
                return false;
        }

        if (!ValidateDetails(type, details))
            return false;

        notification = new RespireMaintenanceNotification(type, sequenceId, details.AsReadOnly());
        return true;
    }

    private static int MinimumLength(RespireMaintenanceNotificationType type) => type switch
    {
        RespireMaintenanceNotificationType.Moving => 4,
        RespireMaintenanceNotificationType.Migrating or RespireMaintenanceNotificationType.FailingOver => 4,
        RespireMaintenanceNotificationType.Migrated or RespireMaintenanceNotificationType.FailedOver => 3,
        RespireMaintenanceNotificationType.Smigrating => 3,
        RespireMaintenanceNotificationType.Smigrated => 5,
        _ => int.MaxValue,
    };

    private static bool TryReadSequence(in RespValue value, out long sequenceId)
    {
        sequenceId = 0;
        if (value.Type == RespDataType.Integer)
        {
            sequenceId = value.AsInteger();
            return sequenceId >= 0;
        }

        return TryReadText(in value, out var text)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out sequenceId)
            && sequenceId >= 0;
    }

    private static bool TryReadText(in RespValue value, out string text)
    {
        if (value.Type is RespDataType.BulkString or RespDataType.SimpleString or RespDataType.Integer)
        {
            text = value.AsString();
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static bool TryAppendDetails(in RespValue value, List<string> details, int depth)
    {
        if (depth > 4)
            return false;

        if (value.Type is RespDataType.Array or RespDataType.Set)
        {
            var elements = value.AsArray();
            foreach (ref readonly var element in elements)
            {
                if (!TryAppendDetails(in element, details, depth + 1))
                    return false;
            }
            return true;
        }

        if (!TryReadText(in value, out var text) || string.IsNullOrWhiteSpace(text))
            return false;

        details.Add(text);
        return details.Count <= 128;
    }

    private static bool ValidateDetails(RespireMaintenanceNotificationType type, List<string> details)
    {
        if (details.Count == 0)
            return false;

        switch (type)
        {
            case RespireMaintenanceNotificationType.Moving:
                return details.Count == 2 && IsNonNegativeSeconds(details[0]) && IsEndpoint(details[1]);
            case RespireMaintenanceNotificationType.Migrating:
            case RespireMaintenanceNotificationType.FailingOver:
                return details.Count >= 2 && IsNonNegativeSeconds(details[0]);
            case RespireMaintenanceNotificationType.Migrated:
            case RespireMaintenanceNotificationType.FailedOver:
                return true;
            case RespireMaintenanceNotificationType.Smigrating:
                return details.All(IsSlotRange);
            case RespireMaintenanceNotificationType.Smigrated:
                return details.Count >= 3 && IsEndpoint(details[0]) && IsEndpoint(details[1])
                    && details.Skip(2).All(IsSlotRange);
            default:
                return false;
        }
    }

    private static bool IsNonNegativeSeconds(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds) && seconds >= 0;

    private static bool IsEndpoint(string value)
    {
        try
        {
            var endpoint = RespireEndpoint.Parse(value);
            return !string.IsNullOrWhiteSpace(endpoint.Host) && endpoint.Port is >= 1 and <= 65535;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSlotRange(string value)
    {
        var separator = value.IndexOf('-');
        if (separator < 0)
            return IsSlot(value);

        return value.IndexOf('-', separator + 1) < 0
            && IsSlot(value.AsSpan(0, separator))
            && IsSlot(value.AsSpan(separator + 1));
    }

    private static bool IsSlot(string value) => IsSlot(value.AsSpan());

    private static bool IsSlot(ReadOnlySpan<char> value)
        => ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) && slot <= 16383;

    private static bool TryGetType(string name, out RespireMaintenanceNotificationType type)
    {
        switch (name)
        {
            case "MOVING": type = RespireMaintenanceNotificationType.Moving; return true;
            case "MIGRATING": type = RespireMaintenanceNotificationType.Migrating; return true;
            case "MIGRATED": type = RespireMaintenanceNotificationType.Migrated; return true;
            case "FAILING_OVER": type = RespireMaintenanceNotificationType.FailingOver; return true;
            case "FAILED_OVER": type = RespireMaintenanceNotificationType.FailedOver; return true;
            case "SMIGRATING": type = RespireMaintenanceNotificationType.Smigrating; return true;
            case "SMIGRATED": type = RespireMaintenanceNotificationType.Smigrated; return true;
            default: type = default; return false;
        }
    }
}
