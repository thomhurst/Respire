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

#if NET10_0_OR_GREATER
    [Test]
    public async Task ReturningLeaseRemovesCallerRegistrationBeforeReuse()
    {
        using var caller = new CancellationTokenSource();
        CancellationToken firstToken;
        using (var first = CommandTimeoutCancellation.Create(
                   caller.Token,
                   Timeout.InfiniteTimeSpan))
        {
            firstToken = first.Token;
        }

        using var second = CommandTimeoutCancellation.Create(
            default,
            Timeout.InfiniteTimeSpan);
        await Assert.That(second.Token).IsEqualTo(firstToken);

        caller.Cancel();

        await Assert.That(second.Token.IsCancellationRequested).IsFalse();
    }
#endif
}
