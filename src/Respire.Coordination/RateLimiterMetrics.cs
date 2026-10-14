using System.Diagnostics.Metrics;

namespace Respire.Coordination;

internal static class RateLimiterMetrics
{
    private static readonly Meter Meter = new("Respire.Coordination.RateLimiting");
    private static readonly Counter<long> Allowed = Meter.CreateCounter<long>("respire.rate_limit.allowed", "{request}");
    private static readonly Counter<long> Blocked = Meter.CreateCounter<long>("respire.rate_limit.blocked", "{request}");

    internal static void Record(bool acquired, bool denyList)
    {
        try
        {
            if (acquired) Allowed.Add(1);
            else Blocked.Add(1, new KeyValuePair<string, object?>("reason", denyList ? "deny_list" : "capacity"));
        }
        catch (Exception)
        {
            // A failing metrics listener must not change an already completed admission.
        }
    }
}
