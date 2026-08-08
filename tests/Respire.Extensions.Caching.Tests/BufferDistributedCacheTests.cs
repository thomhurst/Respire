using System.Buffers;
using Microsoft.Extensions.Caching.Distributed;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Caching.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
[NotInParallel("redis-integration")]
public class BufferDistributedCacheTests(RedisTestContainer fixture)
{
    private RespireClient? _client;
    private RespireDistributedCache? _cache;

    private IBufferDistributedCache Cache => _cache!;

    [Before(Test)]
    public async Task InitializeAsync()
    {
        _client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        (await _client.ExecuteAsync("FLUSHDB")).Dispose();
        _cache = new RespireDistributedCache(_client);
    }

    [After(Test)]
    public async ValueTask DisposeAsync()
    {
        if (_cache is not null)
        {
            await _cache.DisposeAsync();
        }

        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }

    [Test]
    public async Task TryGetAsync_WritesPayloadToBufferWriter()
    {
        var value = new byte[] { 1, 2, 3, 4, 5 };
        await Cache.SetAsync("buffered", new ReadOnlySequence<byte>(value), new DistributedCacheEntryOptions());

        var destination = new ArrayBufferWriter<byte>();
        var found = await Cache.TryGetAsync("buffered", destination);

        await Assert.That(found).IsTrue();
        await Assert.That(destination.WrittenSpan.SequenceEqual(value)).IsTrue();
    }

    [Test]
    public async Task TryGetAsync_MissingKey_ReturnsFalseAndWritesNothing()
    {
        var destination = new ArrayBufferWriter<byte>();

        var found = await Cache.TryGetAsync("missing", destination);

        await Assert.That(found).IsFalse();
        await Assert.That(destination.WrittenCount).IsEqualTo(0);
    }

    [Test]
    public async Task SetAsync_MultiSegmentSequence_RoundTrips()
    {
        var first = new SequenceSegment([1, 2, 3], null);
        var last = new SequenceSegment([4, 5, 6], first);
        var sequence = new ReadOnlySequence<byte>(first, 0, last, 3);
        await Assert.That(sequence.IsSingleSegment).IsFalse();

        await Cache.SetAsync("multi-segment", sequence, new DistributedCacheEntryOptions());
        var fetched = await Cache.GetAsync("multi-segment");

        var expected = new byte[] { 1, 2, 3, 4, 5, 6 };
        await Assert.That(fetched).IsNotNull();
        await Assert.That(fetched!.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task SyncBufferApis_RoundTrip()
    {
        var value = new byte[] { 9, 8, 7 };

        await Task.Run(() =>
        {
            Cache.Set("sync-buffered", new ReadOnlySequence<byte>(value), new DistributedCacheEntryOptions());
            var destination = new ArrayBufferWriter<byte>();
            if (!Cache.TryGet("sync-buffered", destination) || !destination.WrittenSpan.SequenceEqual(value))
            {
                throw new InvalidOperationException("Sync TryGet returned a different value than Set stored.");
            }
        });

        await Assert.That(await Cache.GetAsync("sync-buffered")).IsNotNull();
    }

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public SequenceSegment(byte[] payload, SequenceSegment? previous)
        {
            Memory = payload;
            if (previous is not null)
            {
                RunningIndex = previous.RunningIndex + previous.Memory.Length;
                previous.Next = this;
            }
        }
    }
}
