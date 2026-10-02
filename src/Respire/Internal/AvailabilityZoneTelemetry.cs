using System.Diagnostics.Metrics;

namespace Respire.Internal;

// Accepted reads increment a cached counter without invoking user callbacks under the
// connection write gate. Meter listeners observe totals later, outside transport locks.
internal static class AvailabilityZoneTelemetry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Counter> Zones = new(StringComparer.Ordinal);
    private static readonly Counter Unknown = new();
    private static readonly Counter Other = new();
    private const int MaximumZones = 64;

    internal static readonly ObservableCounter<long> Reads = RespireTelemetry.Meter.CreateObservableCounter(
        "respire.read.availability_zone", Observe, "{read}",
        "Accepted read commands by server availability zone, when ClientAvailabilityZone is configured.");

    internal sealed class Counter
    {
        private long _value;
        internal void Increment() => Interlocked.Increment(ref _value);
        internal long Value => Interlocked.Read(ref _value);
    }

    internal static Counter ForZone(string? zone)
    {
        if (zone is null) return Unknown;
        lock (Gate)
        {
            if (Zones.TryGetValue(zone, out var counter)) return counter;
            if (Zones.Count == MaximumZones) return Other;
            counter = new();
            Zones.Add(zone, counter);
            return counter;
        }
    }

    private static IEnumerable<Measurement<long>> Observe()
    {
        KeyValuePair<string, Counter>[] zones;
        lock (Gate) zones = Zones.ToArray();
        foreach (var pair in zones)
            yield return new(pair.Value.Value,
                new KeyValuePair<string, object?>("server.availability_zone", pair.Key),
                new KeyValuePair<string, object?>("respire.availability_zone.status", "known"));
        yield return new(Unknown.Value,
            new KeyValuePair<string, object?>("respire.availability_zone.status", "unknown"));
        yield return new(Other.Value,
            new KeyValuePair<string, object?>("respire.availability_zone.status", "overflow"));
    }
}
