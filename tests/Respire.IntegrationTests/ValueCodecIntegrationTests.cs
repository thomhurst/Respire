using System.Buffers;
using FluentAssertions;
using Respire.Compression;
using Respire.Compression.Lz4;
using Respire.Compression.Zstd;
using Respire.Serialization;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ValueCodecIntegrationTests(RedisTestContainer fixture)
{
    [ClassDataSource<RedisTestContainer>(Shared = SharedType.Keyed, Key = TestConstraints.ClientCacheServer)]
    public required RedisTestContainer CacheServer { get; init; }

    [Test]
    [Arguments("brotli", 2)]
    [Arguments("brotli", 3)]
    [Arguments("deflate", 2)]
    [Arguments("deflate", 3)]
    [Arguments("lz4", 2)]
    [Arguments("lz4", 3)]
    [Arguments("zstd", 2)]
    [Arguments("zstd", 3)]
    public async Task MixedCodecValuesRoundTripAcrossImmediateAndDeferredApis(string algorithm, int protocol)
    {
        var codec = CreateCodec(algorithm);
        var serializer = new RespireValueCodecSerializer(RespireSerializer.Default, codec);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(fixture.Host, fixture.Port)], Database = fixture.Database,
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3, Serializer = serializer,
        });
        var view = client.WithKeyPrefix($"codec:{Guid.NewGuid():N}:");
        var small = new Payload("small");
        var large = new Payload(new string('x', 8192));
        await view.SetAsync("small", small);
        await view.SetAsync("large", large);
        (await view.GetAsync<Payload>("small")).Should().Be(small);
        (await view.GetAsync<Payload>("large")).Should().Be(large);
        (await view.TryGetAsync<Payload>("missing")).Found.Should().BeFalse();
        (await view.Strings.GetBytesAsync("small"))![5].Should().Be(0);
        var encoded = (await view.Strings.GetBytesAsync("large"))!;
        encoded[5].Should().Be(codec.AlgorithmId);
        encoded.Length.Should().BeLessThan(large.Text.Length);
        await view.Hashes.SetAsync("hash", "field", large);
        (await view.Hashes.GetAsync<Payload>("hash", "field")).Should().Be(large);
        await view.SortedSets.AddAsync("sorted", large, 1);
        (await view.SortedSets.RangeAsync<Payload>("sorted", 0, -1)).Should().Equal(large);

        var serialized = new ArrayBufferWriter<byte>();
        serializer.Serialize(serialized, small);
        await view.Lists.RightPushAsync("list", serialized.WrittenMemory);
        (await view.Lists.LeftPopAsync<Payload>("list")).Should().Be(small);
        using (var batch = view.CreateBatch())
        {
            _ = batch.Strings.Set("batch", large);
            var pending = batch.Strings.Get<Payload>("batch");
            (await batch.ExecuteAsync()).ThrowIfAnyFailed();
            pending.Result.Should().Be(large);
        }
        await using (var transaction = view.CreateTransaction())
        {
            _ = transaction.Strings.Set("transaction", small);
            var pending = transaction.Strings.Get<Payload>("transaction");
            await transaction.CommitAsync();
            pending.Result.Should().Be(small);
        }
        await view.SetAsync("number", 42);
        (await view.GetAsync<int>("number")).Should().Be(42);
        await view.SetAsync("text", "plain");
        (await view.GetStringAsync("text")).Should().Be("plain");
        byte[] raw = [0, 255, 128];
        await view.SetAsync<byte[]>("raw", raw);
        (await view.Strings.GetBytesAsync("raw")).Should().Equal(raw);
        await view.SetAsync<byte[]>("explicit-codec", codec.Encode(raw));
        codec.Decode((await view.Strings.GetBytesAsync("explicit-codec"))!).Should().Equal(raw);

        // Stored corruption is a conversion failure, not a reason to replay or lose socket FIFO.
        encoded[^1] ^= 1;
        await view.SetAsync<byte[]>("corrupt", encoded);
        Func<Task> readCorrupt = async () => await view.GetAsync<Payload>("corrupt");
        await readCorrupt.Should().ThrowAsync<InvalidDataException>();
        await client.PingAsync();
        (await view.GetAsync<Payload>("large")).Should().Be(large);
    }

    [Test]
    [NotInParallel(TestConstraints.ClientCacheHits)] // Exact hit assertions require stable tracking connections.
    [Arguments("brotli")]
    [Arguments("deflate")]
    [Arguments("lz4")]
    [Arguments("zstd")]
    public async Task CachedFramesDecodeIntoIndependentTypedValues(string algorithm)
    {
        var codec = CreateCodec(algorithm);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(CacheServer.Host, CacheServer.Port)], Database = CacheServer.Database,
            Serializer = new RespireValueCodecSerializer(RespireSerializer.Default, codec), ClientSideCache = new(),
        });
        var key = $"codec-cache:{Guid.NewGuid():N}";
        await client.SetAsync(key, new MutablePayload { Text = new string('x', 8192) });
        // Cache population must follow the write's native retirement, not only its caller.
        using var retirement = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.Core.ClientCache!.InspectForTests().ActiveMutationCount != 0)
            await Task.Delay(1, retirement.Token);
        var first = (await client.GetAsync<MutablePayload>(key))!;
        var before = client.ClientSideCache!.GetStatistics().Hits;
        first.Text = "caller mutation";
        var second = (await client.GetAsync<MutablePayload>(key))!;
        second.Text.Should().Be(new string('x', 8192));
        client.ClientSideCache.GetStatistics().Hits.Should().BeGreaterThan(before);
    }

    private static RespireValueCodec CreateCodec(string algorithm) => algorithm switch
    {
        "brotli" => new BrotliValueCodec(),
        "deflate" => new DeflateValueCodec(),
        "lz4" => new Lz4ValueCodec(),
        "zstd" => new ZstdValueCodec(),
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    public sealed record Payload(string Text);
    public sealed class MutablePayload { public string Text { get; set; } = ""; }
}
