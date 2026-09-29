using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class CommandTimeoutCancellationTests
{
    [Test]
    public async Task TimeoutCancelsToken()
    {
        using var cancellation = CommandTimeoutCancellation.Create(
            default,
            TimeSpan.FromMilliseconds(10));

        await Assert.That(async () => await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task CallerCancellationCancelsToken()
    {
        using var caller = new CancellationTokenSource();
        using var cancellation = CommandTimeoutCancellation.Create(
            caller.Token,
            Timeout.InfiniteTimeSpan);

        caller.Cancel();

        await Assert.That(cancellation.Token.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task ReturningLinkedSourceRemovesCallerRegistrationBeforeReuse()
    {
        // Other tests rent command timeout sources concurrently. A dedicated pool makes
        // reuse deterministic while checking the same linked-rental disposal contract.
        using var pool = new Reservoir.CancellationTokenSourcePool(1);
        using var caller = new CancellationTokenSource();
        CancellationToken firstToken;
        using (var first = pool.RentLinked(caller.Token))
        {
            firstToken = first.Token;
        }

        using var second = pool.RentLinked(default);
        await Assert.That(second.Token == firstToken).IsTrue();

        caller.Cancel();

        await Assert.That(second.Token.IsCancellationRequested).IsFalse();
    }
}
