using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CleanupTasksTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RepeatedFailureKeepsIdentity(bool aggregate)
    {
        Exception failure = new InvalidOperationException("cleanup");
        if (aggregate) failure = new AggregateException(failure);
        try
        {
            await CleanupTasks.WhenAllAsync([Task.FromException(failure), Task.FromException(failure)]);
            throw new InvalidOperationException("Cleanup unexpectedly succeeded.");
        }
        catch (Exception error)
        {
            await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        }
    }

    [Test]
    public async Task NestedFailuresAppearOnceEach()
    {
        var first = new InvalidOperationException("first");
        var second = new InvalidOperationException("second");
        var error = await Assert.That(async () => await CleanupTasks.WhenAllAsync([
            Task.FromException(new AggregateException(first, second)), Task.FromException(first),
        ])).ThrowsExactly<AggregateException>();
        await Assert.That(error!.InnerExceptions.Count).IsEqualTo(2);
        await Assert.That(error.InnerExceptions.Contains(first)).IsTrue();
        await Assert.That(error.InnerExceptions.Contains(second)).IsTrue();
    }
}
