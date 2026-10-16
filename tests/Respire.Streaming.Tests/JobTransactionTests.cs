using System.Text;
using System.Reflection;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public class JobTransactionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DecoratorAcceptsItsTransactionsAndRejectsOtherClientViews(bool scheduled)
    {
        await using var server = new RespireFakeServer();
        await using var root = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var other = await RespireClient.ConnectAsync(server.CreateOptions());
        var client = Wrap(root.WithKeyPrefix("tenant:"));
        var queue = new RespireJobQueue<string>(client, "decorated", Encoding.UTF8.GetBytes);
        await using var transaction = client.CreateTransaction();
        await using var wrongPrefix = root.CreateTransaction();
        await using var wrongClient = other.WithKeyPrefix("tenant:").CreateTransaction();

        RespirePending<RespireJobEnqueueResult> Enqueue(RespireTransactionBase target)
            => scheduled ? queue.EnqueueScheduled(target, "job", "payload", new())
                : queue.Enqueue(target, "job", "payload");

        await Assert.That(() => Enqueue(wrongPrefix)).Throws<ArgumentException>();
        await Assert.That(() => Enqueue(wrongClient)).Throws<ArgumentException>();
        var pending = Enqueue(transaction);
        await Assert.That(transaction.Count).IsEqualTo(1);
        await Assert.That(pending.Status).IsEqualTo(RespirePendingStatus.Pending);
        await Assert.That(wrongPrefix.Count).IsEqualTo(0);
        await Assert.That(wrongClient.Count).IsEqualTo(0);
    }

    internal static IRespireClient Wrap(IRespireClient client)
    {
        var wrapped = DispatchProxy.Create<IForwardingClient, ForwardingClient>();
        ((ForwardingClient)wrapped).Inner = client;
        return wrapped;
    }

    public interface IForwardingClient : IRespireClient, IRespireNameResolver;

    public class ForwardingClient : DispatchProxy
    {
        public IRespireClient Inner { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
            => method!.Invoke(Inner, arguments);
    }

    [Test]
    public async Task RejectsInvalidInputsAndDifferentClientViewsWithoutQueueing()
    {
        await using var server = new RespireFakeServer();
        await using var root = await RespireClient.ConnectAsync(server.CreateOptions());
        var client = root.WithKeyPrefix("tenant:");
        var queue = new RespireJobQueue<string>(client, "validation", _ => throw new InvalidOperationException("serialized"));
        await using var wrong = root.CreateTransaction();
        await using var transaction = client.CreateTransaction();
        await Assert.That(() => queue.Enqueue(null!, "id", "payload")).Throws<ArgumentNullException>();
        await Assert.That(() => queue.Enqueue(wrong, "id", "payload")).Throws<ArgumentException>();
        await Assert.That(() => queue.EnqueueScheduled(wrong, "id", "payload", new())).Throws<ArgumentException>();
        await Assert.That(() => queue.Enqueue(transaction, " ", "payload")).Throws<ArgumentException>();
        await Assert.That(() => queue.Enqueue(transaction, "id", "payload", TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => queue.Enqueue(transaction, "\ud800", "payload")).Throws<EncoderFallbackException>();
        await Assert.That(() => queue.EnqueueScheduled(transaction, "id", "payload", null!)).Throws<ArgumentNullException>();
        await Assert.That(() => queue.EnqueueScheduled(transaction, "id", "payload", new() { Delay = TimeSpan.FromSeconds(-1) }))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => queue.EnqueueScheduled(transaction, "id", "payload", new() { Group = " " })).Throws<ArgumentException>();
        await Assert.That(() => queue.Enqueue(transaction, "id", "payload")).Throws<InvalidOperationException>();
        await Assert.That(transaction.Count).IsEqualTo(0);
        await Assert.That(wrong.Count).IsEqualTo(0);
    }
}
