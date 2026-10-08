using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    public async Task ShardedRecoveryRejectionReportsOneInternalError()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(6, FakeRespServer.OkReply);
        var recovering = false;
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => System.Text.Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n"),
            "SSUBSCRIBE one" => Volatile.Read(ref recovering) ? "-NOPERM recovery rejected\r\n"u8.ToArray()
                : "*3\r\n$10\r\nssubscribe\r\n$3\r\none\r\n:1\r\n"u8.ToArray(),
            "SUNSUBSCRIBE one" => "*3\r\n$12\r\nsunsubscribe\r\n$3\r\none\r\n:0\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", server.Port)],
            ReconnectPolicy = new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 },
        });
        await using var subscription = await client.SubscribeShardedAsync("one").AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var index = server.ReceivedCommands.ToList().LastIndexOf("SSUBSCRIBE one");
        using var capture = new Capture(throwOnMeasurement: true);
        Volatile.Write(ref recovering, true);
        await server.SendRawAsync("*3\r\n$12\r\nsunsubscribe\r\n$3\r\none\r\n:0\r\n"u8.ToArray(), server.ReceivedConnectionIds[index]);
        await Assert.That(await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(10)))
            .IsEqualTo(RespireSubscriptionEndReason.ReconnectExhausted);
        var item = capture.Items.Single();
        await Assert.That(item.Tags["db.response.status_code"]).IsEqualTo("NOPERM");
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    public async Task ObservationAttemptsClampNegativeValuesWithoutLosingHighCounts()
    {
        using var owner = RespireTelemetry.ErrorObservation.Rent(force: true);
        owner.SetAttempts(-1);
        await Assert.That(owner.Attempts).IsEqualTo(0);
        owner.SetAttempts(17);
        await Assert.That(owner.Attempts).IsEqualTo(17);
    }

    [Test]
    public async Task DedicatedReplicaHandshakeFailureIsHandledBeforeFallback()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var primary = StandaloneReplicaReadyTests.Server();
        await using var replica = StandaloneReplicaReadyTests.Server();
        await using var client = await RespireClient.ConnectAsync(StandaloneReplicaReadyTests.Options(primary, replica)
            with { Password = "test-password" });
        await client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("warm");
        replica.ReplyOverride = (_, command) => command.StartsWith("AUTH ", StringComparison.Ordinal)
            ? "-WRONGPASS dedicated authentication rejected\r\n"u8.ToArray() : null;
        using var capture = new Capture(throwOnMeasurement: true);
        var lease = await client.Core.ReadRouter.RentDedicatedConnectionAsync(RespireReadFrom.ReplicaPreferred, default);
        try
        {
            await Assert.That(lease.IsReplica).IsFalse();
            var item = capture.Items.Single();
            await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(item.Tags["db.response.status_code"]).IsEqualTo("WRONGPASS");
        }
        finally { lease.Pool.Return(lease.Connection); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DedicatedReplicaRoleRejectionIsHandledBeforeFallback(bool serverError)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var primary = StandaloneReplicaReadyTests.Server();
        await using var replica = StandaloneReplicaReadyTests.Server();
        await using var client = await RespireClient.ConnectAsync(StandaloneReplicaReadyTests.Options(primary, replica));
        await client.WithReadFrom(RespireReadFrom.Replica).GetStringAsync("warm");
        replica.ReplyOverride = (_, command) => command == "ROLE" ? (serverError
            ? "-NOPERM dedicated role rejected\r\n"u8.ToArray()
            : "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray()) : null;
        using var capture = new Capture(throwOnMeasurement: true);
        var lease = await client.Core.ReadRouter.RentDedicatedConnectionAsync(RespireReadFrom.ReplicaPreferred, default);
        try
        {
            await Assert.That(lease.IsReplica).IsFalse();
            var item = capture.Items.Single();
            await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            if (serverError) await Assert.That(item.Tags["db.response.status_code"]).IsEqualTo("NOPERM");
        }
        finally { lease.Pool.Return(lease.Connection); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MultiReplyReportsDiscardedErrorsSeparatelyFromRetainedFinal(bool reserve)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        var source = Respire.Networking.MultiReplyPendingResponseSource.Rent(4, 0, "MULTI/EXEC");
        source.ErrorAttempts = 3;
        using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        observation.SetAttempts(source.ErrorAttempts);
        var pending = RespireTelemetry.ObserveFinalError(ConsumeOwnerFollowupMultiReplyAsync(source.Task), observation).AsTask();
        var scheduler = new Respire.Networking.CompletionScheduler();
        foreach (var code in new[] { "WRONGTYPE", "NOPERM", "ERR", "EXECABORT" })
        {
            var reply = RespValue.Error(System.Text.Encoding.ASCII.GetBytes(code + " rejected"));
            if (reserve) { scheduler.Add(source, in reply); scheduler.Flush(); }
            else { source.TrySetResult(in reply); source.ReleaseRef(); }
        }
        await Assert.That(async () => await pending).Throws<RespireServerException>();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(4);
        await Assert.That(items.Count(item => !(bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(1);
        await Assert.That(items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!)
            .Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
        await Assert.That(items.All(item => Equals(item.Tags["redis.client.operation.retry_attempts"], 3))).IsTrue();
    }

    private static async ValueTask ConsumeOwnerFollowupMultiReplyAsync(ValueTask<RespValue> response)
    {
        using var reply = await response.ConfigureAwait(false);
        if (reply.IsError) throw ResponseReader.ServerError(in reply, "MULTI/EXEC");
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("integer")]
    public async Task PreparedReplicaSourceReportsFinalErrorOnce(string shape)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var primary = StandaloneReplicaReadyTests.Server();
        await using var replica = StandaloneReplicaReadyTests.Server();
        await using var client = await RespireClient.ConnectAsync(StandaloneReplicaReadyTests.Options(primary, replica));
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        await view.GetStringAsync("warm");
        replica.ReplyOverride = (_, command) => command.EndsWith(" failing", StringComparison.Ordinal)
            ? "-WRONGTYPE prepared reply\r\n"u8.ToArray() : null;
        using var capture = new Capture(throwOnMeasurement: true);
        await Assert.That(RespireTelemetry.IsOperationEnabled("GET")).IsFalse();
        await Assert.That(async () =>
        {
            switch (shape)
            {
                case "string": await view.GetStringAsync("failing"); break;
                case "bytes": await view.GetBytesAsync("failing"); break;
                default: await view.Strings.LengthAsync("failing"); break;
            }
        }).Throws<RespireServerException>();
        var item = capture.Items.Single();
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(primary.ReceivedCommands.Any(command => command.EndsWith(" failing", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task PendingErrorReportingPreservesPublicStateAndReportsOnce()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        var pending = new RespirePending<long>();
        var error = new RespireServerException("WRONGTYPE terminal");
        pending.AddErrorAttempts(17);
        pending.Fail(error);
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => pending.ReportError())));
        await Assert.That(pending.Status).IsEqualTo(RespirePendingStatus.Faulted);
        await Assert.That(pending.Error).IsSameReferenceAs(error);
        await Assert.That(pending.HasResult).IsFalse();
        await Assert.That(pending.IsCompleted).IsTrue();
        await Assert.That(() => pending.Result).Throws<RespireServerException>();
        pending.Fail(error);
        await Assert.That(pending.ReportError()).IsTrue();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(capture.Items.Single().Tags["redis.client.operation.retry_attempts"]).IsEqualTo(17);
        pending.Succeed(42);
        await Assert.That(pending.Status).IsEqualTo(RespirePendingStatus.Succeeded);
        await Assert.That(pending.Result).IsEqualTo(42L);
        await Assert.That(pending.ReportError()).IsFalse();
        pending.Abort();
        await Assert.That(pending.Status).IsEqualTo(RespirePendingStatus.Aborted);
        await Assert.That(pending.Error).IsNull();
        await Assert.That(pending.ReportError()).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplicaRoleRejectionIsHandledBeforePrimaryFallback(bool serverError)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var primary = new FakeRespServer("$2\r\nok\r\n"u8.ToArray());
        await using var replica = new FakeRespServer(serverError
            ? "-NOPERM role rejected\r\n"u8.ToArray()
            : "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)], ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            ReadFrom = RespireReadFrom.ReplicaPreferred,
        });
        using var capture = new Capture(throwOnMeasurement: true);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("ok");
        var item = capture.Items.Single();
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        if (serverError) await Assert.That(item.Tags["db.response.status_code"]).IsEqualTo("NOPERM");
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["ROLE"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HedgeHandoffPreservesCallerHandledPrefix(bool alternative)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var primary = new FakeRespServer("-WRONGTYPE terminal\r\n"u8.ToArray());
        await using var replica = new FakeRespServer("*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray());
        replica.ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
            ? "-WRONGTYPE terminal\r\n"u8.ToArray() : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", alternative ? replica.Port : primary.Port)],
            ReadFrom = alternative ? RespireReadFrom.ReplicaPreferred : RespireReadFrom.PrimaryPreferred,
            HedgedReads = new() { Delay = TimeSpan.FromMilliseconds(1), MaximumExtraLoadPercent = 100 },
        });
        using var capture = new Capture();
        using var owner = RespireTelemetry.ErrorObservation.Rent(force: true);
        owner.Handled(new IOException("earlier caller retry"));
        var error = await Assert.That(async () =>
        {
            using var reply = await client.SendForCorrectionAsync("GET", new OwnerFollowupReadCommand(), default, owner);
            if (reply.IsError) throw new RespireServerException(reply.AsString()!);
        }).Throws<RespireServerException>();
        owner.Final(error!);
        var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
    }

    private readonly struct OwnerFollowupReadCommand : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.Read;
        public void Write(ref RespWriter writer) => writer.WriteRaw("*2\r\n$3\r\nGET\r\n$3\r\nkey\r\n"u8);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PendingHedgeWinnerPreservesCallerHandledPrefix(bool hedgeWins)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var primary = new FakeRespServer(FakeRespServer.OkReply)
            { SuppressReply = command => command == "GET key" };
        await using var replica = new FakeRespServer("*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray())
            { SuppressReply = command => command == "GET key" };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)], ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            ReadFrom = RespireReadFrom.ReplicaPreferred,
            HedgedReads = new() { Delay = TimeSpan.FromMilliseconds(1), MaximumExtraLoadPercent = 100 },
        });
        using var capture = new Capture(throwOnMeasurement: true);
        using var owner = RespireTelemetry.ErrorObservation.Rent(force: true);
        owner.Handled(new IOException("earlier caller retry"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = client.SendForCorrectionAsync("GET", new OwnerFollowupReadCommand(), deadline.Token, owner).AsTask();
        while (!primary.ReceivedCommands.Contains("GET key") || !replica.ReceivedCommands.Contains("GET key"))
            await Task.Delay(1, deadline.Token);
        var winner = hedgeWins ? primary : replica;
        var loser = hedgeWins ? replica : primary;
        await winner.SendRawAsync("$2\r\nok\r\n"u8.ToArray());
        using var result = await response.WaitAsync(deadline.Token);
        await Assert.That(result.AsString()).IsEqualTo("ok");
        owner.Final(new RespireServerException("WRONGTYPE caller conversion"));
        var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await loser.SendRawAsync("-WRONGTYPE discarded leg\r\n"u8.ToArray());
        while (capture.Items.Count < 3) await Task.Delay(1, deadline.Token);
        await Assert.That(capture.Items.Count).IsEqualTo(3);
        await Assert.That(capture.Items.Last().Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }
}
