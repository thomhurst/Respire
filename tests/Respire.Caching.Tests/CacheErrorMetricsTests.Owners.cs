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
    [Arguments("get", false)]
    [Arguments("get", true)]
    [Arguments("try-get", false)]
    [Arguments("try-get", true)]
    [Arguments("set", false)]
    [Arguments("set", true)]
    [Arguments("set-buffer", false)]
    [Arguments("set-buffer", true)]
    [Arguments("refresh", false)]
    [Arguments("refresh", true)]
    [Arguments("remove", false)]
    [Arguments("remove", true)]
    [Arguments("remove-placement", false)]
    [Arguments("remove-placement", true)]
    public async Task ForwardingDecoratorHasOneFinalCacheOwner(string route, bool asynchronous)
    {
        var previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        try
        {
            await using var server = new FakeRespServer(FakeRespServer.OkReply)
            {
                ReplyOverride = (_, command) => command.StartsWith("EVALSHA ", StringComparison.Ordinal)
                    ? "-NOSCRIPT not loaded\r\n"u8.ToArray()
                    : command.StartsWith("EVAL ", StringComparison.Ordinal) ||
                        route == "remove-placement" && command.StartsWith("SET ", StringComparison.Ordinal)
                        ? "-WRONGTYPE script rejected\r\n"u8.ToArray() : null,
            };
            await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
            var decorator = new RespireDistributedCacheTests.ScriptInterceptingClient(client, async (_, send) =>
            {
                if (asynchronous) await Task.Yield();
                return await send().ConfigureAwait(false);
            });
            await using var cache = new RespireDistributedCache(decorator);
            using var errors = new CacheErrorCapture();
            var failure = await Assert.That(async () =>
            {
                switch (route)
                {
                    case "get": await cache.GetAsync("key"); break;
                    case "try-get": await cache.TryGetAsync("key", new ArrayBufferWriter<byte>()); break;
                    case "set": await cache.SetAsync("key", new byte[] { 1 }, new()); break;
                    case "set-buffer": await cache.SetAsync("key", new ReadOnlySequence<byte>(new byte[] { 1 }), new()); break;
                    case "refresh": await cache.RefreshAsync("key"); break;
                    default: await cache.RemoveAsync("key"); break;
                }
            }).Throws<RespireServerException>();
            await Assert.That(failure!.Code).IsEqualTo("WRONGTYPE");
            var items = errors.Items.ToArray();
            var finals = items.Where(item => !(bool)item["redis.client.errors.internal"]!).ToArray();
            await Assert.That(finals.Length).IsEqualTo(1);
            await Assert.That(finals[0]["redis.client.operation.retry_attempts"]).IsEqualTo(route == "remove-placement" ? 0 : 1);
            await Assert.That(items.Length).IsEqualTo(route == "remove-placement" ? 1 : 2);

            // The scope must not escape back into the caller after asynchronous forwarding.
            await Assert.That(async () =>
            {
                using var result = await client.Scripts.ExecuteAsync(RespireScript.Create("return 1"));
            }).Throws<RespireServerException>();
            await Assert.That(errors.Items.Count(item => !(bool)item["redis.client.errors.internal"]!)).IsEqualTo(2);
        }
        finally { RespireMetrics.Configure(previous); }
    }

    [Test]
    public async Task ForwardingRemovalCleanupFailureIsInternal()
    {
        var previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        try
        {
            await using var server = new FakeRespServer(FakeRespServer.OkReply)
            {
                ReplyOverride = (_, command) => command.StartsWith("UNLINK ", StringComparison.Ordinal)
                    ? "-NOPERM revoke denied\r\n"u8.ToArray() : null,
            };
            await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
            var expected = new RespireConnectionException("Decorator failed before the removal reply.");
            var decorator = new RespireDistributedCacheTests.ScriptInterceptingClient(client, async (_, _) =>
            {
                await Task.Yield();
                throw expected;
            });
            await using var cache = new RespireDistributedCache(decorator);
            using var errors = new CacheErrorCapture();
            var failure = await Assert.That(async () => await cache.RemoveAsync("key"))
                .Throws<RespireConnectionException>();
            await Assert.That(failure).IsSameReferenceAs(expected);
            var items = errors.Items.ToArray();
            await Assert.That(items.Length).IsEqualTo(2);
            await Assert.That((bool)items[0]["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(items[0]["db.response.status_code"]).IsEqualTo("NOPERM");
            await Assert.That((bool)items[1]["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[1]["error.type"]).IsEqualTo(typeof(RespireConnectionException).FullName);
            await Assert.That(items[1]["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
        finally { RespireMetrics.Configure(previous); }
    }

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
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task CachePayloadConversionReportsOriginalErrorOnce(bool buffer, bool decorated)
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
            IRespireClient cacheClient = decorated
                ? new RespireDistributedCacheTests.ScriptInterceptingClient(client, async (_, send) =>
                {
                    await Task.Yield();
                    return await send().ConfigureAwait(false);
                }) : client;
            await using var cache = new RespireDistributedCache(cacheClient, new() { ValueCodec = new FailingCacheCodec(expected) });
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
