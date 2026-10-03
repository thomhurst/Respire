using System.Diagnostics.Metrics;

namespace Respire.Internal;

// Accepted reads increment a cached counter without invoking user callbacks under the
// connection write gate. Meter listeners observe totals later, outside transport locks.
internal static class AvailabilityZoneTelemetry
{
    private static readonly Registry Counters = new();

    internal static readonly ObservableCounter<long> Reads = RespireTelemetry.Meter.CreateObservableCounter(
        "respire.read.availability_zone", Counters.Observe, "{read}",
        "Accepted read commands by server availability zone, when ClientAvailabilityZone is configured.");

    internal sealed class Counter
    {
        private long _value;
        internal void Increment() => Interlocked.Increment(ref _value);
        internal long Value => Interlocked.Read(ref _value);
    }

    internal static Counter ForZone(string? zone)
        => Counters.ForZone(zone);

    // Production uses one process-wide registry. Separate instances let tests exhaust the
    // budget without permanently consuming other tests' observable metric labels.
    internal sealed class Registry
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Counter> _zones = new(StringComparer.Ordinal);
        private KeyValuePair<string, Counter>[] _snapshot = [];
        private readonly Counter _unknown = new();
        private readonly Counter _overflow = new();
        private const int MaximumZones = 64;
        private const int MaximumZoneNameLength = 128;

        internal Counter ForZone(string? zone)
        {
            if (zone is null) return _unknown;
            if (zone.Length > MaximumZoneNameLength) return _overflow;
            lock (_gate)
            {
                if (_zones.TryGetValue(zone, out var counter)) return counter;
                if (_zones.Count == MaximumZones) return _overflow;
                counter = new();
                _zones.Add(zone, counter);
                // Zone discovery is infrequent and bounded. Publish immutable membership once;
                // collectors read the live counters without locking or copying the dictionary.
                Volatile.Write(ref _snapshot, _zones.ToArray());
                return counter;
            }
        }

        internal IEnumerable<Measurement<long>> Observe()
        {
            var zones = Volatile.Read(ref _snapshot);
            foreach (var pair in zones)
                yield return new(pair.Value.Value,
                    new KeyValuePair<string, object?>("server.availability_zone", pair.Key),
                    new KeyValuePair<string, object?>("respire.availability_zone.status", "known"));
            yield return new(_unknown.Value,
                new KeyValuePair<string, object?>("respire.availability_zone.status", "unknown"));
            yield return new(_overflow.Value,
                new KeyValuePair<string, object?>("respire.availability_zone.status", "overflow"));
        }
    }
}
