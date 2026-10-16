using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class BinarySerializerIntegrationTests(RedisTestContainer redis)
{
    [Test]
    [Arguments("MessagePack")]
    [Arguments("MemoryPack")]
    [Arguments("Protobuf")]
    public async Task ValuesCacheJobsAndNearCacheMapPreserveBinaryPayloads(string format)
    {
        var serializer = BinarySerializerTests.Create(format);
        var options = RespireOptions.Parse(redis.ConnectionString) with
        {
            Serializer = serializer,
            ClientSideCache = new() { TrackingMode = RespireClientTrackingMode.OptIn },
        };
        await using var client = await RespireClient.ConnectAsync(options);
        var key = "binary:" + Guid.NewGuid().ToString("N");
        var value = new SerializerPayload { Name = "binary\0λ🌍", Age = 42 };
        await client.SetAsync(key, value);
        await BinarySerializerTests.Verify(await client.GetAsync<SerializerPayload>(key), value);
        await BinarySerializerTests.Verify(await client.GetAsync<SerializerPayload>(key), value);

        await BinarySerializerTests.Verify(await client.GetOrSetAsync<SerializerPayload>(key + ":cache",
            _ => ValueTask.FromResult<SerializerPayload?>(value), TimeSpan.FromMinutes(1)), value);
        await BinarySerializerTests.Verify(await client.GetOrSetAsync<SerializerPayload>(key + ":cache",
            _ => throw new InvalidOperationException("Cache miss"), TimeSpan.FromMinutes(1)), value);

        byte[] Encode(SerializerPayload payload) => BinarySerializerTests.Encode(serializer, payload);
        var queue = new RespireJobQueue<SerializerPayload>(client, key, Encode);
        await queue.EnqueueAsync("job-1", value);
        var entries = await client.Streams.ReadAsync(queue.StreamKey);
        await Assert.That(entries.Length).IsEqualTo(1);
        await BinarySerializerTests.Verify(serializer.Deserialize<SerializerPayload>(entries[0]["_respire.job.payload"]), value);

        var received = new TaskCompletionSource<SerializerPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection().AddSingleton<IRespireClient>(client).AddSingleton(received);
        services.AddRespireJobQueue<Handler, SerializerPayload>(key, Encode,
            bytes => serializer.Deserialize<SerializerPayload>(bytes)!,
            new() { ConsumerName = "binary-test", ReadWait = TimeSpan.FromMilliseconds(20) });
        await using (var provider = services.BuildServiceProvider())
        {
            var worker = provider.GetServices<IHostedService>().Single();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await worker.StartAsync(deadline.Token);
            try { await BinarySerializerTests.Verify(await received.Task.WaitAsync(deadline.Token), value); }
            finally { await worker.StopAsync(CancellationToken.None); }
        }

        await using var map = new RespireMap<string, SerializerPayload>(client, key + ":map", x => x,
            Encode, bytes => serializer.Deserialize<SerializerPayload>(bytes.Span)!);
        await map.SetAsync("field", value);
        await BinarySerializerTests.Verify((await map.GetAsync("field")).Value, value);
        var hits = client.ClientSideCache!.GetStatistics().Hits;
        var cached = (await map.GetAsync("field")).Value!;
        await BinarySerializerTests.Verify(cached, value);
        await Assert.That(client.ClientSideCache.GetStatistics().Hits).IsGreaterThan(hits);
        cached.Name = "mutated by caller";
        await BinarySerializerTests.Verify((await map.GetAsync("field")).Value, value);
    }

    public sealed class Handler(TaskCompletionSource<SerializerPayload> received) : IRespireStreamHandler<RespireJob<SerializerPayload>>
    {
        public ValueTask<RespireStreamWorkerResult> HandleAsync(RespireJob<SerializerPayload> job, CancellationToken cancellationToken)
        {
            received.TrySetResult(job.Payload);
            return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
        }
    }
}
