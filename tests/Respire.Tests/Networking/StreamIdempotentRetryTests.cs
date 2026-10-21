using System.Text;
using Respire.Internal;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

// Automatic XADD producer identity (#892): retries become safe only when IDMP is attached.
public class StreamIdempotentRetryTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    public async Task LostReplyAfterWriteRetriesIntoExactlyOneEntry()
    {
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This test owns the retry-enabled client and lost-reply fault to isolate automatic XADD deduplication.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with
        {
            RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.WriteChecked },
        });
#pragma warning restore RESP010
        using var fault = server.InjectFault("XADD", RespireFakeFault.Disconnect(afterExecution: true));

        var id = await client.Streams.AddAsync("orders", ("status", "paid")).AsTask().WaitAsync(Limit);

        await Assert.That(fault.ExecutionCount).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync("orders")).IsEqualTo(1);
        // The deduplicated resend reports the entry that the lost reply carried.
        await Assert.That(id.ToString()).IsNotEqualTo(string.Empty);
    }

    [Test]
    public async Task LostReplyWithoutIdempotencyAppendsTwice()
    {
        // Positive control: the same fault duplicates an ordinary accumulating XADD.
        await using var server = new RespireFakeServer();
#pragma warning disable RESP010 // This positive control owns its client and fault to demonstrate ordinary XADD duplication after a lost reply.
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with
        {
            RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.WriteAccumulating },
        });
#pragma warning restore RESP010
        using var fault = server.InjectFault("XADD", RespireFakeFault.Disconnect(afterExecution: true));

        using (await client.ExecuteAsync(RespireCommands.Stream.XADD, new RespireValue[] { "orders", "*", "status", "paid" }).AsTask().WaitAsync(Limit)) { }

        await Assert.That(fault.ExecutionCount).IsEqualTo(1);
        await Assert.That(await client.Streams.CountAsync("orders")).IsEqualTo(2);
    }

    [Test]
    public async Task RetryEnabledAppendsAttachStableProducerAndFreshMessageIds()
    {
        await using var server = XAddServer(supportsIdempotency: true);
#pragma warning disable RESP010 // This test owns the client with its chosen producer so message identities and prefixed appends are isolated.
        await using var client = await RespireClient.ConnectAsync(Options(server, RespireCommandRetryCategory.WriteChecked) with
        {
            StreamProducerId = "orders-service",
        });
#pragma warning restore RESP010

        await client.Streams.AddAsync("s", ("f", "v"));
        await client.Streams.AddAsync("s", new RespireStreamAddOptions { MaxLength = 10, Id = "*" }, ("f", "v"));
        await client.WithKeyPrefix("tenant:").Streams.AddAsync("s", ("f", "v"));

        var adds = XAdds(server);
        await Assert.That(adds.Length).IsEqualTo(3);
        foreach (var add in adds)
        {
            var index = Array.FindIndex(add, static argument => Encoding.ASCII.GetString(argument) == "IDMP");
            await Assert.That(index).IsGreaterThan(1);
            await Assert.That(Encoding.ASCII.GetString(add[index + 1])).IsEqualTo("orders-service");
            await Assert.That(add[index + 2].Length).IsEqualTo(16);
            await Assert.That(Encoding.ASCII.GetString(add[index + 3])).IsEqualTo("*");
        }
        var messageIds = adds.Select(add => Convert.ToHexString(add[Array.FindIndex(add, a => a.AsSpan().SequenceEqual("IDMP"u8)) + 2]));
        await Assert.That(messageIds.Distinct().Count()).IsEqualTo(3);
        await Assert.That(Encoding.ASCII.GetString(adds[1][2])).IsEqualTo("MAXLEN");
    }

    [Test]
    public async Task DefaultProducerIsStablePerClientAndDistinctAcrossClients()
    {
        await using var server = XAddServer(supportsIdempotency: true);
        var options = Options(server, RespireCommandRetryCategory.WriteAccumulating);
#pragma warning disable RESP010 // This test deliberately owns two independent clients to compare stable per-client producer identities.
        await using var first = await RespireClient.ConnectAsync(options);
#pragma warning restore RESP010
#pragma warning disable RESP010 // This test deliberately owns two independent clients to compare stable per-client producer identities.
        await using var second = await RespireClient.ConnectAsync(options);
#pragma warning restore RESP010

        await first.Streams.AddAsync("s", ("f", "v"));
        await first.Streams.AddAsync("s", ("f", "v"));
        await second.Streams.AddAsync("s", ("f", "v"));

        var producers = XAdds(server).Select(add => Encoding.ASCII.GetString(add[3])).ToArray();
        await Assert.That(producers[0]).StartsWith("respire-");
        await Assert.That(producers[1]).IsEqualTo(producers[0]);
        await Assert.That(producers[2]).IsNotEqualTo(producers[0]);
    }

    [Test]
    [Arguments(null)]
    [Arguments(RespireCommandRetryCategory.ReadOnly)]
    [Arguments(RespireCommandRetryCategory.Never)]
    public async Task PoliciesThatCannotRetryWritesSendOrdinaryXAdd(RespireCommandRetryCategory? maximum)
    {
        await using var server = XAddServer(supportsIdempotency: true);
        var options = Options(server, RespireCommandRetryCategory.WriteChecked) with
        {
            RetryPolicy = maximum is { } category ? new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = category } : null,
        };
#pragma warning disable RESP010 // This test owns a client per retry-policy case to verify that ordinary XADD omits automatic identity.
        await using var client = await RespireClient.ConnectAsync(options);
#pragma warning restore RESP010

        await client.Streams.AddAsync("s", ("f", "v"));

        await Assert.That(XAdds(server).Single().Select(Encoding.ASCII.GetString))
            .IsEquivalentTo(new[] { "XADD", "s", "*", "f", "v" });
    }

    [Test]
    public async Task ConnectionLossMustBeSelectedToAttachIdentity()
    {
        await using var server = XAddServer(supportsIdempotency: true);
#pragma warning disable RESP010 // This test owns the client with its selected retry errors to isolate automatic identity admission.
        await using var client = await RespireClient.ConnectAsync(Options(server, RespireCommandRetryCategory.WriteChecked) with
        {
            RetryPolicy = new()
            {
                InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = RespireCommandRetryCategory.WriteChecked,
                RetryableErrors = RespireRetryableErrors.TryAgain,
            },
        });
#pragma warning restore RESP010

        await client.Streams.AddAsync("s", ("f", "v"));

        await Assert.That(XAdds(server).Single().Length).IsEqualTo(5);
    }

    [Test]
    public async Task CallerChoicesAreNotReplaced()
    {
        await using var server = XAddServer(supportsIdempotency: true);
#pragma warning disable RESP010 // This test owns the retry-enabled client to verify caller-selected IDs and producers are preserved.
        await using var client = await RespireClient.ConnectAsync(Options(server, RespireCommandRetryCategory.WriteChecked));
#pragma warning restore RESP010

        await client.Streams.AddAsync("s", new RespireStreamAddOptions { Id = "5-0" }, ("f", "v"));
        await client.Streams.AddAsync("s", new RespireStreamAddOptions { Idempotency = RespireStreamIdempotency.Manual("mine", "m-1") }, ("f", "v"));
        await client.Streams.AddAsync("s", new RespireStreamAddOptions { Idempotency = RespireStreamIdempotency.Automatic("mine") }, ("f", "v"));

        var adds = XAdds(server).Select(add => add.Select(Encoding.ASCII.GetString).ToArray()).ToArray();
        await Assert.That(adds[0]).IsEquivalentTo(new[] { "XADD", "s", "5-0", "f", "v" });
        await Assert.That(adds[1]).IsEquivalentTo(new[] { "XADD", "s", "IDMP", "mine", "m-1", "*", "f", "v" });
        await Assert.That(adds[2]).IsEquivalentTo(new[] { "XADD", "s", "IDMPAUTO", "mine", "*", "f", "v" });
    }

    [Test]
    public async Task OlderServersFallBackForEachAppendToOrdinaryXAdd()
    {
        await using var server = XAddServer(supportsIdempotency: false);
#pragma warning disable RESP010 // This test owns the client and legacy-server script to isolate per-append idempotency fallback.
        await using var client = await RespireClient.ConnectAsync(Options(server, RespireCommandRetryCategory.WriteChecked));
#pragma warning restore RESP010

        await Assert.That(await client.Streams.AddAsync("s", ("f", "v"))).IsEqualTo((RespireStreamId)"1-0");
        await Assert.That(await client.Streams.AddAsync("s", new RespireStreamAddOptions { MaxLength = 5 }, ("f", "v")))
            .IsEqualTo((RespireStreamId?)"1-0");

        var adds = XAdds(server).Select(add => string.Join(' ', add.Take(3).Select(Encoding.ASCII.GetString))).ToArray();
        await Assert.That(adds).IsEquivalentTo(new[] { "XADD s IDMP", "XADD s *", "XADD s MAXLEN", "XADD s MAXLEN" });
        await Assert.That(XAdds(server)[2].Any(argument => argument.AsSpan().SequenceEqual("IDMP"u8))).IsTrue();
        await Assert.That(XAdds(server)[3].Any(argument => argument.AsSpan().SequenceEqual("IDMP"u8))).IsFalse();
    }

    [Test]
    [Arguments("ERR rejected")]
    [Arguments("ERR syntax error")]
    [Arguments("ERR The ID specified in XADD is equal or smaller than the target stream top item")]
    [Arguments("ERR syntax error, IDMP requires a non-empty producer ID")]
    public async Task UnrelatedFirstErrorDoesNotResendOrDisableIdentity(string error)
    {
        await using var server = XAddServer(supportsIdempotency: true);
        var normalReply = server.ReplyOverride!;
        var reject = true;
        server.ReplyOverride = (index, command) => reject && command.StartsWith("XADD", StringComparison.Ordinal)
            ? Encoding.ASCII.GetBytes($"-{error}\r\n") : normalReply(index, command);
#pragma warning disable RESP010 // This test owns the client and changing error script to verify unrelated errors do not disable identity.
        await using var client = await RespireClient.ConnectAsync(Options(server, RespireCommandRetryCategory.WriteChecked));
#pragma warning restore RESP010

        await Assert.That(async () => await client.Streams.AddAsync("s", ("f", "v"))).ThrowsExactly<RespireServerException>();
        await Assert.That(XAdds(server).Length).IsEqualTo(1);
        reject = false;
        await client.Streams.AddAsync("s", ("f", "v"));
        await Assert.That(XAdds(server).Length).IsEqualTo(2);
        await Assert.That(XAdds(server).All(add => add[2].AsSpan().SequenceEqual("IDMP"u8))).IsTrue();
    }

    [Test]
    public async Task CapabilityChangesAreDetectedOnEachAppend()
    {
        await using var server = XAddServer(supportsIdempotency: false);
#pragma warning disable RESP010 // This test owns the client and simulated server upgrades/downgrades to isolate per-append capability detection.
        await using var client = await RespireClient.ConnectAsync(Options(server, RespireCommandRetryCategory.WriteChecked));
#pragma warning restore RESP010
        var olderReply = server.ReplyOverride!;
        await client.Streams.AddAsync("s", ("f", "v"));
        // Simulate an upgrade, then a downgrade, at the same endpoint.
        server.ReplyOverride = (index, command) => command.StartsWith("XADD", StringComparison.Ordinal)
            ? "$3\r\n1-0\r\n"u8.ToArray() : olderReply(index, command);
        await client.Streams.AddAsync("s", ("f", "v"));
        server.ReplyOverride = olderReply;
        await client.Streams.AddAsync("s", ("f", "v"));

        var identities = XAdds(server).Select(add => add[2].AsSpan().SequenceEqual("IDMP"u8)).ToArray();
        await Assert.That(identities).IsEquivalentTo(new[] { true, false, true, true, false }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MixedClusterVersionsDoNotShareCapability(bool newerFirst)
    {
        await using var older = XAddServer(supportsIdempotency: false);
        await using var newer = XAddServer(supportsIdempotency: true);
        const string olderKey = "{older}:s", newerKey = "{newer}:s";
        var olderSlot = ClusterHash.GetSlot(olderKey);
        var newerSlot = ClusterHash.GetSlot(newerKey);
        var map = Encoding.ASCII.GetBytes($"*2\r\n*3\r\n:{olderSlot}\r\n:{olderSlot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{older.Port}\r\n*3\r\n:{newerSlot}\r\n:{newerSlot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{newer.Port}\r\n");
        await using var seed = new FakeRespServer(map, map);
#pragma warning disable RESP010 // This test owns the cluster client and mixed-version topology so capability state cannot cross scenarios.
        await using var client = await RespireClient.ConnectAsync(Options(seed, RespireCommandRetryCategory.WriteChecked) with { UseCluster = true });
#pragma warning restore RESP010

        await client.Streams.AddAsync(newerFirst ? newerKey : olderKey, ("f", "v"));
        await client.Streams.AddAsync(newerFirst ? olderKey : newerKey, ("f", "v"));
        await client.Streams.AddAsync(newerKey, ("f", "v"));

        await Assert.That(XAdds(older).Length).IsEqualTo(2);
        await Assert.That(XAdds(older)[0][2].AsSpan().SequenceEqual("IDMP"u8)).IsTrue();
        await Assert.That(XAdds(older)[1][2].AsSpan().SequenceEqual("*"u8)).IsTrue();
        await Assert.That(XAdds(newer).Length).IsEqualTo(2);
        await Assert.That(XAdds(newer).All(add => add[2].AsSpan().SequenceEqual("IDMP"u8))).IsTrue();
    }

    [Test]
    public async Task ConfirmedSupportDoesNotHideLaterErrors()
    {
        var reject = false;
        await using var server = XAddServer(supportsIdempotency: true);
        server.ReplyOverride = (_, command) => command switch
        {
            _ when command.StartsWith("HELLO", StringComparison.Ordinal) => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "CLIENT ID" => ":1\r\n"u8.ToArray(),
            _ when command.StartsWith("XADD", StringComparison.Ordinal) && reject => "-ERR rejected\r\n"u8.ToArray(),
            _ when command.StartsWith("XADD", StringComparison.Ordinal) => "$3\r\n1-0\r\n"u8.ToArray(),
            _ => null,
        };
#pragma warning disable RESP010 // This test owns the client and later-error script to isolate errors after a successful identity probe.
        await using var client = await RespireClient.ConnectAsync(Options(server, RespireCommandRetryCategory.WriteChecked));
#pragma warning restore RESP010
        await client.Streams.AddAsync("s", ("f", "v"));
        reject = true;

        await Assert.That(async () => await client.Streams.AddAsync("s", ("f", "v"))).ThrowsExactly<RespireServerException>();
        await Assert.That(XAdds(server).Length).IsEqualTo(2);
    }

    [Test]
    [Arguments(128)]
    [Arguments(129)]
    public async Task OverlongMinIdCannotTriggerIdempotencyFallback(int length)
    {
        await using var server = XAddServer(supportsIdempotency: true);
#pragma warning disable RESP010 // This test owns the client and XADD script to prove an overlong minimum ID fails without a fallback attempt.
        await using var client = await RespireClient.ConnectAsync(Options(server, RespireCommandRetryCategory.WriteChecked));
#pragma warning restore RESP010

        var minimumId = new string('0', length - 2) + "-0";
        await Assert.That(async () => await client.Streams.AddAsync("s", new RespireStreamAddOptions { MinId = minimumId }, ("f", "v")))
            .ThrowsExactly<ArgumentException>();
        await Assert.That(XAdds(server).Length).IsEqualTo(0);
    }

    [Test]
    public async Task MinIdAtServerLengthLimitIsAccepted()
    {
        await using var server = XAddServer(supportsIdempotency: true);
#pragma warning disable RESP010 // This test owns the client and XADD script to isolate acceptance at the server's minimum-ID length limit.
        await using var client = await RespireClient.ConnectAsync(Options(server, RespireCommandRetryCategory.WriteChecked));
#pragma warning restore RESP010
        var minimumId = new string('0', 125) + "-0";

        await client.Streams.AddAsync("s", new RespireStreamAddOptions { MinId = minimumId }, ("f", "v"));
        await Assert.That(XAdds(server).Length).IsEqualTo(1);
    }

    [Test]
    public async Task LegacyFallbackSharesTheProbeCommandTimeout()
    {
        await using var server = XAddServer(supportsIdempotency: false);
        // Each XADD fits within CommandTimeout alone, but the probe and fallback together do not.
        server.DelayCommand("XADD", 700);
#pragma warning disable RESP010 // This test owns the client and delayed legacy-server replies to verify one deadline covers probe and fallback.
        await using var client = await RespireClient.ConnectAsync(Options(server, RespireCommandRetryCategory.WriteChecked) with
        {
            CommandTimeout = TimeSpan.FromSeconds(1),
        });
#pragma warning restore RESP010

        await Assert.That(async () => await client.Streams.AddAsync("s", ("f", "v")).AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireTimeoutException>();
        await Assert.That(XAdds(server).Length).IsEqualTo(2);
    }

    [Test]
    public async Task EmptyProducerIdIsRejected()
    {
        await Assert.That(() => RespireClient.Create(new RespireOptions { Endpoints = [new("localhost")], StreamProducerId = "" }))
            .ThrowsExactly<RespireConfigurationException>();
    }

    private static FakeRespServer XAddServer(bool supportsIdempotency) => new(4, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command switch
        {
            _ when command.StartsWith("HELLO", StringComparison.Ordinal) => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "CLIENT ID" => ":1\r\n"u8.ToArray(),
            _ when command.StartsWith("XADD", StringComparison.Ordinal) && !supportsIdempotency && command.Contains(" IDMP ", StringComparison.Ordinal)
                => "-ERR Invalid stream ID specified as stream command argument\r\n"u8.ToArray(),
            _ when command.StartsWith("XADD", StringComparison.Ordinal) => "$3\r\n1-0\r\n"u8.ToArray(),
            _ => null,
        },
    };

    private static RespireOptions Options(FakeRespServer server, RespireCommandRetryCategory maximum) => new()
    {
        Endpoints = { new("127.0.0.1", server.Port) }, Protocol = RespireProtocol.Resp2, ThreadPoolMonitoring = false, Connections = 1,
        CommandTimeout = TimeSpan.FromSeconds(3),
        RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = maximum },
    };

    private static byte[][][] XAdds(FakeRespServer server)
        => server.ReceivedArguments.Where(static command => Encoding.ASCII.GetString(command[0]) == "XADD").ToArray();
}
