using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;

namespace Respire.Streaming;

/// <summary>Persists recurring schedules and atomically appends due ticks to a typed job queue.</summary>
/// <remarks>Schedulers may run concurrently on multiple instances. Keep their UTC clocks synchronized.
/// Queue processing remains at least once. Protect all queue keys from eviction or independent deletion.</remarks>
public sealed class RespireJobScheduler<T>
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IRespireClient _client;
    private readonly string[] _keys;
    private readonly Func<T, byte[]> _serialize;
    private readonly TimeProvider _timeProvider;

    internal RespireJobScheduler(IRespireClient client, string stream, Func<T, byte[]> serialize, TimeProvider timeProvider)
    {
        _client = client;
        _keys = [stream, stream + ":recurring", stream + ":recurring-due"];
        JobQueueScheduling.ValidateKeys(client, _keys);
        _serialize = serialize;
        _timeProvider = timeProvider;
    }

    /// <summary>Creates or replaces a schedule and payload. Its first tick is strictly after the current UTC time.</summary>
    /// <remarks>Every upsert starts a new revision and resets the cadence; it does not retract queued jobs.
    /// Configure schedules through one control plane, rather than upserting on every worker startup.</remarks>
    public async ValueTask UpsertAsync(string scheduleId, T payload, RespireJobSchedule schedule,
        CancellationToken cancellationToken = default)
    {
        ValidateId(scheduleId);
        ArgumentNullException.ThrowIfNull(schedule);
        var cron = schedule.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var interval = schedule.Interval?.Ticks / TimeSpan.TicksPerMillisecond;
        var next = Next(now, interval, cron) ?? throw new ArgumentException("The schedule has no future occurrence.", nameof(schedule));
        var definition = new RecurringJobDefinition(Guid.NewGuid().ToString("N"), interval, schedule.Cron,
            schedule.MissedTickPolicy, _serialize(payload) ?? throw new InvalidOperationException("The job serializer returned null."));
        var json = JsonSerializer.Serialize(definition, RecurringJobJsonContext.Default.RecurringJobDefinition);
        await _client.Scripts.ExecuteIntegerAsync(RecurringJobScripts.Upsert, [.. _keys],
            [scheduleId, json, next], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a schedule. Already enqueued jobs remain available for processing.</summary>
    public async ValueTask<bool> RemoveAsync(string scheduleId, CancellationToken cancellationToken = default)
    {
        ValidateId(scheduleId);
        return await _client.Scripts.ExecuteIntegerAsync(RecurringJobScripts.Remove, [.. _keys],
            [scheduleId], cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Runs a bounded scheduling pass and returns the number of jobs this instance enqueued.</summary>
    /// <remarks>Examines at most maximumTicks tick candidates (1 to 256), using one UTC time snapshot.
    /// Catch-up backlog persists for later passes. Repeating a pass after a lost reply cannot duplicate a committed tick.</remarks>
    public async ValueTask<int> RunDueAsync(int maximumTicks = 64, CancellationToken cancellationToken = default)
    {
        if (maximumTicks is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maximumTicks));
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var enqueued = 0;
        for (var examined = 0; examined < maximumTicks;)
        {
            using var due = await _client.Scripts.ExecuteAsync(RecurringJobScripts.ReadDue, [.. _keys],
                [now, maximumTicks - examined], cancellationToken).ConfigureAwait(false);
            if (due.Count == 0) break;
            for (var i = 0; i < due.Count; i++)
            {
                examined++;
                var row = due[i];
                var scheduleId = row[0].AsString();
                var json = row[1].AsString();
                var original = long.Parse(row[2].AsString(), CultureInfo.InvariantCulture);
                var definition = JsonSerializer.Deserialize(json, RecurringJobJsonContext.Default.RecurringJobDefinition)
                    ?? throw new InvalidOperationException("The recurring schedule definition is invalid.");
                var cron = definition.Cron is { } expression ? CronExpression.Parse(expression) : null;
                var tick = original;
                if (definition.Policy == RespireJobMissedTickPolicy.Skip)
                {
                    tick = definition.Interval is { } interval
                        ? original + ((now - original) / interval * interval)
                        : new DateTimeOffset(cron!.GetPreviousOccurrence(DateTimeOffset.FromUnixTimeMilliseconds(now).UtcDateTime,
                            inclusive: true)!.Value).ToUnixTimeMilliseconds();
                }
                var next = Next(tick, definition.Interval, cron);
                var jobId = "recurring:" + definition.Revision + ":" + tick.ToString(CultureInfo.InvariantCulture);
                enqueued += (int)await _client.Scripts.ExecuteIntegerAsync(RecurringJobScripts.CommitTick, [.. _keys],
                    [scheduleId, json, original, next?.ToString(CultureInfo.InvariantCulture) ?? "", jobId,
                        definition.Payload, tick], cancellationToken).ConfigureAwait(false);
            }
        }
        return enqueued;
    }

    private static long? Next(long after, long? interval, CronExpression? cron)
    {
        if (interval is { } milliseconds)
        {
            // DateTimeOffset's upper limit also keeps sorted-set scores exactly representable.
            var maximum = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
            return milliseconds > maximum - after ? null : after + milliseconds;
        }
        var next = cron!.GetNextOccurrence(DateTimeOffset.FromUnixTimeMilliseconds(after).UtcDateTime);
        return next is { } value ? new DateTimeOffset(value).ToUnixTimeMilliseconds() : null;
    }

    private static void ValidateId(string scheduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        _ = StrictUtf8.GetByteCount(scheduleId);
    }
}

internal sealed record RecurringJobDefinition(string Revision, long? Interval, string? Cron,
    RespireJobMissedTickPolicy Policy, byte[] Payload);

[JsonSerializable(typeof(RecurringJobDefinition))]
internal sealed partial class RecurringJobJsonContext : JsonSerializerContext;
