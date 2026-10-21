using TUnit.Core.Interfaces;

namespace Respire.Tests;

// Fake-server tests with short real-time deadlines. Limiting how many run at once keeps their
// scheduling budgets from competing with each other without serializing the whole suite.
// Cluster wire fixtures also share this limit: concurrent full slot maps can cause GC pauses
// longer than their recovery budgets. Operations within a test still execute concurrently.
public sealed class TimingSensitive : IParallelLimit
{
    public int Limit => 2;
}
