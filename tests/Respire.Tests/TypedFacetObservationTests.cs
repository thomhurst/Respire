using System.Collections.Concurrent;
using System.Buffers;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text;
using Respire.Internal;
using Respire.Serialization;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class TypedFacetObservationTests
{
    [Test]
    [Arguments("string-value")]
    [Arguments("string-expiry")]
    [Arguments("string-offset")]
    [Arguments("string-stream")]
    [Arguments("hash-condition")]
    [Arguments("hash-fields")]
    [Arguments("hash-precision")]
    [Arguments("key-condition")]
    [Arguments("key-database")]
    [Arguments("key-precision")]
    [Arguments("list-rank")]
    [Arguments("list-count")]
    [Arguments("list-values")]
    [Arguments("sorted-count")]
    public async Task SingleCommandFacetPreflightReportsExactlyOneFinalError(string route)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        Exception? failure = null;
        try
        {
            switch (route)
            {
                case "string-value": await client.Strings.SetAsync("key", default(RespireValue)); break;
                case "string-expiry": await client.Strings.SetAsync("key", "value", when: (SetWhen)999); break;
                case "string-offset": await client.Strings.SetRangeAsync("key", -1, "value"); break;
                case "string-stream": await client.Strings.SetAsync("key", Stream.Null, -1); break;
                case "hash-condition": await client.Hashes.SetAsync("key", "field", "value", (SetWhen)999); break;
                case "hash-fields": await client.Hashes.ExpiryTimeAsync("key", Array.Empty<string>()); break;
                case "hash-precision": await client.Hashes.ExpiryTimeAsync("key", (ExpiryTimePrecision)999, "field"); break;
                case "key-condition": await client.Keys.ExpireAsync("key", RespireExpiry.Persist, ExpireWhen.NotExists); break;
                case "key-database": await client.Keys.CopyAsync("key", "other", -1); break;
                case "key-precision": await client.Keys.ExpiryTimeAsync("key", (ExpiryTimePrecision)999); break;
                case "list-rank": await client.Lists.PositionAsync("key", "value", rank: 0); break;
                case "list-count": await client.Lists.LeftPopManyAsync("key", -1); break;
                case "list-values": await client.Lists.LeftPushIfExistsAsync("key", Array.Empty<RespireValue>()); break;
                default: await client.SortedSets.RandomMembersAsync("key", long.MinValue); break;
            }
        }
        catch (Exception error) { failure = error; }
        await Assert.That(failure is ArgumentException).IsTrue();
        var item = capture.Items.Single();
        await Assert.That(item.Tags["error.type"]).IsEqualTo(failure!.GetType().FullName);
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Any(command => command.Contains("key", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("string")]
    [Arguments("hash")]
    [Arguments("key")]
    [Arguments("list")]
    [Arguments("sorted")]
    public async Task SingleCommandFacetConversionReportsExactlyOneFinalError(string facet)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => !command.Contains("key", StringComparison.Ordinal) ? null
                : facet == "key" ? ":9223372036854775807\r\n"u8.ToArray()
                : facet == "list" ? "*1\r\n$3\r\nbad\r\n"u8.ToArray()
                : "$3\r\nbad\r\n"u8.ToArray(),
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        Exception? failure = null;
        try
        {
            switch (facet)
            {
                case "string": await client.Strings.GetAsync<int>("key"); break;
                case "hash": await client.Hashes.GetAsync<int>("key", "field"); break;
                case "key": await client.Keys.IdleTimeAsync("key"); break;
                case "list": await client.Lists.RangeAsync<int>("key"); break;
                default: await client.SortedSets.RandomMemberAsync<int>("key"); break;
            }
        }
        catch (Exception error) { failure = error; }
        await Assert.That(failure).IsNotNull();
        var item = capture.Items.Single();
        await Assert.That(item.Tags["error.type"]).IsEqualTo(failure!.GetType().FullName);
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CachedStringFacetSuccessAddsNoAllocation(bool enabled)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray()
                : command.StartsWith("GET ", StringComparison.Ordinal) ? "$5\r\nvalue\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp3, Connections = 1, ClientSideCache = new(),
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        await Assert.That(await client.Strings.GetStringAsync("key")).IsEqualTo("value");
        using var capture = new Capture();
        for (var index = 0; index < 10; index++) _ = MeasureCachedStrings(client, false);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (MeasureCachedStrings(client, false), MeasureCachedStrings(client, true)));
        await Assert.That(measured.Item1).IsEqualTo(0L);
        await Assert.That(measured.Item2).IsGreaterThanOrEqualTo(37000L);
        await Assert.That(capture.Items).IsEmpty();
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureCachedStrings(RespireClient client, bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            var pending = client.Strings.GetStringAsync("key");
            if (!pending.IsCompletedSuccessfully || pending.GetAwaiter().GetResult() != "value")
                throw new InvalidOperationException("Expected a synchronous cache hit.");
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    [Arguments("MOVED", "success")]
    [Arguments("MOVED", "server")]
    [Arguments("MOVED", "conversion")]
    [Arguments("ASK", "success")]
    [Arguments("ASK", "server")]
    [Arguments("ASK", "conversion")]
    public async Task TypedStringFacetKeepsRetryCountThroughFinalConversion(string redirect, string outcome)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var source = new FakeRespServer(4, FakeRespServer.OkReply);
        await using var target = new FakeRespServer(4, FakeRespServer.OkReply);
        var slot = ClusterHash.GetSlot("key");
        source.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{source.Port}\r\n")
            : command == "GET key" ? Encoding.ASCII.GetBytes($"-{redirect} {slot} 127.0.0.1:{target.Port}\r\n") : null;
        target.ReplyOverride = (_, command) => command == "GET key" ? outcome switch
        {
            "server" => "-NOPERM denied\r\n"u8.ToArray(),
            "conversion" => "$3\r\nbad\r\n"u8.ToArray(),
            _ => "$2\r\n42\r\n"u8.ToArray(),
        } : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", source.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        Exception? failure = null;
        try { await Assert.That(await client.Strings.GetAsync<int>("key")).IsEqualTo(42); }
        catch (Exception error) { failure = error; }
        var final = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(outcome == "success" ? 0 : 1);
        await Assert.That(capture.Items.Count(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(1);
        if (outcome != "success")
        {
            await Assert.That(failure).IsNotNull();
            await Assert.That(final[0].Tags["error.type"]).IsEqualTo(failure!.GetType().FullName);
            await Assert.That(final[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments("string")]
    [Arguments("hash")]
    [Arguments("key")]
    [Arguments("list")]
    [Arguments("sorted")]
    public async Task TypedFacetPreCancellationPreservesTokenAndOneFinalError(string facet)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var capture = new Capture(throwOnMeasurement: true);
        Exception? failure = null;
        try
        {
            switch (facet)
            {
                case "string": await client.Strings.LengthAsync("key", cancellation.Token); break;
                case "hash": await client.Hashes.CountAsync("key", cancellation.Token); break;
                case "key": await client.Keys.ExpiryAsync("key", cancellation.Token); break;
                case "list": await client.Lists.CountAsync("key", cancellation.Token); break;
                default: await client.SortedSets.RandomMemberAsync("key", cancellation.Token); break;
            }
        }
        catch (Exception error) { failure = error; }
        await Assert.That(failure is OperationCanceledException).IsTrue();
        await Assert.That(((OperationCanceledException)failure!).CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
    }

    [Test]
    [Arguments("string-set")]
    [Arguments("hash-set")]
    [Arguments("string-get")]
    [Arguments("hash-get")]
    public async Task TypedFacetPreservesOriginalSerializerFailure(string route)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var expected = new InvalidOperationException("serializer failure");
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
                || command.StartsWith("HGET ", StringComparison.Ordinal) ? "$2\r\n{}\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, Serializer = new ThrowingSerializer(expected),
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        Exception? actual = null;
        try
        {
            switch (route)
            {
                case "string-set": await client.Strings.SetAsync("key", new Payload()); break;
                case "hash-set": await client.Hashes.SetAsync("key", "field", new Payload()); break;
                case "string-get": await client.Strings.GetAsync<Payload>("key"); break;
                default: await client.Hashes.GetAsync<Payload>("key", "field"); break;
            }
        }
        catch (Exception error) { actual = error; }
        await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
    }

    private sealed class Payload;
    private sealed class ThrowingSerializer(Exception error) : IRespireSerializer
    {
        public void Serialize<T>(IBufferWriter<byte> destination, T value) => throw error;
        public T? Deserialize<T>(ReadOnlySpan<byte> payload) => throw error;
        public void Serialize(IBufferWriter<byte> destination, Type type, object? value) => throw error;
        public object? Deserialize(Type type, ReadOnlySpan<byte> payload) => throw error;
    }

    private sealed record Item(Dictionary<string, object?> Tags);
    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal ConcurrentQueue<Item> Items { get; } = new();
        internal Capture(bool throwOnMeasurement = false)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                Items.Enqueue(new(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
                if (throwOnMeasurement) throw new InvalidOperationException("Listener failure must remain isolated.");
            });
            _listener.Start();
        }
        public void Dispose() => _listener.Dispose();
    }
}
