using System.Diagnostics.Metrics;

namespace Respire.Coordination;

internal static class RateLimiterMetrics
{
    private static readonly Meter Meter = new("Respire.Coordination.RateLimiting");
    // Publication invokes listener callbacks. Retry failures without poisoning the type initializer.
    private static readonly Lazy<Counter<long>> Allowed = new(
        static () => Meter.CreateCounter<long>("respire.rate_limit.allowed", "{request}"),
        LazyThreadSafetyMode.PublicationOnly);
    private static readonly Lazy<Counter<long>> Blocked = new(
        static () => Meter.CreateCounter<long>("respire.rate_limit.blocked", "{request}"),
        LazyThreadSafetyMode.PublicationOnly);

    internal static void Record(bool acquired, bool denyList)
    {
        try
        {
            if (acquired) Allowed.Value.Add(1);
            else Blocked.Value.Add(1, new KeyValuePair<string, object?>("reason", denyList ? "deny_list" : "capacity"));
        }
        catch (Exception)
        {
            // A failing metrics listener must not change an already completed admission.
        }
    }
}
