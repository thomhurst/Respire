using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Respire.Internal;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public partial class StreamWorkerTests
{
    [Test]
    public async Task DeadLetterWithoutDestinationLogsFailureAndContinuesProcessing()
    {
        var state = new State
        {
            Handle = (entry, _) => ValueTask.FromResult(entry.GetString("payload") == "0"
                ? RespireStreamWorkerResult.DeadLetter : RespireStreamWorkerResult.Ack),
        };
        await using var fixture = await Fixture.CreateAsync(state: state);
        await fixture.AddAsync(0);
        await fixture.AddAsync(1);
        await fixture.StartAsync();
        await UntilAsync(() => Task.FromResult(state.ScopeIds.Count == 2 && state.WarningCount == 1));
        await fixture.StopAsync();
        await Assert.That(fixture.Service.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
        await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(1);
        await Assert.That(state.Warnings.Single().Fields.Single(pair => pair.Key == "ExceptionType").Value)
            .IsEqualTo(typeof(InvalidOperationException).FullName);
    }

    [Test]
    public async Task OversizedDeliveryFailsBeforeHandlerAndLeavesSourcePending()
    {
        await using var fixture = await Fixture.CreateAsync(options: new() { DeadLetterStream = "dlq" },
            keyPrefix: "{worker}tenant:");
        var fields = Enumerable.Range(0, 1025).Select(index => ($"field-{index}", (RespireValue)"body")).ToArray();
        await fixture.View.Streams.AddAsync("events", fields);
        await fixture.StartAsync();
        var error = await Assert.That(async () => await fixture.Service.ExecuteTask!.WaitAsync(Deadline))
            .Throws<InvalidOperationException>();
        await Assert.That(error!.Message).Contains("at most 1024 field/value pairs");
        await Assert.That(fixture.State.ScopeIds.Count).IsEqualTo(0);
        await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(1);
        await Assert.That(await fixture.View.Streams.CountAsync("dlq")).IsEqualTo(0);
    }

    [Test]
    [Arguments("explicit", 1)]
    [Arguments("nack", 3)]
    [Arguments("throw", 3)]
    [Arguments("invalid", 3)]
    [Arguments("deserialize", 3)]
    [Arguments("ack", 1)]
    public async Task DeliveryPolicyUsesRealAttemptsAndPreservesPayload(string outcome, int limit)
    {
        var state = new State
        {
            Handle = (_, _) => outcome switch
            {
                "explicit" => ValueTask.FromResult(RespireStreamWorkerResult.DeadLetter),
                "throw" => throw new InvalidOperationException("private payload"),
                "invalid" => ValueTask.FromResult((RespireStreamWorkerResult)42),
                "ack" => ValueTask.FromResult(RespireStreamWorkerResult.Ack),
                _ => ValueTask.FromResult(RespireStreamWorkerResult.Nack),
            },
        };
        // A tagged prefix makes logical keys share a slot, and must be applied exactly once.
        await using var fixture = await Fixture.CreateAsync(state: state, options: new()
        {
            DeadLetterStream = "dlq", DeliveryLimit = limit,
            MinimumIdleTime = TimeSpan.FromMilliseconds(20), RecoveryPollInterval = TimeSpan.FromMilliseconds(10),
        }, deserializeFailure: outcome == "deserialize", keyPrefix: "{worker}tenant:");
        await fixture.AddAsync(0);
        // .NET 10 starts ExecuteAsync in the background; pending queries need a ready group.
        await fixture.View.Streams.CreateGroupAsync("events", "workers", RespireStreamId.Beginning);
        await fixture.StartAsync();
        await UntilAsync(async () => (await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count == 0
            && (outcome == "ack" ? state.ScopeIds.Count == 1 : await fixture.View.Streams.CountAsync("dlq") == 1));
        await fixture.StopAsync();
        await Assert.That(await fixture.Client.Streams.CountAsync("dlq")).IsEqualTo(0);
        if (outcome == "ack")
        {
            await Assert.That(await fixture.View.Streams.CountAsync("dlq")).IsEqualTo(0);
            return;
        }
        var deadLetter = (await fixture.View.Streams.ReadAsync("dlq")).Single();
        await Assert.That(deadLetter.GetString("payload")).IsEqualTo("0");
        await Assert.That(deadLetter.GetString("_respire.attempt")).IsEqualTo(limit.ToString());
        await Assert.That(deadLetter.GetString("_respire.reason")).IsEqualTo(outcome switch
        {
            "explicit" => "explicit", "nack" => "nack", _ => "processing-failed",
        });
        await Assert.That(state.DisposedScopes).IsEqualTo(limit);
        await Assert.That(deadLetter.Fields.Any(field => Encoding.UTF8.GetString(field.Value).Contains("private payload")))
            .IsFalse();
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task InvalidDeliveryLimitFailsRegistration(int limit)
        => await Assert.That(() => new ServiceCollection().AddRespireStreamWorker<EntryHandler>("events", "g",
            new() { DeadLetterStream = "dlq", DeliveryLimit = limit })).Throws<ArgumentOutOfRangeException>();

    [Test]
    public async Task DeliveryLimitRequiresDestination()
        => await Assert.That(() => new ServiceCollection().AddRespireStreamWorker<EntryHandler>("events", "g",
            new() { DeliveryLimit = 1 })).Throws<ArgumentException>();

    [Test]
    public async Task StartupReplayCountsTowardLimitWithoutAnotherHandlerAttempt()
    {
        await using var fixture = await Fixture.CreateAsync(options: new()
        {
            ConsumerName = "stable", DeadLetterStream = "dlq", DeliveryLimit = 1,
        }, keyPrefix: "{worker}tenant:");
        await fixture.AddAsync(0);
        await fixture.View.Streams.CreateGroupAsync("events", "workers", RespireStreamId.Beginning);
        await fixture.View.Streams.ReadGroupOnceAsync("events", "workers", "stable-0");
        await fixture.StartAsync();
        await UntilAsync(async () => await fixture.View.Streams.CountAsync("dlq") == 1);
        await fixture.StopAsync();
        await Assert.That(fixture.State.ScopeIds.Count).IsEqualTo(0);
        var entry = (await fixture.View.Streams.ReadAsync("dlq")).Single();
        await Assert.That(entry.GetString("_respire.attempt")).IsEqualTo("2");
        await Assert.That(entry.GetString("_respire.reason")).IsEqualTo("delivery-limit");
    }

    [Test]
    [Arguments("events")]
    [Arguments("{other}dlq")]
    public async Task InvalidResolvedDeadLetterKeysFailBeforeCreatingGroup(string destination)
    {
        await using var fixture = await Fixture.CreateAsync(options: new() { DeadLetterStream = destination });
        await Assert.That(async () =>
        {
            await fixture.StartAsync();
            await fixture.Service.ExecuteTask!.WaitAsync(Deadline);
        }).Throws<ArgumentException>();
        await Assert.That(await fixture.View.Streams.CountAsync("events")).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LostDeadLetterReplyDoesNotRepeatCompletion(bool afterExecution)
    {
        var state = new State { Handle = (_, _) => ValueTask.FromResult(RespireStreamWorkerResult.DeadLetter) };
        await using var fixture = await Fixture.CreateAsync(state: state,
            options: new() { DeadLetterStream = "dlq" }, keyPrefix: "{worker}tenant:");
        using var fault = fixture.Server.InjectFault("EVAL", RespireFakeFault.Disconnect(afterExecution),
            firstArgument: Encoding.UTF8.GetBytes(StreamWorkerScripts.DeadLetterSource));
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        await Assert.That(async () => await fixture.Service.ExecuteTask!.WaitAsync(Deadline)).Throws<Exception>();
        await Assert.That(fault.ExecutionCount).IsEqualTo(afterExecution ? 1L : 0L);
        await using var root = await RespireClient.ConnectAsync(fixture.Server.CreateOptions());
        var control = root.WithKeyPrefix("{worker}tenant:");
        await Assert.That((await control.Streams.PendingSummaryAsync("events", "workers")).Count)
            .IsEqualTo(afterExecution ? 0L : 1L);
        await Assert.That(await control.Streams.CountAsync("dlq")).IsEqualTo(afterExecution ? 1L : 0L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeadLetterCancellationPreservesUncommittedDelivery(bool afterExecution)
    {
        var state = new State { Handle = (_, _) => ValueTask.FromResult(RespireStreamWorkerResult.DeadLetter) };
        await using var fixture = await Fixture.CreateAsync(state: state,
            options: new() { DeadLetterStream = "dlq" }, keyPrefix: "{worker}tenant:");
        var gate = new RespireFakeGate();
        using var fault = fixture.Server.InjectFault("EVAL", RespireFakeFault.Pause(gate, afterExecution),
            firstArgument: Encoding.UTF8.GetBytes(StreamWorkerScripts.DeadLetterSource));
        try
        {
            await fixture.AddAsync(0);
            await fixture.StartAsync();
            await fault.Matched.WaitAsync(Deadline);
            await fixture.Service.StopAsync(new CancellationToken(canceled: true)).WaitAsync(Deadline);
            await fixture.Service.ExecuteTask!.WaitAsync(Deadline);
            await using var root = await RespireClient.ConnectAsync(fixture.Server.CreateOptions());
            var control = root.WithKeyPrefix("{worker}tenant:");
            await Assert.That((await control.Streams.PendingSummaryAsync("events", "workers")).Count)
                .IsEqualTo(afterExecution ? 0L : 1L);
            await Assert.That(await control.Streams.CountAsync("dlq")).IsEqualTo(afterExecution ? 1L : 0L);
        }
        finally { gate.Release(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectedDeadLetterWritesFaultWorkerAndKeepPending(bool loading)
    {
        var state = new State { Handle = (_, _) => ValueTask.FromResult(RespireStreamWorkerResult.DeadLetter) };
        await using var fixture = await Fixture.CreateAsync(state: state,
            options: new() { DeadLetterStream = "dlq" }, keyPrefix: "{worker}tenant:");
        using var fault = fixture.Server.InjectFault("EVAL", loading ? RespireFakeFault.Loading() : RespireFakeFault.ReadOnly(),
            firstArgument: Encoding.UTF8.GetBytes(StreamWorkerScripts.DeadLetterSource));
        await fixture.AddAsync(0);
        await fixture.StartAsync();
        await Assert.That(async () => await fixture.Service.ExecuteTask!.WaitAsync(Deadline)).Throws<Exception>();
        await Assert.That(fault.ExecutionCount).IsEqualTo(0L);
        await Assert.That((await fixture.View.Streams.PendingSummaryAsync("events", "workers")).Count).IsEqualTo(1);
        await Assert.That(await fixture.View.Streams.CountAsync("dlq")).IsEqualTo(0);
    }
}
