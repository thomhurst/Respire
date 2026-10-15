using System.Diagnostics.Metrics;

namespace Respire.Coordination;

internal static class LeaderElectionMetrics
{
    private static readonly Meter Meter = new("Respire.Coordination.LeaderElection");
    private static readonly Lazy<Counter<long>> Changes = new(
        static () => Meter.CreateCounter<long>("respire.leader_election.changes", "{transition}"),
        LazyThreadSafetyMode.PublicationOnly);
    private static readonly Lazy<Counter<long>> Failures = new(
        static () => Meter.CreateCounter<long>("respire.leader_election.acquire_failures", "{attempt}"),
        LazyThreadSafetyMode.PublicationOnly);

    internal static void Changed(bool elected)
    {
        try { Changes.Value.Add(1, new KeyValuePair<string, object?>("state", elected ? "elected" : "revoked")); }
        catch (Exception) { /* Listener failures must not change lease ownership. */ }
    }

    internal static void FailedAttempt()
    {
        try { Failures.Value.Add(1); }
        catch (Exception) { /* Listener failures must not stop acquisition retries. */ }
    }
}
