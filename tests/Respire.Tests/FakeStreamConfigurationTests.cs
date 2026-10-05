using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeStreamConfigurationTests
{
    [Test]
    public async Task IdentityRetentionUsesGeneratedIdAfterFutureExplicitId()
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await client.Streams.AddAsync("s", new StreamAddOptions { Id = "10000-0" }, ("f", "seed"));
        await client.Streams.ConfigureAsync("s", new() { IdempotencyDurationSeconds = 1 });
        var options = new StreamAddOptions { Idempotency = StreamIdempotency.Manual("p", "message") };
        var first = await client.Streams.AddAsync("s", options, ("f", "first"));
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await client.Streams.AddAsync("s", options, ("f", "duplicate"))).IsEqualTo(first);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.That(await client.Streams.AddAsync("s", options, ("f", "expired"))).IsNotEqualTo(first);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConfigurationAndDeduplicationPreserveBoundedReadsAndGroups(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await client.Streams.AddAsync("s", new StreamAddOptions { Id = "1-0" }, ("f", "seed"));
        await client.Streams.ConfigureAsync("s", new() { IdempotencyMaxSize = 2 });
        var options = new StreamAddOptions { Idempotency = StreamIdempotency.Manual("p", "message") };
        var first = await client.Streams.AddAsync("s", options, ("f", "first"));
        await Assert.That(await client.Streams.AddAsync("s", options, ("f", "duplicate"))).IsEqualTo(first);
        var entries = await client.Streams.ReadAsync(new StreamReadOptions { MaxCount = 2 }, "s");
        await Assert.That(entries.Length).IsEqualTo(2);
        await Assert.That(entries[0].Id).IsEqualTo((RespireStreamId)"1-0");
        await client.Streams.CreateGroupAsync("s", "g", "0");
        var delivered = await client.Streams.ReadGroupAsync([("s", ">")], "g", "c");
        await Assert.That(delivered[0].Entries.Length).IsEqualTo(2);
        await client.Streams.ConfigureAsync("s", new() { IdempotencyMaxSize = 3 });
        var second = await client.Streams.AddAsync("s", options, ("f", "second"));
        await Assert.That(second).IsNotEqualTo(first);
        var history = await client.Streams.ReadGroupAsync([("s", "0")], "g", "c");
        await Assert.That(history[0].Entries.Length).IsEqualTo(2);
        await history[0].Entries[0].AckAsync();
        var next = await client.Streams.ReadGroupAsync([("s", ">")], "g", "c");
        await Assert.That(next[0].Entries.Length).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync("s")).IsEqualTo(3);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task IdentityExpiresExactlyAtRetentionBoundary(int protocol)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await client.Streams.AddAsync("s", ("f", "seed"));
        await client.Streams.ConfigureAsync("s", new() { IdempotencyDurationSeconds = 1 });
        var identity = new StreamAddOptions { Idempotency = StreamIdempotency.Manual("producer", "message") };
        var first = await client.Streams.AddAsync("s", identity, ("f", "first"));

        clock.Advance(TimeSpan.FromMilliseconds(999));
        await Assert.That(await client.Streams.AddAsync("s", identity, ("f", "duplicate"))).IsEqualTo(first);
        await Assert.That(await client.Streams.CountAsync("s")).IsEqualTo(2);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(await client.Streams.AddAsync("s", identity, ("f", "expired"))).IsNotEqualTo(first);
        await Assert.That(await client.Streams.CountAsync("s")).IsEqualTo(3);
        await Assert.That(ProducerCount(server)).IsEqualTo(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await client.Streams.CountAsync("s");
        await Assert.That(ProducerCount(server)).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task StreamAccessReclaimsExpiredProducersAndPreservesLiveIdentities(int access)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await client.Streams.AddAsync("s", ("f", "seed"));
        await client.Streams.ConfigureAsync("s", new() { IdempotencyDurationSeconds = 1 });
        for (var index = 0; index < 64; index++)
            await client.Streams.AddAsync("s", new StreamAddOptions { Idempotency = StreamIdempotency.Manual($"producer-{index}", "message") }, ("f", "v"));
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var live = new StreamAddOptions { Idempotency = StreamIdempotency.Manual("live", "message") };
        var liveId = await client.Streams.AddAsync("s", live, ("f", "v"));
        await Assert.That(ProducerCount(server)).IsEqualTo(65);
        clock.Advance(TimeSpan.FromMilliseconds(501));
        switch (access)
        {
            case 0: await client.Streams.AddAsync("s", ("f", "ordinary append")); break;
            case 1: await client.Streams.CountAsync("s"); break;
            case 2: await client.Streams.ConfigureAsync("s", new() { IdempotencyDurationSeconds = 1 }); break;
        }
        await Assert.That(ProducerCount(server)).IsEqualTo(1);
        await Assert.That(await client.Streams.AddAsync("s", live, ("f", "duplicate"))).IsEqualTo(liveId);
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        await Assert.That(await client.Keys.ExistsAsync("s")).IsTrue();
        await Assert.That(ProducerCount(server)).IsEqualTo(0);
    }

    // Replies alone cannot expose stale, inactive producers: consulting one would
    // expire it even in the broken implementation. Inspect retained state instead.
    private static int ProducerCount(RespireFakeServer server)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var entries = (System.Collections.IDictionary)typeof(RespireFakeServer).GetField("_entries", flags)!.GetValue(server)!;
        var entry = entries.Values.Cast<object>().Single();
        var stream = entry.GetType().GetProperty("Data", flags)!.GetValue(entry)!;
        var producers = (System.Collections.IDictionary)stream.GetType().GetProperty("Producers", flags)!.GetValue(stream)!;
        return producers.Count;
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task RetentionCapacityAndInvalidConfigurationPreserveState(int protocol)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        var identity = new StreamAddOptions { Idempotency = StreamIdempotency.Manual("p", "i") };
        await client.Streams.AddAsync("s", ("f", "seed"));
        await client.Streams.ConfigureAsync("s", new() { IdempotencyDurationSeconds = 1, IdempotencyMaxSize = 1 });
        var first = await client.Streams.AddAsync("s", identity, ("f", "first"));
        RespireValue[][] invalid = [["IDMP-DURATION", 100, "IDMP-MAXSIZE", 0], ["IDMP-DURATION", 86401], ["IDMP-MAXSIZE", 10001],
            ["IDMP-MAXSIZE", 2, "IDMP-MAXSIZE", 3],
            ["IDMP-DURATION", 100, "UNKNOWN", 2], ["IDMP-MAXSIZE"], []];
        foreach (var arguments in invalid)
        {
            await Assert.That(async () => { using var reply = await client.ExecuteAsync(RespireCommands.Stream.XCFGSET, ["s", .. arguments]); }).Throws<RespireServerException>();
            await Assert.That(await client.Streams.AddAsync("s", identity, ("f", "duplicate"))).IsEqualTo(first);
        }
        clock.Advance(TimeSpan.FromMilliseconds(1001));
        var expired = await client.Streams.AddAsync("s", identity, ("f", "expired"));
        await Assert.That(expired).IsNotEqualTo(first);
        await client.Streams.AddAsync("s", identity with { Idempotency = StreamIdempotency.Manual("q", "i") }, ("f", "other producer"));
        await Assert.That(await client.Streams.AddAsync("s", identity, ("f", "duplicate"))).IsEqualTo(expired);
        using (var expiry = await client.ExecuteAsync("PEXPIRE", "s", 1000)) { }
        await client.Streams.ConfigureAsync("s", new() { IdempotencyMaxSize = 2 });
        using (var ttl = await client.ExecuteAsync("PTTL", "s")) await Assert.That(ttl.AsInteger()).IsEqualTo(1000);
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        await Assert.That(await client.Streams.CountAsync("s")).IsEqualTo(0);
        await Assert.That(await client.Streams.AddAsync("missing", identity with { CreateStream = false }, ("f", "v"))).IsNull();
    }

    [Test]
    public async Task UnsupportedAppendOptionsDoNotCreateStreams()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        StreamAddOptions[] unsupported = [new() { MaxLength = 10 }, new() { Idempotency = StreamIdempotency.Automatic("p") }];
        foreach (var option in unsupported)
            await Assert.That(async () => await client.Streams.AddAsync("s", option, ("f", "v"))).Throws<RespireServerException>();
        await Assert.That(await client.Keys.ExistsAsync("s")).IsFalse();
    }
}
