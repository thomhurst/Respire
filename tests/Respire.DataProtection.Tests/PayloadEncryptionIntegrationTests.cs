using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Caching;
using Respire.Compression;
using Respire.Streaming;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.DataProtection.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class PayloadEncryptionIntegrationTests(RedisTestContainer redis)
{
    private static RespireDataProtectionValueCodec Codec() => new(
        new EphemeralDataProtectionProvider().CreateProtector("integration"), new RespireBrotliValueCodec());

    [Test]
    public async Task CacheEncryptsOnlyOptedInValuesAndRejectsTampering()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var codec = Codec();
        await using var encrypted = new RespireDistributedCache((IRespireClient)client, new() { ValueCodec = codec });
        await using var plain = new RespireDistributedCache((IRespireClient)client);
        var payload = "cache secret"u8.ToArray();
        await encrypted.SetAsync("encrypted", payload, new());
        await plain.SetAsync("plain", payload, new());
        var stored = await client.Hashes.GetBytesAsync("encrypted", "data") ?? throw new InvalidOperationException("Missing encrypted value.");
        await Assert.That(stored.AsSpan().SequenceEqual(payload)).IsFalse();
        await Assert.That(await client.Hashes.GetBytesAsync("plain", "data")).IsEquivalentTo(payload);
        await Assert.That(await encrypted.GetAsync("encrypted")).IsEquivalentTo(payload);
        var destination = new ArrayBufferWriter<byte>();
        await Assert.That(await encrypted.TryGetAsync("encrypted", destination)).IsTrue();
        await Assert.That(destination.WrittenSpan.SequenceEqual(payload)).IsTrue();
        stored[^1] ^= 1;
        await client.Hashes.SetAsync("encrypted", "data", stored);
        await Assert.That(async () => await encrypted.GetAsync("encrypted")).Throws<CryptographicException>();
        var before = destination.WrittenCount;
        await Assert.That(async () => await encrypted.TryGetAsync("encrypted", destination)).Throws<CryptographicException>();
        await Assert.That(destination.WrittenCount).IsEqualTo(before);
        await Assert.That(async () => await encrypted.GetAsync("plain")).Throws<CryptographicException>();
    }

    [Test]
    public async Task MapDecodesEncryptedNearCacheHits()
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(redis.ConnectionString) with
        {
            Protocol = RespireProtocol.Resp3, ClientSideCache = new(),
        });
        var codec = Codec();
        await using var map = new RespireMap<string, string>(client, "map", key => key,
            value => codec.Encode(Encoding.UTF8.GetBytes(value)),
            bytes => Encoding.UTF8.GetString(codec.Decode(bytes.Span)));
        await map.SetAsync("field", "map secret");
        var raw = await client.Hashes.GetBytesAsync("map", "field");
        await Assert.That(raw!.AsSpan().SequenceEqual("map secret"u8)).IsFalse();
        await Assert.That((await map.GetAsync("field")).Value).IsEqualTo("map secret");
        var hits = client.ClientSideCache!.GetStatistics().Hits;
        await Assert.That((await map.GetAsync("field")).Value).IsEqualTo("map secret");
        await Assert.That(client.ClientSideCache.GetStatistics().Hits).IsGreaterThan(hits);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task JobAndStreamWorkersReceiveDecryptedPayloads(bool jobs)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var codec = Codec();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection().AddSingleton<IRespireClient>(client).AddSingleton(received);
        var options = new RespireStreamWorkerOptions { ReadWait = TimeSpan.FromMilliseconds(20) };
        string stream;
        string field;
        if (jobs)
        {
            services.AddRespireJobQueue<JobHandler, string>("encrypted",
                value => codec.Encode(Encoding.UTF8.GetBytes(value)),
                bytes => Encoding.UTF8.GetString(codec.Decode(bytes)), options);
            var queue = new RespireJobQueue<string>(client, "encrypted", value => codec.Encode(Encoding.UTF8.GetBytes(value)));
            await queue.EnqueueAsync("id", "worker secret");
            stream = queue.StreamKey;
            field = "_respire.job.payload";
        }
        else
        {
            stream = "encrypted-stream";
            field = "payload";
            services.AddRespireStreamWorker<StreamHandler, string>(stream, "readers",
                entry => Encoding.UTF8.GetString(codec.Decode(entry["payload"])), options);
            await client.Streams.AddAsync(stream, ("payload", codec.Encode("worker secret"u8)));
        }
        var entry = (await client.Streams.ReadAsync(stream)).Single();
        await Assert.That(entry[field].AsSpan().SequenceEqual("worker secret"u8)).IsFalse();
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().Single();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await worker.StartAsync(deadline.Token);
        try
        {
            await Assert.That(await received.Task.WaitAsync(deadline.Token)).IsEqualTo("worker secret");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    public sealed class JobHandler(TaskCompletionSource<string> received) : IRespireStreamHandler<RespireJob<string>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<string> job, CancellationToken cancellationToken)
        {
            received.TrySetResult(job.Payload);
            return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
        }
    }

    public sealed class StreamHandler(TaskCompletionSource<string> received) : IRespireStreamHandler<string>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(string message, CancellationToken cancellationToken)
        {
            received.TrySetResult(message);
            return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
        }
    }
}
