using System.Reflection;
using System.Runtime.InteropServices;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class InflightRingOrderingTests
{
#if DEBUG
    [Test]
    public async Task ConsumerAcquiresProducerTailOncePerPublishedBatch()
    {
        var ring = new InflightRing(8);
        var source = new PendingResponseSource();
        for (var i = 0; i < 8; i++)
            await Assert.That(ring.TryEnqueue(source, i + 1)).IsTrue();

        for (var i = 0; i < 8; i++)
        {
            for (var peek = 0; peek < 3; peek++)
                await Assert.That(ring.TryPeekConsumer(out _)).IsTrue();
            var dequeued = i % 2 == 0
                ? ring.TryDequeue(out _)
                : ring.TryDequeue(out _, out _, out _);
            await Assert.That(dequeued).IsTrue();
            await Assert.That(ring.ConsumerTailReadsForTests).IsEqualTo(1);
        }

        // Positive control: draining the snapshot requires another acquire, even when empty.
        await Assert.That(ring.TryPeekConsumer(out _)).IsFalse();
        await Assert.That(ring.ConsumerTailReadsForTests).IsEqualTo(2);
        await Assert.That(ring.TryEnqueue(source, 9)).IsTrue();
        // Exact inspection does not write the consumer-owned cache.
        await Assert.That(ring.TryPeek(out _)).IsTrue();
        await Assert.That(ring.ConsumerTailReadsForTests).IsEqualTo(2);
        await Assert.That(ring.TryDequeue(out _)).IsTrue();
        await Assert.That(ring.ConsumerTailReadsForTests).IsEqualTo(3);
        await Assert.That(ring.CompletedWriteEnd).IsEqualTo(9L);
    }

    [Test]
    public async Task CapacityHintAvoidsConsumerHeadWhileCachedCapacitySuffices()
    {
        var ring = new InflightRing(8);
        var source = new PendingResponseSource();
        await Assert.That(ring.TryEnqueue(source)).IsTrue();
        for (var i = 0; i < 100; i++)
            await Assert.That(ring.HasCapacitySnapshot(7)).IsTrue();
        await Assert.That(ring.CapacitySnapshotHeadReadsForTests).IsEqualTo(0);
        // Positive control: insufficient cached capacity really reads the consumer head.
        await Assert.That(ring.HasCapacitySnapshot(8)).IsFalse();
        await Assert.That(ring.CapacitySnapshotHeadReadsForTests).IsEqualTo(1);
    }
#endif

    [Test]
    public async Task CapacityHintObservesReleasedSlotsWithoutPublishingProducerCache()
    {
        var ring = new InflightRing(8);
        var source = new PendingResponseSource();
        for (var i = 0; i < 8; i++)
            await Assert.That(ring.TryEnqueue(source, i + 1)).IsTrue();
        await Assert.That(ring.HasCapacitySnapshot(1)).IsFalse();
        for (var i = 0; i < 3; i++)
            await Assert.That(ring.TryDequeue(out _)).IsTrue();

        // Many callers may inspect capacity before taking the write gate. Their hints
        // must observe released slots without becoming writers of the producer's cache.
        Parallel.For(0, 100, _ =>
        {
            if (!ring.HasCapacitySnapshot(3) || ring.HasCapacitySnapshot(4))
                throw new InvalidOperationException("The capacity hint lost a released slot or invented capacity.");
        });
        var positions = typeof(InflightRing).GetField("_positions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(ring)!;
        var cachedHead = (long)positions.GetType().GetField("CachedHead", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(positions)!;
        await Assert.That(cachedHead).IsEqualTo(0L);
        await Assert.That(ring.HasCapacity(3)).IsTrue();
        await Assert.That(ring.TryEnqueue(source, 9)).IsTrue();
        await Assert.That(ring.TryEnqueue(source, 10)).IsTrue();
        await Assert.That(ring.TryEnqueue(source, 11)).IsTrue();
        await Assert.That(ring.HasCapacitySnapshot(1)).IsFalse();
        await Assert.That(ring.Count).IsEqualTo(8);
    }

    [Test]
    [Arguments("Tail", "Head")]
    [Arguments("Tail", "CompletedWriteEnd")]
    [Arguments("CachedHead", "Head")]
    [Arguments("CachedHead", "CompletedWriteEnd")]
    [Arguments("Tail", "CachedTail")]
    [Arguments("CachedHead", "CachedTail")]
    public async Task ProducerAndConsumerCounterRangesHaveCacheLineSeparation(string producer, string consumer)
    {
        var positions = typeof(InflightRing).GetNestedType("Positions", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The ring counter layout is missing.");
        var producerOffset = Marshal.OffsetOf(positions, producer).ToInt64();
        var consumerOffset = Marshal.OffsetOf(positions, consumer).ToInt64();
        // Check entire field ranges, preserving isolation even with an unaligned base address.
        var gap = Math.Abs(consumerOffset - producerOffset) - sizeof(long);
        await Assert.That(gap).IsGreaterThanOrEqualTo(128L);
    }

    [Test]
    [Arguments(1, false)]
    [Arguments(1, true)]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(8, false)]
    [Arguments(8, true)]
    [Arguments(256, false)]
    [Arguments(256, true)]
    public async Task ConcurrentPublicationPreservesSourcesOffsetsAndDiscardMetadata(int capacity, bool peekBeforeDequeue)
    {
        const int count = 50_000;
        var ring = new InflightRing(capacity);
        var sources = Enumerable.Range(0, 64).Select(_ => new PendingResponseSource()).ToArray();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var start = new ManualResetEventSlim();
        var token = cancellation.Token;
        var producer = Task.Factory.StartNew(() =>
        {
            start.Wait(token);
            var spin = new SpinWait();
            for (var i = 0; i < count; i++)
            {
                while (!ring.HasCapacitySnapshot(1) || !(i % 3 == 0
                    ? ring.TryEnqueueDiscard(i % 2 == 0 ? "SET" : null, i + 1, i % 17)
                    : ring.TryEnqueue(sources[i % sources.Length], i + 1)))
                {
                    token.ThrowIfCancellationRequested();
                    spin.SpinOnce(sleep1Threshold: -1);
                }
                spin.Reset();
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var consumer = Task.Factory.StartNew(() =>
        {
            start.Wait(token);
            var spin = new SpinWait();
            for (var i = 0; i < count; i++)
            {
                PendingResponse source;
                string? operation;
                int retryAttempts;
                if (peekBeforeDequeue)
                {
                    PendingResponse peeked;
                    while (!ring.TryPeekConsumer(out peeked))
                    {
                        token.ThrowIfCancellationRequested();
                        spin.SpinOnce(sleep1Threshold: -1);
                    }
                    if (!ring.TryDequeue(out source, out operation, out retryAttempts)
                        || !ReferenceEquals(source, peeked))
                    {
                        cancellation.Cancel();
                        throw new InvalidOperationException($"Published reply disappeared at {i}.");
                    }
                }
                else
                {
                    while (!ring.TryDequeue(out source, out operation, out retryAttempts))
                    {
                        token.ThrowIfCancellationRequested();
                        spin.SpinOnce(sleep1Threshold: -1);
                    }
                }
                spin.Reset();
                var expected = i % 3 == 0 ? InflightRing.DiscardSentinel : sources[i % sources.Length];
                if (!ReferenceEquals(source, expected) || operation != (i % 6 == 0 ? "SET" : null)
                    || retryAttempts != (i % 3 == 0 ? i % 17 : 0)
                    || ring.CompletedWriteEnd != i + 1)
                {
                    cancellation.Cancel();
                    throw new InvalidOperationException($"FIFO publication failed at reply {i}.");
                }
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        start.Set();
        await Task.WhenAll(producer, consumer);
        await Assert.That(ring.Count).IsEqualTo(0);
        await Assert.That(ring.ConsumerPosition).IsEqualTo((long)count);
        await Assert.That(ring.CompletedWriteEnd).IsEqualTo((long)count);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(8)]
    public async Task ConsumerRefreshesDrainedSnapshotsAcrossSlotReuse(int capacity)
    {
        var ring = new InflightRing(capacity);
        var sources = Enumerable.Range(0, capacity).Select(_ => new PendingResponseSource()).ToArray();
        for (var cycle = 0; cycle < 100; cycle++)
        {
            await Assert.That(ring.TryPeekConsumer(out _)).IsFalse();
            await Assert.That(ring.TryDequeue(out _, out _, out _)).IsFalse();
            for (var i = 0; i < capacity; i++)
                await Assert.That(ring.TryEnqueue(sources[i], cycle * capacity + i + 1)).IsTrue();
            for (var i = 0; i < capacity; i++)
            {
                await Assert.That(ring.TryPeekConsumer(out var peeked)).IsTrue();
                await Assert.That(ReferenceEquals(peeked, sources[i])).IsTrue();
                var dequeued = i % 2 == 0
                    ? ring.TryDequeue(out var source)
                    : ring.TryDequeue(out source, out _, out _);
                await Assert.That(dequeued).IsTrue();
                await Assert.That(ReferenceEquals(source, peeked)).IsTrue();
                await Assert.That(ring.CompletedWriteEnd).IsEqualTo((long)(cycle * capacity + i + 1));
            }
            await Assert.That(ring.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task DiscardMetadataIsClearedByEveryDequeueOverloadBeforeSlotReuse()
    {
        var ring = new InflightRing(1);
        var normal = new PendingResponseSource();
        for (var cycle = 0; cycle < 100; cycle++)
        {
            await Assert.That(ring.TryEnqueueDiscard("SET", 1, 3)).IsTrue();
            await Assert.That(ring.TryEnqueueDiscard("FAILED", 2, 7)).IsFalse();
            if (cycle % 2 == 0)
                await Assert.That(ring.TryDequeue(out _)).IsTrue();
            else
                await Assert.That(ring.TryDequeue(out _, out _)).IsTrue();
            await Assert.That(ring.TryEnqueue(normal, 3)).IsTrue();
            await Assert.That(ring.TryDequeue(out var source, out var operation, out var attempts)).IsTrue();
            await Assert.That(ReferenceEquals(source, normal)).IsTrue();
            await Assert.That(operation).IsNull();
            await Assert.That(attempts).IsEqualTo(0);
            await Assert.That(ring.TryEnqueueDiscard(null, 4, 2)).IsTrue();
            await Assert.That(ring.TryDequeue(out source, out operation, out attempts)).IsTrue();
            await Assert.That(ReferenceEquals(source, InflightRing.DiscardSentinel)).IsTrue();
            await Assert.That(operation).IsNull();
            await Assert.That(attempts).IsEqualTo(2);
            await Assert.That(ring.TryEnqueueDiscard(null, 5)).IsTrue();
            await Assert.That(ring.TryDequeue(out _, out operation, out attempts)).IsTrue();
            await Assert.That(operation).IsNull();
            await Assert.That(attempts).IsEqualTo(0);
            await Assert.That(ring.TryDequeue(out _, out operation, out attempts)).IsFalse();
            await Assert.That(operation).IsNull();
            await Assert.That(attempts).IsEqualTo(0);
        }
    }

    [Test]
    public async Task FullRingRefreshesCapacityAfterConsumptionWithoutOverwritingReplies()
    {
        var ring = new InflightRing(2);
        var first = new PendingResponseSource();
        var second = new PendingResponseSource();
        for (var cycle = 0; cycle < 100; cycle++)
        {
            await Assert.That(ring.HasCapacity(2)).IsTrue();
            await Assert.That(ring.TryEnqueue(first, 4 * cycle + 1)).IsTrue();
            await Assert.That(ring.TryEnqueue(second, 4 * cycle + 2)).IsTrue();
            await Assert.That(ring.HasCapacity(1)).IsFalse();
            await Assert.That(ring.TryEnqueueDiscard("FAILED", 0)).IsFalse();
            await Assert.That(ring.TryDequeue(out var reply, out var operation)).IsTrue();
            await Assert.That(ReferenceEquals(reply, first)).IsTrue();
            await Assert.That(operation).IsNull();
            await Assert.That(ring.HasCapacity(2)).IsFalse();
            await Assert.That(ring.HasCapacity(1)).IsTrue();
            await Assert.That(ring.TryEnqueueDiscard("SET", 4 * cycle + 3)).IsTrue();
            await Assert.That(ring.TryDequeue(out reply, out operation)).IsTrue();
            await Assert.That(ReferenceEquals(reply, second)).IsTrue();
            await Assert.That(operation).IsNull();
            await Assert.That(ring.TryDequeue(out reply, out operation)).IsTrue();
            await Assert.That(ReferenceEquals(reply, InflightRing.DiscardSentinel)).IsTrue();
            await Assert.That(operation).IsEqualTo("SET");
            await Assert.That(ring.CompletedWriteEnd).IsEqualTo((long)(4 * cycle + 3));
        }
    }
}
