using System.Reflection;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CommandAdmissionTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    private sealed class AdmissionState
    {
        public Exception? Failure;
        public int Validated;
        public int Accepted;
        public CancellationToken ResponseToken;
    }

    private readonly struct AdmissionCommand(AdmissionState state, int sizeHint = 0) : IRespCommand
    {
        public int GetWriteSizeHint() => sizeHint;
        public AdmissionCommand WithSizeHint(int bound) => new(state, bound);
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void Write(ref RespWriter writer) => writer.WriteRaw(FakeRespServer.PingFrame);
        public void OnAccepted() => state.Accepted++;
        public void ValidateAdmission()
        {
            state.Validated++;
            if (state.Failure is { } failure) throw failure;
        }
        public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken) => state.ResponseToken;
    }

    [Test]
    public async Task RetainedCommandsRequireExplicitAdmissionPolicy()
    {
        var wrappers = typeof(RespireClient).Assembly.GetTypes().Where(type =>
            type.IsValueType && typeof(IRespCommand).IsAssignableFrom(type) &&
            type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Any(field => typeof(IRespCommand).IsAssignableFrom(field.FieldType) ||
                    field.FieldType.IsGenericParameter && field.FieldType.GetGenericParameterConstraints()
                        .Any(constraint => typeof(IRespCommand).IsAssignableFrom(constraint)))).ToArray();
        await Assert.That(wrappers.Length).IsGreaterThanOrEqualTo(3);
        foreach (var wrapper in wrappers)
        {
            await Assert.That(typeof(IRespCommandWrapper).IsAssignableFrom(wrapper)).IsTrue();
            await Assert.That(wrapper.GetMethod(nameof(IRespCommand.GetMutationFence),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .IsNotNull();
        }
    }

    [Test]
    [Arguments(false, "raw")]
    [Arguments(true, "raw")]
    [Arguments(false, "timestamp")]
    [Arguments(true, "timestamp")]
    [Arguments(false, "prefix")]
    [Arguments(true, "prefix")]
    [Arguments(false, "validated-prefix")]
    [Arguments(true, "validated-prefix")]
    public async Task RejectedAdmissionPublishesNeitherBytesNorResponseSlots(bool direct, string wrapper)
    {
        await using var server = new FakeRespServer("+unexpected\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command switch
            {
                "ECHO first" => "+first\r\n"u8.ToArray(),
                "ECHO second" => "+second\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var connection = await ConnectAsync(server);
        var failure = new InvalidOperationException("Admission rejected after serialization.");
        var state = new AdmissionState { Failure = failure };
        var execution = new RespireClient.TrackedScriptExecution(connection, default);
        var error = await Assert.That(async () =>
            { using var reply = await StartSend(connection, new AdmissionCommand(state), execution, direct, wrapper); })
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(ReferenceEquals(error, failure)).IsTrue();
        await Assert.That(state.Validated).IsEqualTo(1);
        await Assert.That(state.Accepted).IsEqualTo(0);
        await Assert.That(execution.StartedTimestamp).IsEqualTo(0L);

        // Filling both response slots proves rejection releases capacity. Distinct frames/replies
        // prove FIFO has no orphaned slot and rejected bytes (including ASKING) never reach Redis.
        using var next = await connection.SendPrefixedCheckedAsync(
            new RawCommand("*2\r\n$4\r\nECHO\r\n$5\r\nfirst\r\n"u8.ToArray()),
            new RawCommand("*2\r\n$4\r\nECHO\r\n$6\r\nsecond\r\n"u8.ToArray()))
            .AsTask().WaitAsync(Limit);
        await Assert.That(next.AsString()).IsEqualTo("second");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["ECHO first", "ECHO second"]);
    }

    [Test]
    [Arguments("timestamp", false, false)]
    [Arguments("prefix", false, false)]
    [Arguments("timestamp", true, false)]
    [Arguments("prefix", true, false)]
    [Arguments("validated-prefix", false, false)]
    [Arguments("validated-prefix", true, false)]
    [Arguments("validated-prefix", false, true)]
    [Arguments("validated-prefix", true, true)]
    public async Task AcceptedWrapperUsesResponseToken(string wrapper, bool cancelCaller, bool waitForCapacity)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capacityFilled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "PING") return false;
                var count = Interlocked.Increment(ref received);
                if (waitForCapacity && count == 2) capacityFilled.TrySetResult();
                if (count == (waitForCapacity ? 3 : 1)) arrived.TrySetResult();
                return true;
            },
        };
        await using var connection = await ConnectAsync(server);
        using var admission = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var state = new AdmissionState { ResponseToken = caller.Token };
        var execution = new RespireClient.TrackedScriptExecution(connection, default);
        var first = waitForCapacity ? connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask() : null;
        var second = waitForCapacity ? connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask() : null;
        if (waitForCapacity) await capacityFilled.Task.WaitAsync(Limit);
        var pending = StartSend(connection, new AdmissionCommand(state), execution, false, wrapper, admission.Token).AsTask();
        if (waitForCapacity)
        {
            await Assert.That(pending.IsCompleted).IsFalse();
            await server.SendRawAsync("+PONG\r\n+PONG\r\n"u8.ToArray());
            using var firstReply = await first!.WaitAsync(Limit);
            using var secondReply = await second!.WaitAsync(Limit);
        }
        await arrived.Task.WaitAsync(Limit);
        admission.Cancel();
        await Assert.That(pending.IsCompleted).IsFalse();
        if (cancelCaller)
        {
            caller.Cancel();
            var error = await Assert.That(async () => { using var reply = await pending.WaitAsync(Limit); })
                .Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
            await server.SendRawAsync(FakeRespServer.PongReply);
        }
        else
        {
            await server.SendRawAsync(FakeRespServer.PongReply);
            using var reply = await pending.WaitAsync(Limit);
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        }
        await Assert.That(state.Accepted).IsEqualTo(1);
        if (wrapper == "timestamp") await Assert.That(execution.StartedTimestamp).IsGreaterThan(0L);
    }

    [Test, NotInParallel]
    public async Task TimestampPolicyForwardingAllocatesNothing()
    {
        // No socket is used: the execution record only stores timestamps in this measurement.
        var execution = new RespireClient.TrackedScriptExecution(null!, default);
        var command = new RespireClient.SendTimestampCommand<AdmissionCommand>(new(new AdmissionState()), execution);
        for (var i = 0; i < 20; i++) { Measure(command, false); Measure(command, true); }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Bytes: Measure(command, false), Control: Measure(command, true)));
        await Assert.That(measured.Bytes).IsEqualTo(0L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000L);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure<TCommand>(TCommand command, bool control) where TCommand : struct, IRespCommand
    {
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            command.ValidateAdmission();
            command.GetResponseCancellationToken(default);
            command.OnAccepted();
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static ValueTask<RespValue> StartSend(RespireConnection connection, AdmissionCommand command,
        RespireClient.TrackedScriptExecution execution, bool direct, string wrapper, CancellationToken token = default)
    {
        // An oversized complete upper bound selects direct admission without changing wire bytes.
        command = command.WithSizeHint(direct ? 65_537 : FakeRespServer.PingFrame.Length);
        return wrapper switch
        {
            "timestamp" => connection.SendAsync(new RespireClient.SendTimestampCommand<AdmissionCommand>(command, execution), token),
            "prefix" => connection.SendPrefixedCheckedAsync(new RawCommand("*1\r\n$6\r\nASKING\r\n"u8.ToArray()), command, token),
            "validated-prefix" => connection.SendValidatedPrefixedAsync(new RawCommand("*1\r\n$6\r\nASKING\r\n"u8.ToArray()), command, token),
            _ => connection.SendAsync(command, token),
        };
    }

    private static Task<RespireConnection> ConnectAsync(FakeRespServer server)
        => RespireConnection.ConnectAsync("127.0.0.1", server.Port, new RespireConnectionOptions
        {
            Protocol = RespProtocol.Resp2,
            CommandTimeout = null,
            MaxInflightCommands = 2, // ASKING plus the application command consume two slots.
        });
}
