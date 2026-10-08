using System.Reflection;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class DeadlinePublicationTests
{
    /// <summary>The caller's exact, possibly relaxed deadline must be visible when its ring slot is published.</summary>
    [Test]
    [Arguments(0, false)]
    [Arguments(0, true)]
    [Arguments(1, false)]
    [Arguments(1, true)]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task EffectiveDeadlineIsPublishedBeforeAcceptance(int path, bool direct)
    {
        await using var server = new FakeRespServer("$4\r\npong\r\n"u8.ToArray());
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { CommandTimeout = TimeSpan.FromMinutes(5) });
        var expected = CommandDeadline.At(Environment.TickCount64 + 60_000).Relax(1234);
        var observed = CommandDeadline.None;
        var published = false;
        var command = new AcceptanceCommand(() =>
        {
            published = connection.InspectForTests().Inflight.TryPeek(out var source);
            if (published) observed = source.Deadline;
        });
        await SendAsync(connection, command, expected, path, direct);
        await Assert.That(published).IsTrue();
        await Assert.That(observed.RawValue).IsEqualTo(expected.RawValue);
    }

    /// <summary>Admission after a full-ring wait publishes the original deadline and retains FIFO replies.</summary>
    [Test]
    [Arguments(0, false)]
    [Arguments(0, true)]
    [Arguments(1, false)]
    [Arguments(1, true)]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task CapacityWaitPublishesOriginalDeadline(int path, bool direct)
    {
        var firstSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer("$4\r\npong\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command != "PING") return false;
                firstSeen.TrySetResult();
                return true;
            },
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { CommandTimeout = TimeSpan.FromMinutes(5), MaxInflightCommands = 1 });
        var first = connection.SendAsync(new AcceptanceCommand(static () => { }), armCommandDeadline: false).AsTask();
        await firstSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var expected = CommandDeadline.At(Environment.TickCount64 + 60_000).Relax(1234);
        var accepted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AcceptanceCommand(() =>
        {
            if (connection.InspectForTests().Inflight.TryPeek(out var source))
                accepted.SetResult(source.Deadline.RawValue);
        });
        var second = SendAsync(connection, command, expected, path, direct);
        await Assert.That(second.IsCompleted).IsFalse();
        await Assert.That(accepted.Task.IsCompleted).IsFalse();
        await server.SendRawAsync("$4\r\npong\r\n"u8.ToArray());
        using var firstReply = await first.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(firstReply.AsString()).IsEqualTo("pong");
        var observed = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await server.SendRawAsync("$4\r\npong\r\n"u8.ToArray());
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(observed).IsEqualTo(expected.RawValue);
        await Assert.That(connection.InspectForTests().Inflight.Count).IsEqualTo(0);
    }

    /// <summary>Diagnostic ages have millisecond precision, preserve tick zero, and handle ordinary signed wrap.</summary>
    [Test]
    [Arguments(-1L, 100L, -1L)]
    [Arguments(0L, 100L, 100L)]
    [Arguments(50L, 51L, 1L)]
    [Arguments(50L, 50L, 0L)]
    [Arguments(51L, 50L, 0L)]
    [Arguments(long.MaxValue - 5, long.MinValue + 4, 10L)]
    public async Task DiagnosticAgeUsesMonotonicMilliseconds(long timestamp, long now, long expectedMilliseconds)
    {
        var elapsed = (TimeSpan?)typeof(RespireConnection)
            .GetMethod("GetDiagnosticElapsed", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [timestamp, now]);
        if (expectedMilliseconds < 0) await Assert.That(elapsed).IsNull();
        else await Assert.That(elapsed).IsEqualTo((TimeSpan?)TimeSpan.FromMilliseconds(expectedMilliseconds));
    }

    [Test]
    [Arguments("FlushProgress", "LastWriteTimestamp")]
    [Arguments("ReceiveProgress", "LastReadTimestamp")]
    public async Task DiagnosticAgeIsUnknownBeforeFirstObservation(string holderName, string timestampName)
    {
        var holderType = typeof(RespireConnection).GetNestedType(holderName, BindingFlags.NonPublic)!;
        var holder = Activator.CreateInstance(holderType, nonPublic: true)!;
        var timestamp = (long)holderType.GetField(timestampName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(holder)!;
        var elapsed = (TimeSpan?)typeof(RespireConnection)
            .GetMethod("GetDiagnosticElapsed", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [timestamp, Environment.TickCount64]);
        await Assert.That(elapsed).IsNull();
    }

    private static async Task SendAsync(RespireConnection connection, AcceptanceCommand command,
        CommandDeadline deadline, int path, bool direct)
    {
        command = command.WithSizeHint(direct ? 65_537 : FakeRespServer.PingFrame.Length);
        var response = path switch
        {
            0 => ConsumeAsync(connection.SendCheckedAsync(in command, commandDeadline: deadline)),
            1 => connection.SendStringAsync(in command, commandDeadline: deadline).AsTask(),
            2 => connection.SendBytesAsync(in command, commandDeadline: deadline).AsTask(),
            _ => connection.SendConvertedAsync<AcceptanceCommand, int, int>(in command, 0,
                static (int state, in RespValue reply) => state + reply.AsSpan().Length,
                transferOwnership: false, commandDeadline: deadline).AsTask(),
        };
        await response.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task ConsumeAsync(ValueTask<RespValue> response)
    {
        using var reply = await response;
    }

    private readonly struct AcceptanceCommand(Action accepted, int sizeHint = 0) : IRespCommand
    {
        public int GetWriteSizeHint() => sizeHint;
        public AcceptanceCommand WithSizeHint(int bound) => new(accepted, bound);
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void Write(ref RespWriter writer) => writer.WriteRaw(FakeRespServer.PingFrame);
        public void OnAccepted() => accepted();
    }
}
