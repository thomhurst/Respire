using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Respire.Compression;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ValueCodecCacheTests(RedisTestContainer fixture)
{
    private static RespireValueCodec CreateCodec(bool deflate = false) => deflate
        ? new DeflateValueCodec(new() { MinimumLength = 64, MaximumDecodedLength = 8192 })
        : new BrotliValueCodec(new() { MinimumLength = 64, MaximumDecodedLength = 8192 });

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ArrayApisRoundTripMixedFramesAndPreserveMetadata(bool deflate, bool synchronous)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var codec = CreateCodec(deflate);
        await using var cache = new RespireDistributedCache((IRespireClient)client, new()
        {
            InstanceName = "codec:", ValueCodec = codec,
        });
        byte[][] values = [[], [0, 255, 128, 1], Enumerable.Repeat((byte)42, 4096).ToArray()];
        for (var index = 0; index < values.Length; index++)
        {
            var key = "value-" + index;
            if (synchronous) await Task.Run(() => cache.Set(key, values[index], new()));
            else await cache.SetAsync(key, values[index], new());
            var value = synchronous ? await Task.Run(() => cache.Get(key)) : await cache.GetAsync(key);
            await Assert.That(value!.AsSpan().SequenceEqual(values[index])).IsTrue();
            using var stored = await client.ExecuteAsync("HMGET", "codec:" + key, "absexp", "sldexp", "data");
            await Assert.That(stored[0].AsString()).IsEqualTo("-1");
            await Assert.That(stored[1].AsString()).IsEqualTo("-1");
            var frame = stored[2].AsBytes();
            await Assert.That(frame.Length).IsGreaterThanOrEqualTo(RespireValueCodec.HeaderLength);
            await Assert.That(frame.AsSpan(0, 4).SequenceEqual("RVC\0"u8)).IsTrue();
            await Assert.That(frame[5]).IsEqualTo(index == 2 ? codec.AlgorithmId : (byte)0);
            await Assert.That(codec.Decode(frame).AsSpan().SequenceEqual(values[index])).IsTrue();
        }
        await Assert.That(synchronous ? await Task.Run(() => cache.Get("missing")) : await cache.GetAsync("missing")).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BufferApisUseDestinationDecoderAndSupportMultipleSegments(bool synchronous)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var codec = new DestinationCodec();
        await using var cache = new RespireDistributedCache((IRespireClient)client, new() { ValueCodec = codec });
        var first = new Segment([0, 255, 128]);
        var last = first.Append([1, 2, 3]);
        var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        IBufferDistributedCache bufferCache = cache;
        if (synchronous) await Task.Run(() => bufferCache.Set("buffer", sequence, new()));
        else await bufferCache.SetAsync("buffer", sequence, new());
        var destination = new ArrayBufferWriter<byte>();
        destination.Write(new byte[] { 99 });
        var found = synchronous ? await Task.Run(() => bufferCache.TryGet("buffer", destination))
            : await bufferCache.TryGetAsync("buffer", destination);
        await Assert.That(found).IsTrue();
        await Assert.That(destination.WrittenSpan.SequenceEqual(new byte[] { 99, 0, 255, 128, 1, 2, 3 })).IsTrue();
        await Assert.That(codec.DestinationDecodes).IsEqualTo(1);
        await Assert.That(await bufferCache.TryGetAsync("missing", destination)).IsFalse();
        await Assert.That(destination.WrittenCount).IsEqualTo(7);
    }

    [Test]
    [Arguments("legacy")]
    [Arguments("checksum")]
    [Arguments("oversized")]
    [Arguments("algorithm")]
    public async Task InvalidFramesFailWithoutAdvancingDestination(string corruption)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var codec = CreateCodec();
        await using var raw = new RespireDistributedCache((IRespireClient)client);
        await using var cache = new RespireDistributedCache((IRespireClient)client, new() { ValueCodec = codec });
        var frame = codec.Encode([1, 2, 3]);
        switch (corruption)
        {
            case "legacy": frame = [1, 2, 3]; break;
            case "checksum": frame[^1] ^= 1; break;
            case "oversized": BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(6), 8193); break;
            case "algorithm": frame[5] = 255; break;
        }
        await raw.SetAsync("invalid", frame, new());
        await Assert.That(async () => await cache.GetAsync("invalid")).Throws<InvalidDataException>();
        var destination = new ArrayBufferWriter<byte>();
        destination.Write(new byte[] { 99 });
        await Assert.That(async () => await cache.TryGetAsync("invalid", destination)).Throws<InvalidDataException>();
        await Assert.That(destination.WrittenSpan.SequenceEqual(new byte[] { 99 })).IsTrue();
    }

    [Test]
    public async Task OversizedWritesAndPreCancellationDoNotPublishOrInvokeCodec()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        await using var cache = new RespireDistributedCache((IRespireClient)client, new() { ValueCodec = CreateCodec() });
        await Assert.That(async () => await cache.SetAsync("too-large", new byte[8193], new()))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(await client.ExistsAsync("too-large")).IsFalse();
        await using var guarded = new RespireDistributedCache((IRespireClient)client, new() { ValueCodec = new RejectCallsCodec() });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var destination = new ArrayBufferWriter<byte>();
        await Assert.That(async () => await guarded.SetAsync("cancelled", [1], new(), cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(async () => await guarded.SetAsync("cancelled", new ReadOnlySequence<byte>(new byte[] { 1 }), new(), cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(async () => await guarded.GetAsync("cancelled", cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await guarded.TryGetAsync("cancelled", destination, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(await client.ExistsAsync("cancelled")).IsFalse();
        await Assert.That(destination.WrittenCount).IsEqualTo(0);
    }

    [Test]
    public async Task CodecSelectionIsCapturedAndIndependentOfClientSerializerAndOwnership()
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        {
            Serializer = new RespireValueCodecSerializer(RespireSerializer.Default, new RejectCallsCodec()),
        });
        var options = new RespireCacheOptions { ValueCodec = CreateCodec() };
        await using (var cache = new RespireDistributedCache((IRespireClient)client, options))
        {
            options.ValueCodec = new RejectCallsCodec();
            await cache.SetAsync("captured", [0, 255], new());
            await Assert.That((await cache.GetAsync("captured"))!.AsSpan().SequenceEqual(new byte[] { 0, 255 })).IsTrue();
        }
        await client.PingAsync(); // Disposing a cache must not dispose its borrowed client or codec.
        await using var raw = new RespireDistributedCache((IRespireClient)client);
        await raw.SetAsync("raw", [0, 255], new());
        using var stored = await client.ExecuteAsync("HGET", "raw", "data");
        await Assert.That(stored.AsSpan().SequenceEqual(new byte[] { 0, 255 })).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DependencyInjectionUsesExplicitCodecForOwnedAndRegisteredClients(bool owned)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var services = new ServiceCollection();
        if (!owned) services.AddSingleton<IRespireClient>(client);
        services.AddRespireDistributedCache(options =>
        {
            if (owned) options.ClientOptions = _ => RespireOptions.Parse(fixture.ConnectionString);
            options.ValueCodec = CreateCodec();
            options.InstanceName = "di-codec:";
        });
        await using (var provider = services.BuildServiceProvider())
        {
            var cache = provider.GetRequiredService<IDistributedCache>();
            await cache.SetAsync("value", [0, 255], new());
            await Assert.That((await cache.GetAsync("value"))!.AsSpan().SequenceEqual(new byte[] { 0, 255 })).IsTrue();
        }
        await client.PingAsync();
        using var stored = await client.ExecuteAsync("HGET", "di-codec:value", "data");
        await Assert.That(CreateCodec().Decode(stored.AsSpan()).AsSpan().SequenceEqual(new byte[] { 0, 255 })).IsTrue();
    }

    [Test]
    public async Task ExpiryAndRefreshOperateOnMetadataWithoutDecodingPayload()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        await using var cache = new RespireDistributedCache((IRespireClient)client, new() { ValueCodec = CreateCodec() });
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        await cache.SetAsync("expiry", [1, 2, 3], new()
        {
            AbsoluteExpiration = deadline, SlidingExpiration = TimeSpan.FromSeconds(10),
        });
        using var metadata = await client.ExecuteAsync("HMGET", "expiry", "absexp", "sldexp");
        await Assert.That(metadata[0].AsString()).IsEqualTo(deadline.UtcTicks.ToString());
        await Assert.That(metadata[1].AsString()).IsEqualTo(TimeSpan.FromSeconds(10).Ticks.ToString());
        using (var shortened = await client.ExecuteAsync("PEXPIRE", "expiry", 2000)) { }
        await Assert.That(await cache.GetAsync("expiry")).IsNotNull();
        using (var refreshed = await client.ExecuteAsync("PTTL", "expiry"))
        {
            await Assert.That(refreshed.AsInteger()).IsGreaterThan(2000);
            await Assert.That(refreshed.AsInteger()).IsLessThanOrEqualTo(10_000);
        }
        await using var refreshOnly = new RespireDistributedCache((IRespireClient)client, new() { ValueCodec = new RejectCallsCodec() });
        await Task.Run(() => refreshOnly.Refresh("expiry"));
        await refreshOnly.RefreshAsync("expiry");
        await cache.SetAsync("absolute-cap", [1], new()
        {
            AbsoluteExpiration = deadline, SlidingExpiration = TimeSpan.FromSeconds(60),
        });
        using (var capped = await client.ExecuteAsync("PTTL", "absolute-cap"))
            await Assert.That(capped.AsInteger()).IsLessThanOrEqualTo(30_000);
        await refreshOnly.RemoveAsync("absolute-cap");
        await Assert.That(await client.ExistsAsync("absolute-cap")).IsFalse();
        using (var expireNow = await client.ExecuteAsync("PEXPIRE", "expiry", 0)) { }
        await Assert.That(await cache.GetAsync("expiry")).IsNull();
        var destination = new ArrayBufferWriter<byte>();
        await Assert.That(await cache.TryGetAsync("expiry", destination)).IsFalse();
    }

    [Test]
    public async Task CancellationDuringEncodingPreventsPublication()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        using var cancellation = new CancellationTokenSource();
        await using var cache = new RespireDistributedCache((IRespireClient)client, new()
        {
            ValueCodec = new CancellingCodec(cancellation),
        });
        var error = await Assert.That(async () => await cache.SetAsync("encoding-cancelled", [1], new(), cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(await client.ExistsAsync("encoding-cancelled")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CustomDecoderFailurePreservesCommittedPrefix(bool synchronous)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var expected = new InvalidDataException("custom decode failure");
        await using var cache = new RespireDistributedCache((IRespireClient)client, new()
        {
            ValueCodec = new PartialFailureCodec(expected),
        });
        await cache.SetAsync("partial-decode", [1, 2, 3], new());
        var destination = new ArrayBufferWriter<byte>();
        destination.Write(new byte[] { 99 });
        var error = await Assert.That(async () =>
        {
            if (synchronous) await Task.Run(() => cache.TryGet("partial-decode", destination));
            else await cache.TryGetAsync("partial-decode", destination);
        }).Throws<InvalidDataException>();
        await Assert.That(error).IsSameReferenceAs(expected);
        await Assert.That(destination.WrittenSpan.SequenceEqual(new byte[] { 99 })).IsTrue();
        await Assert.That(destination.GetSpan(1)[0]).IsEqualTo((byte)42);
        await client.PingAsync(); // The failed decoder must still release the Redis result.
    }

    private sealed class PartialFailureCodec(Exception failure) : IRespireValueCodec
    {
        public byte[] Encode(ReadOnlySpan<byte> payload) => payload.ToArray();
        public byte[] Decode(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("Buffer reads must use the destination overload.");
        public void Decode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination)
        {
            destination.GetSpan(1)[0] = 42;
            // A custom codec controls Advance; this one leaves partial output uncommitted.
            throw failure;
        }
    }
    private sealed class CancellingCodec(CancellationTokenSource cancellation) : IRespireValueCodec
    {
        public byte[] Encode(ReadOnlySpan<byte> payload)
        {
            cancellation.Cancel();
            return payload.ToArray();
        }
        public byte[] Decode(ReadOnlySpan<byte> payload) => payload.ToArray();
    }

    private sealed class DestinationCodec : IRespireValueCodec
    {
        private readonly BrotliValueCodec _inner = new();
        public int DestinationDecodes { get; private set; }
        public byte[] Encode(ReadOnlySpan<byte> payload) => _inner.Encode(payload);
        public byte[] Decode(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("Buffer reads must use the destination overload.");
        public void Decode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination)
        {
            DestinationDecodes++;
            _inner.Decode(payload, destination);
        }
    }

    private sealed class RejectCallsCodec : IRespireValueCodec
    {
        public byte[] Encode(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("Codec must not be invoked.");
        public byte[] Decode(ReadOnlySpan<byte> payload) => throw new InvalidOperationException("Codec must not be invoked.");
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment Append(byte[] value)
        {
            var next = new Segment(value) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }

        public Segment(byte[] value) => Memory = value;
    }
}