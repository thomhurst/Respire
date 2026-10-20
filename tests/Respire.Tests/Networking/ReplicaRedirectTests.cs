using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReplicaRedirectTests
{
    [Test]
    [NotInParallel]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task RetirementBeforeRedirectAdmissionReportsReplacementEndpoint(bool circuit, bool script)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Command });
        var activities = new List<Activity>();
        var measurements = new List<Dictionary<string, object?>>();
        Action? beforeScriptSend = null;
        var procedure = RespireScript.Create("return 7");
        var operation = script ? procedure.EvalShaOperation : "GET";
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Respire",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (Equals(activity.GetTagItem("db.stored_procedure.name"), procedure.Sha1)) beforeScriptSend?.Invoke();
            },
            ActivityStopped = activity => { lock (activities) activities.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, capture) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.operation.duration")
                capture.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            lock (measurements) measurements.Add(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value));
        });
        meter.Start();
        await using var target = Server();
        await using var source = Server();
        await using var client = await RespireClient.ConnectAsync(Options(source.Port) with
        {
            Protocol = RespireProtocol.Resp3, MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            CircuitBreaker = circuit ? new() : null,
        });
        var original = client.Core.Multiplexer.GetConnection();
        var announcement = client.Core.Multiplexer.CaptureMovingAnnouncement(0, original,
            new MaintenanceNotification("MOVING", 1, 10, new("127.0.0.1", target.Port)));
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Core.Multiplexer.MovingHandoffPublished += () => published.TrySetResult();
        async Task PublishReplacement()
        {
            client.Core.Multiplexer.QueueDedicatedMovingHandoff(original, announcement, static () => true);
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (script)
        {
            // Script selection precedes activity creation; retire that selection before admission.
            beforeScriptSend = () => PublishReplacement().GetAwaiter().GetResult();
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(procedure, "key")).IsEqualTo(7);
        }
        else
        {
            await PublishReplacement();
            using var reply = await client.SendOnConnectionAsync("GET", original,
                new Respire.Commands.Cmd1(Respire.Commands.Verbs.Get, "key"), timeout.Token);
            await Assert.That(reply.AsString()).IsEqualTo("value");
        }
        var wireOperation = script ? "EVALSHA " : "GET ";
        await Assert.That(source.ReceivedCommands.Any(command => command.StartsWith(wireOperation, StringComparison.Ordinal))).IsFalse();
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith(wireOperation, StringComparison.Ordinal))).IsEqualTo(1);
        Activity[] traces;
        Dictionary<string, object?>[] durations;
        lock (activities) traces = activities.Where(activity => Equals(activity.GetTagItem("db.operation.name"), operation)).ToArray();
        lock (measurements) durations = measurements.Where(tags => Equals(tags.GetValueOrDefault("db.operation.name"), operation)).ToArray();
        await Assert.That(traces.Length).IsEqualTo(1);
        await Assert.That(traces[0].GetTagItem("server.port")).IsEqualTo(target.Port);
        await Assert.That(durations.Length).IsEqualTo(1);
        await Assert.That(durations[0]["server.port"]).IsEqualTo(target.Port);

        static FakeRespServer Server() => new(4)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Bytes("%1\r\n$5\r\nproto\r\n:3\r\n")
                : command.StartsWith("EVALSHA ", StringComparison.Ordinal) ? Bytes(":7\r\n")
                : command == "GET key" ? Bytes("$5\r\nvalue\r\n") : FakeRespServer.OkReply,
        };
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CapabilityPipelinesAfterAuthAndIgnoresUnsupportedServers(bool supported)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply,
            supported ? FakeRespServer.OkReply : Bytes("-ERR unknown subcommand 'CAPA'\r\n"), FakeRespServer.PongReply)
        { MinimumCommandsBeforeReply = 2 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { Password = "secret", AdvertiseReplicaRedirects = true }, cancellationToken: timeout.Token);
        server.MinimumCommandsBeforeReply = 1;
        using var pong = await connection.SendAsync(new Respire.Commands.RawCommand(FakeRespServer.PingFrame), timeout.Token);
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["AUTH secret", "CLIENT CAPA REDIRECT", "PING"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadOnlyFollowsOnlySuccessfulCapability(bool supported)
    {
        await using var server = new FakeRespServer(
            supported ? FakeRespServer.OkReply : Bytes("-NOPERM unavailable\r\n"), FakeRespServer.OkReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { AdvertiseReplicaRedirects = true, ReplicaRedirectReadOnly = true });
        string[] expected = supported ? ["CLIENT CAPA REDIRECT", "READONLY"] : ["CLIENT CAPA REDIRECT"];
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments("127.0.0.1:6380", "127.0.0.1", 6380)]
    [Arguments("primary.example:6380", "primary.example", 6380)]
    [Arguments("[::1]:6380", "::1", 6380)]
    [Arguments("2001:db8::1:6380", "2001:db8::1", 6380)]
    [Arguments(":6380", "source", 6380)]
    public async Task RedirectParsesEndpoint(string address, string host, int port)
    {
        await Assert.That(RespireClient.TryParseReplicaRedirect("REDIRECT " + address, "source", out var endpoint)).IsTrue();
        await Assert.That(endpoint).IsEqualTo(new RespireEndpoint(host, port));
    }

    [Test]
    [Arguments("REDIRECT bad")]
    [Arguments("REDIRECT host:0")]
    [Arguments("REDIRECT host:65536")]
    [Arguments("REDIRECT ?:6379")]
    [Arguments("REDIRECT host:6379 extra")]
    [Arguments("REDIRECT host:-1")]
    [Arguments("MOVED 0 host:6379")]
    public async Task MalformedRedirectDoesNotConnect(string message)
        => await Assert.That(RespireClient.TryParseReplicaRedirect(message, "source", out _)).IsFalse();

    [Test]
    public async Task KeylessWriteAndTypedReadFollowRedirectWithDatabaseAndCredentials()
    {
        await using var primary = new FakeRespServer(2)
        {
            ReplyOverride = (_, command) => command == "RANDOMKEY" ? Bytes("$3\r\nkey\r\n") : FakeRespServer.OkReply,
        };
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command is "FLUSHDB" or "RANDOMKEY"
                ? Bytes($"-REDIRECT 127.0.0.1:{primary.Port}\r\n") : FakeRespServer.OkReply,
        };
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        { Database = 2, Password = "secret", AllowAdmin = true });
        await client.Server.FlushDatabaseAsync();
        using var key = await client.ExecuteAsync(RespireCommands.Key.RANDOMKEY);
        await Assert.That(key.AsString()).IsEqualTo("key");
        await Assert.That(primary.ReceivedCommands.Count(command => command == "FLUSHDB")).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Count(command => command == "RANDOMKEY")).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Count(command => command == "AUTH secret")).IsEqualTo(2);
        await Assert.That(primary.ReceivedCommands.Count(command => command == "SELECT 2")).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FireAndForgetFollowsRedirectBeforeCompleting(bool backlog)
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT" ? FakeRespServer.OkReply
                : Bytes($"-REDIRECT 127.0.0.1:{primary.Port}\r\n"),
        };
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        { BacklogPolicy = backlog ? RespireBacklogPolicy.Queue : RespireBacklogPolicy.FailFast });
        await client.ExecuteFireAndForgetAsync("SET", "key", "value");
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["CLIENT CAPA REDIRECT", "SET key value"]);
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(true, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, true, false)]
    [Arguments(false, true, true)]
    [Arguments(true, true, true)]
    public async Task FireAndForgetSurfacesTerminalRedirect(bool backlog, bool follow, bool loop)
    {
        var targetPort = 0;
        await using var target = new FakeRespServer(20)
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT" ? FakeRespServer.OkReply
                : Bytes(loop ? $"-REDIRECT 127.0.0.1:{targetPort}\r\n" : "-REDIRECT bad\r\n"),
        };
        targetPort = target.Port;
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT" ? FakeRespServer.OkReply
                : Bytes(follow ? $"-REDIRECT 127.0.0.1:{targetPort}\r\n" : "-REDIRECT bad\r\n"),
        };
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        { BacklogPolicy = backlog ? RespireBacklogPolicy.Queue : RespireBacklogPolicy.FailFast });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.That(async () =>
            await client.ExecuteFireAndForgetAsync("SET", ["key", "value"], timeout.Token))
            .Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("REDIRECT");
        await Assert.That(replica.ReceivedCommands.Count(command => command == "SET key value")).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands.Count(command => command == "SET key value"))
            .IsEqualTo(loop ? Respire.Internal.ClusterRouter.RedirectLimit : follow ? 1 : 0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FireAndForgetDiscardsOrdinaryErrorsAfterRedirect(bool backlog)
    {
        await using var primary = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT" ? FakeRespServer.OkReply
                : Bytes("-WRONGTYPE wrong value kind\r\n"),
        };
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT" ? FakeRespServer.OkReply
                : Bytes($"-REDIRECT 127.0.0.1:{primary.Port}\r\n"),
        };
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        { BacklogPolicy = backlog ? RespireBacklogPolicy.Queue : RespireBacklogPolicy.FailFast });
        await client.ExecuteFireAndForgetAsync("SET", "key", "value");
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["CLIENT CAPA REDIRECT", "SET key value"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OrdinaryScriptsFollowRedirectAndNoScriptFallback(bool cache)
    {
        await using var primary = new FakeRespServer(4)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Bytes("%1\r\n$5\r\nproto\r\n:3\r\n")
                : command.StartsWith("EVALSHA ", StringComparison.Ordinal)
                ? Bytes("-NOSCRIPT missing\r\n") : command.StartsWith("EVAL ", StringComparison.Ordinal)
                    ? Bytes(":7\r\n") : FakeRespServer.OkReply,
        };
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Bytes("%1\r\n$5\r\nproto\r\n:3\r\n")
                : command.StartsWith("EVAL", StringComparison.Ordinal)
                    ? Bytes($"-REDIRECT 127.0.0.1:{primary.Port}\r\n") : FakeRespServer.OkReply,
        };
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        { ClientSideCache = cache ? new RespireClientSideCacheOptions() : null });
        var script = RespireScript.Create("return 7");
        var result = await client.Scripts.ExecuteIntegerAsync(script, "key");
        await Assert.That(result).IsEqualTo(7);
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("EVALSHA ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    [Arguments(true, true, true)]
    public async Task CorrectionBoundLocksKeepTheirCapturedConnection(bool extend, bool lua, bool cache)
    {
        await using var primary = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var replica = CorrectionReplica(primary.Port, lua);
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        { ClientSideCache = cache ? new RespireClientSideCacheOptions() : null, RetryPolicy = new() });
        await client.EnsureReliableCorrectionOrderingAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var execution = await client.StartLockExecutionAsync("key", "owner", extend ? 1000 : null,
            requireIdentity: true, allowUnfencedFallback: false, cancellation.Token);
        var error = await Assert.That(async () => await execution.ConsumeResponseAsync()).Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("REDIRECT");
        await Assert.That(execution.ConnectionIdentity.Endpoint.Port).IsEqualTo(replica.Port);
        await Assert.That(execution.ConnectionIdentity.ServerClientId).IsEqualTo(41);
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
        var verb = lua ? "EVALSHA " : extend ? "SET " : "DELEX ";
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith(verb, StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task RetryPolicyCanRetryMutationWhenRedirectTargetHandshakeFailsBeforeSend()
    {
        await using var target = new FakeRespServer(2)
        {
            CloseConnectionAfterCommand = 1,
            ReplyOverride = (_, command) => command == "INCR counter" ? Bytes(":1\r\n") : FakeRespServer.OkReply,
        };
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT" ? FakeRespServer.OkReply
                : Bytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n"),
        };
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        { RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero } });
        using var reply = await client.ExecuteAsync(RespireCommands.String.INCR, ["counter"]);
        await Assert.That(reply.AsInteger()).IsEqualTo(1);
        await Assert.That(replica.ReceivedCommands.Count(command => command == "INCR counter")).IsEqualTo(2);
        await Assert.That(target.ReceivedCommands.Count(command => command == "INCR counter")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task TrackedScriptsKeepTheirCapturedConnection(bool captureTimestampOnly, bool cache)
    {
        await using var primary = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var replica = CorrectionReplica(primary.Port, lua: false);
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        { ClientSideCache = cache ? new RespireClientSideCacheOptions() : null, RetryPolicy = new() });
        await client.EnsureReliableCorrectionOrderingAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var execution = await client.StartTrackedScriptExecutionAsync(RespireScript.Create("return 1"),
            ["key"], [], cancellation.Token, requireReliableCorrectionOrdering: true,
            captureSendTimestampOnly: captureTimestampOnly);
        var error = await Assert.That(async () => { using var reply = await execution.ConsumeResponseAsync(); })
            .Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("REDIRECT");
        await Assert.That(execution.ConnectionIdentity.Endpoint.Port).IsEqualTo(replica.Port);
        await Assert.That(execution.ConnectionIdentity.ServerClientId).IsEqualTo(41);
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [NotInParallel]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task OrdinaryScriptTelemetryNamesRedirectTarget(bool cache, bool fail)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Command });
        var activities = new List<Activity>();
        var measurements = new List<Dictionary<string, object?>>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Respire",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (activities) activities.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, capture) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.operation.duration")
                capture.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            lock (measurements) measurements.Add(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value));
        });
        meter.Start();
        await using var primary = new FakeRespServer(4)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Bytes("%1\r\n$5\r\nproto\r\n:3\r\n")
                : command.StartsWith("EVALSHA ", StringComparison.Ordinal) ? Bytes("-NOSCRIPT missing\r\n")
                : command.StartsWith("EVAL ", StringComparison.Ordinal) ? Bytes(fail ? "-ERR script failed\r\n" : ":7\r\n")
                : FakeRespServer.OkReply,
        };
        await using var replica = CorrectionReplica(primary.Port, lua: false);
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        { ClientSideCache = cache ? new RespireClientSideCacheOptions() : null });
        var script = RespireScript.Create("return 7");
        if (fail)
            await Assert.That(async () => await client.Scripts.ExecuteIntegerAsync(script, "key")).Throws<RespireServerException>();
        else
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(script, "key")).IsEqualTo(7);
        Activity[] traces;
        Dictionary<string, object?>[] durations;
        lock (activities) traces = activities.Where(activity => Equals(activity.GetTagItem("db.stored_procedure.name"), script.Sha1)).ToArray();
        lock (measurements) durations = measurements.Where(tags => Equals(tags.GetValueOrDefault("db.operation.name"), script.EvalShaOperation)).ToArray();
        await Assert.That(traces.Length).IsEqualTo(1);
        await Assert.That(traces[0].GetTagItem("server.port")).IsEqualTo(primary.Port);
        await Assert.That(traces[0].Status == ActivityStatusCode.Error).IsEqualTo(fail);
        await Assert.That(durations.Length).IsEqualTo(1);
        await Assert.That(durations[0]["server.port"]).IsEqualTo(primary.Port);
        await Assert.That(durations[0].ContainsKey("error.type")).IsEqualTo(fail);
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RedirectTelemetryPreservesStoredProcedureName(bool redirect)
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Respire",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (activities) activities.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);
        await using var primary = new FakeRespServer(":7\r\n"u8.ToArray());
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT" ? FakeRespServer.OkReply
                : redirect ? Bytes($"-REDIRECT 127.0.0.1:{primary.Port}\r\n") : Bytes(":7\r\n"),
        };
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port));
        using var reply = await client.ExecuteAsync("FCALL", "my_function", 0);
        Activity[] matches;
        lock (activities) matches = activities.Where(activity => activity.GetTagItem("db.stored_procedure.name") as string == "my_function").ToArray();
        await Assert.That(matches.Length).IsEqualTo(1);
        await Assert.That(matches[0].DisplayName.Contains("my_function", StringComparison.Ordinal)).IsTrue();
        await Assert.That(matches[0].GetTagItem("server.port")).IsEqualTo(redirect ? primary.Port : replica.Port);
    }

    [Test]
    [Arguments("0", false)]
    [Arguments("9", false)]
    [Arguments("0", true)]
    [Arguments("9", true)]
    public async Task ServerCursorRedirectIsNotReplayed(string cursor, bool retry)
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT" ? FakeRespServer.OkReply
                : Bytes($"-REDIRECT 127.0.0.1:{primary.Port}\r\n"),
        };
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        { RetryPolicy = retry ? new() : null });
        var error = await Assert.That(async () => { using var reply = await client.ExecuteAsync("SCAN", cursor); })
            .Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("REDIRECT");
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task TypedScanDoesNotRedirectItsFirstPage()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT" ? FakeRespServer.OkReply
                : Bytes($"-REDIRECT 127.0.0.1:{primary.Port}\r\n"),
        };
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port));
        await using var scan = client.Keys.ScanAsync().GetAsyncEnumerator();
        await Assert.That(async () => _ = await scan.MoveNextAsync()).Throws<RespireServerException>();
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task UnsupportedCapabilityKeepsNormalTypedReplyPath()
    {
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT"
                ? Bytes("-ERR unknown subcommand 'CAPA'\r\n") : Bytes("$5\r\nvalue\r\n"),
        };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port));
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["CLIENT CAPA REDIRECT", "GET key"]);
    }

    [Test]
    public async Task ReplicaReadPolicyUsesReadOnlyAndKeepsReadOnReplica()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command switch
            {
                "ROLE" => Bytes("*5\r\n+slave\r\n+127.0.0.1\r\n:6379\r\n+connected\r\n:0\r\n"),
                "GET key" => Bytes("$7\r\nreplica\r\n"),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = RespireClient.Create(Options(primary.Port) with
        { ReplicaEndpoints = [new("127.0.0.1", replica.Port)], ReadFrom = RespireReadFrom.Replica });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("replica");
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["CLIENT CAPA REDIRECT", "READONLY", "ROLE", "GET key"]);
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task SentinelGenerationFollowsRedirectAfterPrimaryBecomesReplica()
    {
        await using var promoted = new FakeRespServer(FakeRespServer.OkReply);
        await using var previous = new FakeRespServer
        {
            ReplyOverride = (_, command) => command switch
            {
                "ROLE" => Bytes("*3\r\n+master\r\n:0\r\n*0\r\n"),
                "FLUSHDB" => Bytes($"-REDIRECT 127.0.0.1:{promoted.Port}\r\n"),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var sentinel = new FakeRespServer(64)
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => Bytes($"*2\r\n+127.0.0.1\r\n+{previous.Port}\r\n"),
                "SENTINEL MASTER mymaster" => Bytes($"*8\r\n+ip\r\n+127.0.0.1\r\n+port\r\n+{previous.Port}\r\n+config-epoch\r\n+1\r\n+flags\r\n+master\r\n"),
                "SUBSCRIBE +switch-master +sdown +odown" => Bytes("*3\r\n+subscribe\r\n++switch-master\r\n:1\r\n*3\r\n+subscribe\r\n++sdown\r\n:2\r\n*3\r\n+subscribe\r\n++odown\r\n:3\r\n"),
                _ => Bytes("*0\r\n"),
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        { SentinelPrimaryName = "mymaster", AllowAdmin = true });
        await client.Server.FlushDatabaseAsync();
        await Assert.That(promoted.ReceivedCommands).IsEquivalentTo(["CLIENT CAPA REDIRECT", "FLUSHDB"]);
        await Assert.That(previous.ReceivedCommands.Contains("CLIENT CAPA REDIRECT")).IsTrue();
        await Assert.That(sentinel.ReceivedCommands.Contains("CLIENT CAPA REDIRECT")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RedirectLoopsAreBounded(bool retry)
    {
        var port = 0;
        await using var server = new FakeRespServer(20)
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT" ? FakeRespServer.OkReply
                : Bytes($"-REDIRECT 127.0.0.1:{port}\r\n"),
        };
        port = server.Port;
        await using var client = await RespireClient.ConnectAsync(Options(port) with
        { RetryPolicy = retry ? new() : null });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.That(async () => await client.GetStringAsync("key", timeout.Token)).Throws<RespireServerException>();
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key"))
            .IsEqualTo(Respire.Internal.ClusterRouter.RedirectLimit + 1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    [NotInParallel]
    public async Task TargetHandshakeSharesCommandDeadlineAndCallerCancellation(bool cancelCaller, bool retry)
    {
        // Compile the redirect and optional retry path before the deliberately short deadline.
        // Keep this timing assertion separate from CPU-heavy source inventory tests.
        await using (var warmTarget = new FakeRespServer(FakeRespServer.OkReply, Bytes("$1\r\nx\r\n")))
        await using (var warmReplica = new FakeRespServer(FakeRespServer.OkReply,
            Bytes($"-REDIRECT 127.0.0.1:{warmTarget.Port}\r\n")))
        await using (var warmClient = await RespireClient.ConnectAsync(Options(warmReplica.Port) with
            { CommandTimeout = TimeSpan.FromSeconds(5), RetryPolicy = retry ? new() : null }))
            await Assert.That(await warmClient.GetStringAsync("key")).IsEqualTo("x");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = new FakeRespServer
        {
            SuppressReply = _ => { entered.TrySetResult(); return true; },
        };
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT" ? FakeRespServer.OkReply
                : Bytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n"),
        };
        using var caller = new CancellationTokenSource();
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        { CommandTimeout = TimeSpan.FromMilliseconds(cancelCaller ? 5000 : 300), RetryPolicy = retry ? new() : null });
        var pending = client.GetStringAsync("key", caller.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
        {
            caller.Cancel();
            var error = await Assert.That(async () => await pending).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        }
        else
            await Assert.That(async () => await pending).Throws<RespireTimeoutException>();
        await target.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(["CLIENT CAPA REDIRECT"]);
    }

    [Test]
    [Arguments("typed-read")]
    [Arguments("raw-read")]
    [Arguments("catalog-read")]
    [Arguments("typed-write")]
    [Arguments("raw-write")]
    [Arguments("catalog-write")]
    [Arguments("fire-and-forget")]
    public async Task RetryPolicyFollowsRedirectForImmediateCommands(string shape)
    {
        await using var target = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "GET key" ? Bytes("$5\r\nvalue\r\n") : FakeRespServer.OkReply,
        };
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT" ? FakeRespServer.OkReply
                : Bytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n"),
        };
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with { RetryPolicy = new() });
        switch (shape)
        {
            case "typed-read": await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value"); break;
            case "raw-read":
                using (var reply = await client.ExecuteAsync("GET", "key"))
                    await Assert.That(reply.AsString()).IsEqualTo("value");
                break;
            case "catalog-read":
                using (var reply = await client.ExecuteAsync(RespireCommands.String.GET, ["key"]))
                    await Assert.That(reply.AsString()).IsEqualTo("value");
                break;
            case "typed-write": await client.SetAsync("key", "value"); break;
            case "raw-write":
                using (var reply = await client.ExecuteAsync("SET", "key", "value"))
                    await Assert.That(reply.AsString()).IsEqualTo("OK");
                break;
            case "catalog-write":
                using (var reply = await client.ExecuteAsync(RespireCommands.String.SET, ["key", "value"]))
                    await Assert.That(reply.AsString()).IsEqualTo("OK");
                break;
            default: await client.ExecuteFireAndForgetAsync("SET", "key", "value"); break;
        }
        await Assert.That(target.ReceivedCommands).IsEquivalentTo([
            "CLIENT CAPA REDIRECT", shape.EndsWith("read", StringComparison.Ordinal) ? "GET key" : "SET key value"]);
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetryPolicyRetriesTargetFailureAndReportsFinalEndpoint(bool fail)
    {
        var reads = 0;
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Respire",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (activities) activities.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);
        await using var target = new FakeRespServer(2)
        {
            ReplyOverride = (_, command) => command == "GET key"
                ? Interlocked.Increment(ref reads) == 1 || fail ? Bytes("-TRYAGAIN busy\r\n") : Bytes("$5\r\nvalue\r\n")
                : FakeRespServer.OkReply,
        };
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "GET key" ? Bytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n")
                : FakeRespServer.OkReply,
        };
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        { RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero } });
        if (fail)
            await Assert.That(async () => await client.GetStringAsync("key")).Throws<RespireServerException>();
        else
            await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(reads).IsEqualTo(2);
        await Assert.That(replica.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(2);
        Activity[] traces;
        lock (activities) traces = activities.Where(activity => Equals(activity.GetTagItem("db.operation.name"), "GET")).ToArray();
        await Assert.That(traces.Length).IsEqualTo(1);
        await Assert.That(traces[0].GetTagItem("server.port")).IsEqualTo(target.Port);
        await Assert.That(traces[0].Status == ActivityStatusCode.Error).IsEqualTo(fail);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetryPolicyRetainsRedirectTargetWriteEvidence(bool permitAccumulating)
    {
        await using var target = new FakeRespServer(2)
        {
            CloseConnectionAfterCommand = 2,
            ReplyOverride = (_, command) => command == "INCR counter" ? Bytes(":2\r\n") : FakeRespServer.OkReply,
        };
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT" ? FakeRespServer.OkReply
                : Bytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n"),
        };
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        {
            RetryPolicy = new()
            {
                MaxAttempts = 2, InitialBackoff = TimeSpan.Zero,
                MaxCommandRetryCategory = permitAccumulating ? RespireCommandRetryCategory.WriteAccumulating : RespireCommandRetryCategory.ReadOnly,
            },
        });
        if (permitAccumulating)
        {
            using var reply = await client.ExecuteAsync(RespireCommands.String.INCR, ["counter"]);
            await Assert.That(reply.AsInteger()).IsEqualTo(2);
        }
        else
            await Assert.That(async () => { using var reply = await client.ExecuteAsync(RespireCommands.String.INCR, ["counter"]); })
                .Throws<RespireConnectionException>();
        await Assert.That(target.ReceivedCommands.Count(command => command == "INCR counter")).IsEqualTo(permitAccumulating ? 2 : 1);
        await Assert.That(replica.ReceivedCommands.Count(command => command == "INCR counter")).IsEqualTo(permitAccumulating ? 2 : 1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RedirectedCacheReadsAreNotPublishedFromTemporaryTarget(bool retry, bool multi)
    {
        var reads = 0;
        var operation = multi ? "MGET key" : "GET key";
        await using var target = new FakeRespServer(2)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Bytes("%1\r\n$5\r\nproto\r\n:3\r\n")
                : command == operation ? Bytes((multi ? "*1\r\n" : "") + $"$1\r\n{Interlocked.Increment(ref reads)}\r\n")
                : FakeRespServer.OkReply,
        };
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Bytes("%1\r\n$5\r\nproto\r\n:3\r\n")
                : command == operation ? Bytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n") : FakeRespServer.OkReply,
        };
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        { ClientSideCache = new(), RetryPolicy = retry ? new() : null });
        for (var expected = 1; expected <= 2; expected++)
        {
            var value = multi ? (await client.Strings.GetManyAsync("key"))[0] : await client.GetStringAsync("key");
            await Assert.That(value).IsEqualTo(expected.ToString());
        }
        await Assert.That(reads).IsEqualTo(2);
        await Assert.That(target.ReceivedCommands.Any(command => command.StartsWith("CLIENT CACHING ", StringComparison.Ordinal)
            || command.StartsWith("CLIENT TRACKING ", StringComparison.Ordinal))).IsFalse();
    }

    private static RespireOptions Options(int port) => new()
    {
        Protocol = RespireProtocol.Resp2, Connections = 1, EnableReplicaRedirects = true,
        Endpoints = [new("127.0.0.1", port)],
    };

    private static FakeRespServer CorrectionReplica(int targetPort, bool lua) => new(8)
    {
        ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => Bytes("%1\r\n$5\r\nproto\r\n:3\r\n"),
            "CLIENT ID" => Bytes(":41\r\n"),
            _ when command.StartsWith("CLIENT KILL ", StringComparison.Ordinal) => Bytes(":0\r\n"),
            _ when lua && command.StartsWith("SET ", StringComparison.Ordinal) => Bytes("-ERR syntax error\r\n"),
            _ when lua && command.StartsWith("DELEX ", StringComparison.Ordinal) => Bytes("-ERR unknown command 'DELEX', unsupported\r\n"),
            _ when lua && command.StartsWith("DELIFEQ ", StringComparison.Ordinal) => Bytes("-ERR unknown command 'DELIFEQ', unsupported\r\n"),
            _ when command.StartsWith("SET ", StringComparison.Ordinal) || command.StartsWith("DELEX ", StringComparison.Ordinal)
                || command.StartsWith("EVAL", StringComparison.Ordinal) => Bytes($"-REDIRECT 127.0.0.1:{targetPort}\r\n"),
            _ => FakeRespServer.OkReply,
        },
    };

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
