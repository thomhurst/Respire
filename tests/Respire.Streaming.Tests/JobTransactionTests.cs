using System.Text;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public class JobTransactionTests
{
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
