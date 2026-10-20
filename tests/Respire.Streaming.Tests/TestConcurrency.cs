using Respire.Streaming.Tests;
using TUnit.Core;
using TUnit.Core.Interfaces;

[assembly: ParallelLimiter<StreamingTestParallelLimit>]

namespace Respire.Streaming.Tests;

// Bound competition between hosted workers and tests with real-time deadlines.
// Keep the bound independent of CPU count: tests also share Redis and Docker resources.
// See README.md and #1938 for the concurrency comparison.
public sealed class StreamingTestParallelLimit : IParallelLimit
{
    public int Limit => 2;
}
