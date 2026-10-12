using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Respire.Internal;

internal static partial class RespireTelemetry
{
    private static class CommandRetryInstruments
    {
        private static readonly Lazy<Counter<long>> Instrument = new(static () => Meter.CreateCounter<long>(
            "respire.command.retries", "{retry}", "Accepted retry attempts and logical retry outcomes."),
            LazyThreadSafetyMode.PublicationOnly);
        internal static Counter<long> Retries => Instrument.Value;
    }

    internal static void RecordCommandRetry(RespireCommandRetryCategory category, string outcome)
    {
        try
        {
            var instrument = CommandRetryInstruments.Retries;
            if (!IsMetricEnabled(RespireMetricGroups.Resiliency, instrument)) return;
            var tags = new TagList { LibraryTag, SystemTag,
                new("respire.command.retry.category", category.ToString()), new("respire.command.retry.outcome", outcome) };
            instrument.Add(1, in tags);
        }
        catch (Exception) { /* Exporters cannot change admission, retry eligibility or final publication. */ }
    }
}
