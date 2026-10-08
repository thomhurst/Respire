namespace Respire;

internal sealed partial class ClientSideCacheCoordinator
{
    /// <summary>Creates a borrowed view for friend tests; never use it for production coordination.</summary>
    internal TestInspection InspectForTests() => new(this);

    internal readonly ref struct TestInspection(ClientSideCacheCoordinator owner)
    {
        /// <summary>Borrowed gate used to hold a controlled shared-read admission barrier.</summary>
        /// <remarks>Own an EnterScope lease before inspecting gate-protected state; never dispose the gate.</remarks>
        internal Lock SharedReadGate => owner._sharedReadLock;

        /// <summary>Number of distinct keys retained by pending query dependency leases.</summary>
        internal int PendingQueryDependencyCount
        {
            get
            {
                lock (owner._queryLock) return owner._queryDependencies.Count;
            }
        }

        /// <summary>Counts cleared idle storage under the query gate; the copy grants no rent or return ownership.</summary>
        internal (int Leases, int States, int RetainedDependencies) IdleQueryStorage
        {
            get
            {
                lock (owner._queryLock)
                {
                    var retained = 0;
                    for (var state = owner._idleQueryStates; state is not null; state = state.Next)
                        if (!state.Key.Equals(default(RespireKey))) retained++;
                    for (var lease = owner._idleQueryLeases; lease is not null; lease = lease.Next)
                        for (var index = 0; index < lease.Capacity; index++)
                            if (lease.GetDependency(index).State is not null) retained++;
                    return (owner._idleQueryLeaseCount, owner._idleQueryStateCount, retained);
                }
            }
        }
    }
}
