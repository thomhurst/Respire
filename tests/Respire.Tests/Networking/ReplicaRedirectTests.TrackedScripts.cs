using System.Diagnostics;
using System.Diagnostics.Metrics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class ReplicaRedirectTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TrackedScriptsCanRedirectAgainDuringEvalFallback(bool cache)
    {
        await using var final = TrackedTarget();
        await using var intermediate = TrackedTarget();
        var normal = intermediate.ReplyOverride!;
        intermediate.ReplyOverride = (id, command) => command.StartsWith("EVAL ")
            ? Bytes($"-REDIRECT 127.0.0.1:{final.Port}\r\n") : normal(id, command);
        await using var source = CorrectionReplica(intermediate.Port, lua: false);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port) with
        { ClientSideCache = cache ? new() : null });
        await client.EnsureReliableCorrectionOrderingAsync();
        var pools = client.Core.OwnedPools.Count;
        var execution = await client.StartTrackedScriptExecutionAsync(RespireScript.Create("return 1"), ["key"], [], default,
            requireReliableCorrectionOrdering: true);
        using var reply = await execution.ConsumeResponseAsync();
        await Assert.That(reply.AsInteger()).IsEqualTo(1);
        await Assert.That(execution.ConnectionIdentity.Endpoint.Port).IsEqualTo(final.Port);
        await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools);
        await Assert.That(final.ReceivedCommands.Count(command => command.StartsWith("EVALSHA "))).IsEqualTo(0);
        await Assert.That(final.ReceivedCommands.Count(command => command.StartsWith("EVAL "))).IsEqualTo(1);
    }

    [Test]
    [Arguments("cancel", false)]
    [Arguments("cancel", true)]
    [Arguments("timeout", false)]
    [Arguments("timeout", true)]
    [Arguments("lost-reply", false)]
    [Arguments("lost-reply", true)]
    public async Task TrackedUncertainScriptFencesTargetBeforeCorrection(string failure, bool cache)
    {
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fencing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = TrackedTarget();
        target.SuppressReply = command =>
        {
            if (!command.StartsWith("EVAL ")) return false;
            submitted.TrySetResult();
            return true;
        };
        await using var source = CorrectionReplica(target.Port, lua: false);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port) with
        {
            ClientSideCache = cache ? new RespireClientSideCacheOptions() : null,
            CommandTimeout = failure == "timeout" ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10),
        });
        await client.EnsureReliableCorrectionOrderingAsync();
        var pools = client.Core.OwnedPools.Count;
        using var caller = new CancellationTokenSource();
        var execution = await client.StartTrackedScriptExecutionAsync(RespireScript.Create("return 1"),
            ["key"], [], caller.Token, requireReliableCorrectionOrdering: true);
        var corrected = false;
        var response = client.ExecuteWithCorrectionAsync(execution, RespireClient.CorrectionOrdering.FenceFirst,
            state: 0, correct: (_, identity) =>
            {
                if (identity.Endpoint.Port != target.Port) throw new InvalidOperationException("Wrong correction target");
                corrected = true;
                return default;
            }).AsTask();
        try
        {
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            target.GateCommand("CLIENT KILL ID 99", releaseFence.Task, () => fencing.TrySetResult());
            if (failure == "cancel") caller.Cancel();
            if (failure == "lost-reply") target.CloseConnection(0);
            await fencing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(response.IsCompleted).IsFalse();
            await Assert.That(corrected).IsFalse();
            // The target pool remains owned while its ordering barrier is unacknowledged.
            await Assert.That(client.Core.OwnedPools.Count > pools).IsTrue();
        }
        finally { releaseFence.TrySetResult(); }
        if (failure == "cancel")
        {
            var error = await Assert.That(async () => { using var reply = await response; }).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        }
        else if (failure == "timeout")
            await Assert.That(async () => { using var reply = await response; }).Throws<RespireTimeoutException>();
        else
            await Assert.That(async () => { using var reply = await response; }).Throws<RespireConnectionException>();
        await Assert.That(corrected).IsTrue();
        await Assert.That(source.ReceivedCommands.Contains("CLIENT KILL ID 41")).IsFalse();
        await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools);
        await Assert.That(execution.Connection.IsConnected).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TrackedTargetIdentityFailureDoesNotSubmitOrCorrect(bool denyKill)
    {
        await using var target = TrackedTarget();
        var normal = target.ReplyOverride!;
        target.ReplyOverride = (id, command) => command == (denyKill ? "CLIENT KILL ID 99 SKIPME yes" : "CLIENT ID")
            ? Bytes("-NOPERM target identity denied\r\n") : normal(id, command);
        await using var source = CorrectionReplica(target.Port, lua: false);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port));
        await client.EnsureReliableCorrectionOrderingAsync();
        var pools = client.Core.OwnedPools.Count;
        var execution = await client.StartTrackedScriptExecutionAsync(RespireScript.Create("return 1"),
            ["key"], [], default, requireReliableCorrectionOrdering: true);
        var corrected = false;
        await Assert.That(async () =>
        {
            using var reply = await client.ExecuteWithCorrectionAsync(execution, RespireClient.CorrectionOrdering.FenceFirst,
                state: 0, correct: (_, _) => { corrected = true; return default; });
        }).Throws<RespireServerException>();
        await Assert.That(corrected).IsFalse();
        await Assert.That(target.ReceivedCommands.Any(command => command.StartsWith("EVAL"))).IsFalse();
        await Assert.That(client.Core.OwnedPools.Count).IsEqualTo(pools);
    }

    [Test]
    public async Task TrackedReplicaRedirectsRemainOptIn()
    {
        await using var target = TrackedTarget();
        await using var source = CorrectionReplica(target.Port, lua: false);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port) with { EnableReplicaRedirects = false });
        await client.EnsureReliableCorrectionOrderingAsync();
        var execution = await client.StartTrackedScriptExecutionAsync(RespireScript.Create("return 1"), ["key"], [], default);
        var error = await Assert.That(async () => { using var reply = await execution.ConsumeResponseAsync(); })
            .Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("REDIRECT");
        await Assert.That(target.CommandsSeen).IsEqualTo(0);
    }

    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TrackedScriptTelemetryNamesFinalTarget(bool fail)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Command });
        var activities = new List<Activity>();
        var durations = new List<Dictionary<string, object?>>();
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
            lock (durations) durations.Add(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value));
        });
        meter.Start();
        await using var target = TrackedTarget();
        var normal = target.ReplyOverride!;
        target.ReplyOverride = (id, command) => fail && command.StartsWith("EVAL ")
            ? Bytes("-ERR final script failed\r\n") : normal(id, command);
        await using var source = CorrectionReplica(target.Port, lua: false);
        await using var client = await RespireClient.ConnectAsync(Options(source.Port));
        await client.EnsureReliableCorrectionOrderingAsync();
        var script = RespireScript.Create("return 1");
        var execution = await client.StartTrackedScriptExecutionAsync(script, ["key"], [], default,
            requireReliableCorrectionOrdering: true);
        if (fail)
            await Assert.That(async () => { using var reply = await execution.ConsumeResponseAsync(); }).Throws<RespireServerException>();
        else { using var reply = await execution.ConsumeResponseAsync(); }
        Activity trace;
        Dictionary<string, object?> duration;
        lock (activities) trace = activities.Single(activity => Equals(activity.GetTagItem("db.stored_procedure.name"), script.Sha1));
        lock (durations) duration = durations.Single(tags => Equals(tags.GetValueOrDefault("db.operation.name"), script.EvalShaOperation));
        await Assert.That(trace.GetTagItem("server.port")).IsEqualTo(target.Port);
        await Assert.That(trace.Status == ActivityStatusCode.Error).IsEqualTo(fail);
        await Assert.That(duration["server.port"]).IsEqualTo(target.Port);
    }

    private static FakeRespServer TrackedTarget() => new(16)
    {
        ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => Bytes("%1\r\n$5\r\nproto\r\n:3\r\n"),
            "CLIENT ID" => Bytes(":99\r\n"),
            _ when command.StartsWith("CLIENT KILL ") => Bytes(":0\r\n"),
            _ when command.StartsWith("EVALSHA ") => Bytes("-NOSCRIPT missing\r\n"),
            _ when command.StartsWith("EVAL ") => Bytes(":1\r\n"),
            _ => FakeRespServer.OkReply,
        },
    };
}
