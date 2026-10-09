using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Caching.Distributed;
using Respire.Compression;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

public partial class CacheErrorMetricsTests
{
    [Test]
    [Arguments("get")]
    [Arguments("try-get")]
    [Arguments("set")]
    [Arguments("set-buffer")]
    [Arguments("refresh")]
    [Arguments("remove")]
    public async Task PublicCachePreflightHasOneFinalOwner(string route)
    {
        var previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        try
        {
            await using var server = new FakeRespServer(FakeRespServer.OkReply);
            await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
            await using var cache = new RespireDistributedCache(client);
            using var errors = new CacheErrorCapture();
            await Assert.That(async () =>
            {
                switch (route)
                {
                    case "get": await cache.GetAsync(null!); break;
                    case "try-get": await cache.TryGetAsync("key", null!); break;
                    case "set": await cache.SetAsync("key", null!, new()); break;
                    case "set-buffer": await cache.SetAsync("key", ReadOnlySequence<byte>.Empty, null!); break;
                    case "refresh": await cache.RefreshAsync(null!); break;
                    case "remove": await cache.RemoveAsync(null!); break;
                }
            }).Throws<ArgumentNullException>();
            await Assert.That(errors.Items.Count).IsEqualTo(1);
            await Assert.That((bool)errors.Items.Single()["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(server.ReceivedCommands).IsEmpty();
        }
        finally { RespireMetrics.Configure(previous); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CachePayloadConversionReportsOriginalErrorOnce(bool buffer)
    {
        var previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        try
        {
            await using var server = new FakeRespServer(FakeRespServer.OkReply)
            {
                ReplyOverride = (_, command) => command == "CLIENT ID" ? ":123\r\n"u8.ToArray()
                    : command.StartsWith("EVALSHA ", StringComparison.Ordinal) ? "*2\r\n:0\r\n$5\r\nvalue\r\n"u8.ToArray() : null,
            };
            await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
            var expected = new InvalidOperationException("codec failed");
            await using var cache = new RespireDistributedCache(client, new() { ValueCodec = new FailingCacheCodec(expected) });
            using var errors = new CacheErrorCapture();
            var failure = await Assert.That(async () =>
            {
                if (buffer) await cache.TryGetAsync("key", new ArrayBufferWriter<byte>());
                else await cache.GetAsync("key");
            }).Throws<InvalidOperationException>();
            await Assert.That(failure).IsSameReferenceAs(expected);
            await Assert.That(errors.Items.Count).IsEqualTo(1);
            await Assert.That((bool)errors.Items.Single()["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(errors.Items.Single()["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        finally { RespireMetrics.Configure(previous); }
    }

    private sealed class FailingCacheCodec(Exception error) : IRespireValueCodec
    {
        public byte[] Encode(ReadOnlySpan<byte> payload) => throw error;
        public byte[] Decode(ReadOnlySpan<byte> payload) => throw error;
    }

    private sealed class CacheErrorCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal ConcurrentQueue<Dictionary<string, object?>> Items { get; } = new();
        internal CacheErrorCapture()
        {
            _listener.InstrumentPublished = (instrument, observer) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                    observer.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                Items.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value));
                throw new InvalidOperationException("Listener must not replace the caller failure.");
            });
            _listener.Start();
        }
        public void Dispose() => _listener.Dispose();
    }
}
