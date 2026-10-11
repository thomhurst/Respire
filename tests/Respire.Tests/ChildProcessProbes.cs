using TUnit.Core.Interfaces;

namespace Respire.Tests;

// Tests whose measurement runs in a fresh child process (AsyncFlushSignalTests.RunProbeAsync).
// The child owns its thread pool, GC and no-GC region, so these need no runner-wide isolation.
// Bound how many run at once so runtime startup does not oversubscribe small CI runners.
public sealed class ChildProcessProbes : IParallelLimit
{
    public int Limit => Math.Max(2, Environment.ProcessorCount / 2);
}
