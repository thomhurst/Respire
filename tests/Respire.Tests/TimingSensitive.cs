using TUnit.Core.Interfaces;

namespace Respire.Tests;

// Fake-server tests with short real-time deadlines. Limiting how many run at once keeps their
// scheduling budgets from competing with each other without serializing the whole suite.
public sealed class TimingSensitive : IParallelLimit
{
    public int Limit => 2;
}
