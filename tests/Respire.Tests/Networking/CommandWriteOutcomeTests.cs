using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CommandWriteOutcomeTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Test]
    public async Task DefaultEvidenceFailsClosed()
        => await Assert.That(default(CommandAttemptResult).WriteOutcome).IsEqualTo(CommandWriteOutcome.Unknown);

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ServerErrorsKeepCheckedAndRawReplyContracts(bool throwOnError, bool streaming)
    {
        await using var server = new FakeRespServer("-ERR injected rejection\r\n"u8.ToArray());
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        await using var payload = new MemoryStream("value"u8.ToArray());
        var attempt = streaming
            ? await connection.SendAttemptAsync(new StreamedSetCommand("key", payload, 5, default, SetWhen.Always),
                commandName: "SET", throwOnError: throwOnError)
            : await connection.SendAttemptAsync(new RawCommand(FakeRespServer.PingFrame),
                commandName: "PING", throwOnError: throwOnError);
        await Assert.That(attempt.WriteOutcome).IsEqualTo(CommandWriteOutcome.EffectMayHaveReachedServer);
        if (throwOnError)
        {
            var error = await Assert.That(() => attempt.GetResult()).ThrowsExactly<RespireServerException>();
            await Assert.That(error!.CommandName).IsEqualTo(streaming ? "SET" : "PING");
            await Assert.That(error.Code).IsEqualTo("ERR");
        }
        else
        {
            using var response = attempt.GetResult();
            await Assert.That(response.IsError).IsTrue();
            await Assert.That(response.AsSpan().SequenceEqual("ERR injected rejection"u8)).IsTrue();
        }
        // The rejected attempt must release its slot without changing the next reply owner.
        server.ReplyOverride = (_, _) => FakeRespServer.PongReply;
        var next = await connection.SendAttemptAsync(new RawCommand(FakeRespServer.PingFrame), commandName: "PING");
        using var nextReply = next.GetResult();
        await Assert.That(nextReply.AsString()).IsEqualTo("PONG");
        await Assert.That(connection.InspectForTests().Inflight.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AskingUploadHasOneOutcomeIncludingItsPrelude(bool cancelPrelude)
    {
        var askingReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        { SuppressReply = command =>
            {
                if (command == "ASKING") askingReceived.TrySetResult();
                return cancelPrelude && command == "ASKING";
            } };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        using var caller = new CancellationTokenSource();
        await using var source = new MemoryStream("data"u8.ToArray());
        var command = new StreamedSetCommand((RespireValue)"key", source, 4, default, SetWhen.Always);
        var asking = new RawCommand("*1\r\n$6\r\nASKING\r\n"u8.ToArray());
        var pending = connection.SendAskingStreamAttemptAsync(asking, command, caller.Token,
            CommandDeadline.None, default).AsTask();
        await askingReceived.Task.WaitAsync(Guard);
        if (cancelPrelude) caller.Cancel();
        var attempt = await pending.WaitAsync(Guard);
        await Assert.That(attempt.WriteOutcome).IsEqualTo(CommandWriteOutcome.EffectMayHaveReachedServer);
        if (cancelPrelude)
        {
            var error = await Assert.That(() => attempt.GetResult()).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
            await Assert.That(server.ReceivedCommands).IsEquivalentTo(["ASKING"]);
            await Assert.That(connection.IsConnected).IsFalse();
        }
        else
        {
            using var response = attempt.GetResult();
            await Assert.That(response.AsString()).IsEqualTo("OK");
            await Assert.That(server.ReceivedCommands).IsEquivalentTo(["ASKING", "SET key data"]);
        }
    }

    [Test]
    public async Task BytePositionWrapCannotGiveAnOlderWrittenFrameAnUnwrittenOutcome()
    {
        var fourReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fiveReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = 0;
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        { SuppressReply = _ =>
            {
                var count = ++commands;
                if (count == 4) fourReceived.TrySetResult();
                if (count == 5) fiveReceived.TrySetResult();
                return true;
            } };
        GatedTransport? transport = null;
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { TestingStreamFactory = async (host, port, token) =>
                transport = await GatedTransport.ConnectAsync(host, port, 0, token) });
        var first = connection.SendAttemptAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await transport!.WriteEntered.Task.WaitAsync(Guard);
        SetProducerPosition(connection, long.MaxValue - 28);
        var beforeWrap = connection.SendAttemptAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        var atMaximum = connection.SendAttemptAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        var wrapping = connection.SendAttemptAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        transport.ReleaseWrite.TrySetResult();
        await fourReceived.Task.WaitAsync(Guard);
        // Advance past the negative half of a full counter cycle without sending exabytes. Real
        // writes on both sides still prove that the earlier positive frame reached the peer.
        SetProducerPosition(connection, 100);
        var afterWrap = connection.SendAttemptAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await fiveReceived.Task.WaitAsync(Guard);
        await connection.DisposeAsync().AsTask().WaitAsync(Guard);
        foreach (var pending in new[] { first, beforeWrap, atMaximum, afterWrap })
        {
            var attempt = await pending.WaitAsync(Guard);
            await Assert.That(attempt.WriteOutcome).IsEqualTo(CommandWriteOutcome.EffectMayHaveReachedServer);
            await Assert.That(() => attempt.GetResult()).Throws<RespireConnectionException>();
        }
        var invalidRange = await wrapping.WaitAsync(Guard);
        await Assert.That(invalidRange.WriteOutcome).IsEqualTo(CommandWriteOutcome.Unknown);
        await Assert.That(() => invalidRange.GetResult()).Throws<RespireConnectionException>();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(5);
    }

    private static void SetProducerPosition(RespireConnection connection, long position)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var gate = typeof(RespireConnection).GetField("_writeGate", flags)!.GetValue(connection)!;
        var enter = gate.GetType().GetMethod("Enter")!;
        var exit = gate.GetType().GetMethod("Exit")!;
        enter.Invoke(gate, null);
        try
        {
            var progress = typeof(RespireConnection).GetField("_producerProgress", flags)!.GetValue(connection)!;
            progress.GetType().GetField("EnqueuedBytes", flags)!.SetValue(progress, position);
        }
        finally { exit.Invoke(gate, null); }
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(65_537)]
    public async Task SerializationFailureIsUnwrittenAndKeepsTheOriginalException(int bound)
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var original = new InvalidOperationException("Injected serialization failure.");
        var attempt = await connection.SendAttemptAsync(new FailingCommand(bound, original));
        await Assert.That(attempt.WriteOutcome).IsEqualTo(CommandWriteOutcome.DefinitelyUnwritten);
        var failure = await Assert.That(() => attempt.GetResult()).ThrowsExactly<InvalidOperationException>();
        await Assert.That(ReferenceEquals(failure, original)).IsTrue();
        using var next = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        await Assert.That(next.AsString()).IsEqualTo("PONG");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["PING"]);
        await Assert.That(connection.InspectForTests().Inflight.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClosedOrRetiredConnectionRejectsBeforeAdmission(bool retire)
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        if (retire) await connection.RetireAsync();
        else await connection.DisposeAsync();
        var serialized = false;
        var attempt = await connection.SendAttemptAsync(new CallbackCommand(() => serialized = true));
        // Small frames retain their existing off-gate serialization before the authoritative
        // closed check. Retirement is checked earlier; neither path admits or writes the frame.
        await Assert.That(serialized).IsEqualTo(!retire);
        await Assert.That(attempt.WriteOutcome).IsEqualTo(CommandWriteOutcome.DefinitelyUnwritten);
        if (retire) await Assert.That(() => attempt.GetResult()).ThrowsExactly<RespireConnectionRetiredException>();
        else await Assert.That(() => attempt.GetResult()).Throws<RespireConnectionException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments(0, 1)]
    [Arguments(7, 1)]
    [Arguments(14, 1)]
    [Arguments(0, 3)]
    [Arguments(7, 3)]
    [Arguments(14, 3)]
    public async Task SelectedAndUnselectedFramesShareFailureButHaveDifferentOutcomes(int prefixLength, int queuedReplies)
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply) { SuppressReply = _ => true };
        GatedTransport? transport = null;
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { TestingStreamFactory = async (host, port, token) =>
                transport = await GatedTransport.ConnectAsync(host, port, prefixLength, token) });
        var selected = connection.SendAttemptAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await transport!.WriteEntered.Task.WaitAsync(Guard);
        // The actual transport can have accepted a prefix, although the connection's successful
        // write counter is still zero. A second frame stays in the unselected active buffer.
        var queuedFrame = new byte[FakeRespServer.PingFrame.Length * queuedReplies];
        for (var i = 0; i < queuedReplies; i++) FakeRespServer.PingFrame.CopyTo(queuedFrame, i * FakeRespServer.PingFrame.Length);
        var queued = connection.SendAttemptAsync(new RawCommand(queuedFrame), repliesBeforeFinal: queuedReplies - 1).AsTask();
        await Assert.That(connection.InspectForTests().Inflight.Count).IsEqualTo(1 + queuedReplies);
        await Assert.That(connection.CaptureTimeoutDiagnostics().PendingWriteBytes).IsEqualTo((long)FakeRespServer.PingFrame.Length * (1 + queuedReplies));
        await Assert.That(transport.BytesWritten).IsEqualTo(prefixLength);
        await connection.DisposeAsync().AsTask().WaitAsync(Guard);
        var first = await selected.WaitAsync(Guard);
        var second = await queued.WaitAsync(Guard);
        await Assert.That(first.WriteOutcome).IsEqualTo(CommandWriteOutcome.EffectMayHaveReachedServer);
        await Assert.That(second.WriteOutcome).IsEqualTo(CommandWriteOutcome.DefinitelyUnwritten);
        var firstError = await Assert.That(() => first.GetResult()).Throws<RespireConnectionException>();
        var secondError = await Assert.That(() => second.GetResult()).Throws<RespireConnectionException>();
        await Assert.That(ReferenceEquals(firstError, secondError)).IsTrue();
        await Assert.That(transport.WriteCalls).IsEqualTo(1);
    }

    [Test]
    public async Task QueuedCancellationRemainsAmbiguousAndItsLateReplyPreservesFifo()
    {
        await using var server = new FakeRespServer(":11\r\n"u8.ToArray(), ":22\r\n"u8.ToArray(), ":33\r\n"u8.ToArray());
        GatedTransport? transport = null;
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { TestingStreamFactory = async (host, port, token) =>
                transport = await GatedTransport.ConnectAsync(host, port, 0, token) });
        var first = connection.SendAttemptAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await transport!.WriteEntered.Task.WaitAsync(Guard);
        using var caller = new CancellationTokenSource();
        var second = connection.SendAttemptAsync(new RawCommand(FakeRespServer.PingFrame), caller.Token).AsTask();
        await Assert.That(connection.InspectForTests().Inflight.Count).IsEqualTo(2);
        caller.Cancel();
        var cancelled = await second.WaitAsync(Guard);
        await Assert.That(cancelled.WriteOutcome).IsEqualTo(CommandWriteOutcome.EffectMayHaveReachedServer);
        var error = await Assert.That(() => cancelled.GetResult()).ThrowsExactly<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(transport.BytesWritten).IsEqualTo(0);
        transport.ReleaseWrite.TrySetResult();
        using var firstReply = (await first.WaitAsync(Guard)).GetResult();
        await Assert.That(firstReply.AsInteger()).IsEqualTo(11);
        var third = await connection.SendAttemptAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask().WaitAsync(Guard);
        using var thirdReply = third.GetResult();
        await Assert.That(thirdReply.AsInteger()).IsEqualTo(33);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["PING", "PING", "PING"]);
        await Assert.That(cancelled.WriteOutcome).IsEqualTo(CommandWriteOutcome.EffectMayHaveReachedServer);
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("deadline")]
    [Arguments("dispose")]
    public async Task CapacityFailureIsUnwrittenWithoutSerializing(string failureKind)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        { SuppressReply = _ => { received.TrySetResult(); return true; } };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { MaxInflightCommands = 1, CommandTimeout = TimeSpan.FromSeconds(1) });
        var occupying = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), armCommandDeadline: false).AsTask();
        await received.Task.WaitAsync(Guard);
        using var caller = new CancellationTokenSource();
        var serialized = false;
        var pending = connection.SendAttemptAsync(new CallbackCommand(() => serialized = true), caller.Token,
            commandName: "PING").AsTask();
        await Assert.That(pending.IsCompleted).IsFalse();
        if (failureKind == "cancel") caller.Cancel();
        else if (failureKind == "dispose") await connection.DisposeAsync().AsTask().WaitAsync(Guard);
        var attempt = await pending.WaitAsync(Guard);
        await Assert.That(attempt.WriteOutcome).IsEqualTo(CommandWriteOutcome.DefinitelyUnwritten);
        // Teardown can free ring capacity before waking this waiter. The existing small-frame
        // serializer may then run, but the dead check still rejects its admission under the gate.
        if (failureKind != "dispose") await Assert.That(serialized).IsFalse();
        if (failureKind == "cancel")
        {
            var error = await Assert.That(() => attempt.GetResult()).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        }
        else if (failureKind == "deadline")
        {
            var error = await Assert.That(() => attempt.GetResult()).ThrowsExactly<RespireTimeoutException>();
            await Assert.That(error!.CommandName).IsEqualTo("PING");
            await Assert.That(error.Diagnostics.Stage).IsEqualTo(RespireCommandStage.WaitingForCapacity);
        }
        else await Assert.That(() => attempt.GetResult()).Throws<RespireConnectionException>();
        await connection.DisposeAsync().AsTask().WaitAsync(Guard);
        await Assert.That(async () => await occupying.WaitAsync(Guard)).Throws<RespireConnectionException>();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["PING"]);
    }

    [Test]
    public async Task FrozenOutcomeSurvivesReuseOfTheExactSource()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        { SuppressReply = _ => { received.TrySetResult(); return true; } };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { CompletionSourcePoolSize = 1 });
        using var caller = new CancellationTokenSource();
        var pending = connection.SendAttemptAsync(new RawCommand(FakeRespServer.PingFrame), caller.Token,
            commandName: "first").AsTask();
        await received.Task.WaitAsync(Guard);
        var identity = CaptureSourceIdentity(connection);
        caller.Cancel();
        var first = await pending.WaitAsync(Guard);
        // The string barrier uses a different pool. Its ordered reply proves the cancelled
        // raw source has returned without returning another raw source that could replace it.
        var barrier = connection.SendStringAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        using var guard = new CancellationTokenSource(Guard);
        while (server.CommandsSeen != 2) await Task.Delay(1, guard.Token);
        await server.SendRawAsync("+PONG\r\n+PONG\r\n"u8.ToArray());
        var barrierReply = await barrier.WaitAsync(guard.Token);
        await Assert.That(barrierReply).IsEqualTo("PONG");
        var reused = false;
        var original = new InvalidOperationException("Failure on the next incarnation.");
        var second = await connection.SendAttemptAsync(new FailingCommand(0, original, () =>
            reused = identity.Source.TryGetTarget(out var current) && current.CommandName == "second"
                && current.State != identity.State), commandName: "second");
        await Assert.That(reused).IsTrue();
        await Assert.That(second.WriteOutcome).IsEqualTo(CommandWriteOutcome.DefinitelyUnwritten);
        await Assert.That(first.WriteOutcome).IsEqualTo(CommandWriteOutcome.EffectMayHaveReachedServer);
        var firstError = await Assert.That(() => first.GetResult()).ThrowsExactly<OperationCanceledException>();
        var secondError = await Assert.That(() => second.GetResult()).ThrowsExactly<InvalidOperationException>();
        await Assert.That(firstError!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(ReferenceEquals(secondError, original)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FullyWrittenFrameWithLostReplyRemainsAmbiguous(bool tls)
    {
        using var certificate = TestTlsCertificate.Create();
        using var deadline = new CancellationTokenSource(Guard);
        var listener = FakeRespServer.StartListener();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync(deadline.Token);
            await using var network = new NetworkStream(socket, ownsSocket: false);
            await using var secure = tls ? new SslStream(network, leaveInnerStreamOpen: true) : null;
            if (secure is not null)
                await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, deadline.Token);
            Stream transport = secure is not null ? secure : network;
            var frame = new byte[FakeRespServer.PingFrame.Length];
            await transport.ReadExactlyAsync(frame, deadline.Token);
            received.TrySetResult(frame);
            await close.Task.WaitAsync(deadline.Token);
        });
        try
        {
            await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", port,
                new() { UseTls = tls, TlsOptions = tls ? new SslClientAuthenticationOptions
                { TargetHost = "localhost", RemoteCertificateValidationCallback = (_, _, _, _) => true } : null });
            var pending = connection.SendAttemptAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
            var frame = await received.Task.WaitAsync(deadline.Token);
            await Assert.That(frame.AsSpan().SequenceEqual(FakeRespServer.PingFrame)).IsTrue();
            close.TrySetResult();
            var attempt = await pending.WaitAsync(deadline.Token);
            await Assert.That(attempt.WriteOutcome).IsEqualTo(CommandWriteOutcome.EffectMayHaveReachedServer);
            await Assert.That(() => attempt.GetResult()).Throws<RespireConnectionException>();
            await peer.WaitAsync(deadline.Token);
        }
        finally
        {
            close.TrySetResult();
            deadline.Cancel();
            listener.Stop();
            try { await peer; } catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StreamSourceFailureIsClassifiedFromTheFrameLifetime(bool afterHeader)
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var original = new IOException("Injected source failure.");
        await using var source = new FailingPayload(afterHeader, original);
        var command = new StreamedSetCommand((RespireValue)"key", source, RespireConnection.StreamChunkSize + 1,
            default, SetWhen.Always);
        var attempt = await connection.SendAttemptAsync(command, commandName: "SET").AsTask().WaitAsync(Guard);
        await Assert.That(attempt.WriteOutcome).IsEqualTo(afterHeader
            ? CommandWriteOutcome.EffectMayHaveReachedServer : CommandWriteOutcome.DefinitelyUnwritten);
        var error = await Assert.That(() => attempt.GetResult()).ThrowsExactly<IOException>();
        await Assert.That(ReferenceEquals(error, original)).IsTrue();
        await Assert.That(connection.IsConnected).IsEqualTo(!afterHeader);
        if (!afterHeader)
        {
            using var next = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
            await Assert.That(next.AsString()).IsEqualTo("PONG");
            await Assert.That(server.ReceivedCommands).IsEquivalentTo(["PING"]);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PartialSocketFrameNeverAuthorizesResend(bool knownBound)
    {
        using var deadline = new CancellationTokenSource(Guard);
        var listener = FakeRespServer.StartListener();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync(deadline.Token);
            socket.ReceiveBufferSize = 16 * 1024;
            await using var network = new NetworkStream(socket, ownsSocket: false);
            var prefix = new byte[4096];
            await network.ReadExactlyAsync(prefix, deadline.Token);
            received.TrySetResult(prefix);
            // No more reads: the bounded socket buffers cannot accept the remaining 5 MiB.
            await close.Task.WaitAsync(deadline.Token);
        });
        try
        {
            await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", port,
                new() { SocketSendBufferSize = 16 * 1024 });
            // Zero disables the Windows socket provider's additional send buffering. The options
            // value zero deliberately means "keep the OS default", so set this owned socket directly.
            var sendingSocket = (Socket)typeof(RespireConnection)
                .GetField("_socket", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection)!;
            sendingSocket.SendBufferSize = 0;
            var pending = connection.SendAttemptAsync(new LargeSetCommand(new byte[5 * 1024 * 1024], knownBound)).AsTask();
            var prefix = await received.Task.WaitAsync(deadline.Token);
            await Assert.That(prefix.AsSpan().StartsWith("*3\r\n$3\r\nSET\r\n"u8)).IsTrue();
            await Assert.That(pending.IsCompleted).IsFalse();
            await Assert.That(connection.CaptureTimeoutDiagnostics().PendingWriteBytes > 0).IsTrue();
            close.TrySetResult();
            var attempt = await pending.WaitAsync(deadline.Token);
            await Assert.That(attempt.WriteOutcome).IsEqualTo(CommandWriteOutcome.EffectMayHaveReachedServer);
            await Assert.That(() => attempt.GetResult()).Throws<RespireConnectionException>();
            await peer.WaitAsync(deadline.Token);
        }
        finally
        {
            close.TrySetResult();
            deadline.Cancel();
            listener.Stop();
            try { await peer; } catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }

    [Test]
    public async Task UnwrittenStreamCancellationDoesNotReturnMemoryOwnedByALateRead()
    {
        var pool = new ReadOwnershipPool();
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { StreamPayloadPool = pool });
        using var caller = new CancellationTokenSource();
        await using var source = new LatePayload();
        var command = new StreamedSetCommand((RespireValue)"key", source, 4, default, SetWhen.Always);
        var pending = connection.SendAttemptAsync(command, caller.Token, commandName: "SET").AsTask();
        try
        {
            await source.ReadEntered.Task.WaitAsync(Guard);
            caller.Cancel();
            var attempt = await pending.WaitAsync(Guard);
            await Assert.That(attempt.WriteOutcome).IsEqualTo(CommandWriteOutcome.DefinitelyUnwritten);
            var error = await Assert.That(() => attempt.GetResult()).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
            await Assert.That(pool.Returned.Task.IsCompleted).IsFalse();
            await Assert.That(server.CommandsSeen).IsEqualTo(0);
            using var next = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
            await Assert.That(next.AsString()).IsEqualTo("PONG");
        }
        finally { source.ReleaseRead.TrySetResult(); }
        await pool.Returned.Task.WaitAsync(Guard);
        await Assert.That(pool.ReturnCount).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["PING"]);
    }

    [Test]
    public async Task QueuedDeadlineDoesNotProveThatTheFrameWillRemainUnwritten()
    {
        await using var server = new FakeRespServer(":11\r\n"u8.ToArray(), ":22\r\n"u8.ToArray(), ":33\r\n"u8.ToArray());
        GatedTransport? transport = null;
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new() { CommandTimeout = TimeSpan.FromSeconds(1), TestingStreamFactory = async (host, port, token) =>
                transport = await GatedTransport.ConnectAsync(host, port, 0, token) });
        var first = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), armCommandDeadline: false).AsTask();
        await transport!.WriteEntered.Task.WaitAsync(Guard);
        var pending = connection.SendAttemptAsync(new RawCommand(FakeRespServer.PingFrame), commandName: "PING").AsTask();
        var expired = await pending.WaitAsync(Guard);
        await Assert.That(expired.WriteOutcome).IsEqualTo(CommandWriteOutcome.EffectMayHaveReachedServer);
        var error = await Assert.That(() => expired.GetResult()).ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Buffered);
        await Assert.That(transport.BytesWritten).IsEqualTo(0);
        transport.ReleaseWrite.TrySetResult();
        using var firstReply = await first.WaitAsync(Guard);
        await Assert.That(firstReply.AsInteger()).IsEqualTo(11);
        using var third = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask().WaitAsync(Guard);
        await Assert.That(third.AsInteger()).IsEqualTo(33);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["PING", "PING", "PING"]);
    }

    [Test]
    public async Task MultiReplyFrameKeepsItsWholeUnitOutcomeAndDrainsCancelledReplies()
    {
        var serverCommands = 0;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        { SuppressReply = _ => { if (serverCommands++ == 2) received.TrySetResult(); return true; } };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        using var caller = new CancellationTokenSource();
        var frame = new byte[FakeRespServer.PingFrame.Length * 3];
        for (var i = 0; i < 3; i++) FakeRespServer.PingFrame.CopyTo(frame, i * FakeRespServer.PingFrame.Length);
        var pending = connection.SendAttemptAsync(new RawCommand(frame), caller.Token,
            repliesBeforeFinal: 2, firstQueueReply: 0).AsTask();
        await received.Task.WaitAsync(Guard);
        caller.Cancel();
        var attempt = await pending.WaitAsync(Guard);
        await Assert.That(attempt.WriteOutcome).IsEqualTo(CommandWriteOutcome.EffectMayHaveReachedServer);
        await Assert.That(() => attempt.GetResult()).ThrowsExactly<OperationCanceledException>();
        server.SuppressReply = null;
        await server.SendRawAsync("+PONG\r\n+PONG\r\n+PONG\r\n"u8.ToArray());
        var next = await connection.SendAttemptAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask().WaitAsync(Guard);
        using var reply = next.GetResult();
        await Assert.That(reply.AsString()).IsEqualTo("PONG");
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(4);

    }

    private static (WeakReference<PendingResponse> Source, long State) CaptureSourceIdentity(RespireConnection connection)
    {
        if (!connection.InspectForTests().Inflight.TryPeek(out var source))
            throw new InvalidOperationException("The controlled reply must still own its source.");
        return (new WeakReference<PendingResponse>(source), source.State);
    }

    private readonly struct FailingCommand(int bound, Exception failure, Action? beforeFailure = null) : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public int GetWriteSizeHint() => bound == -1 ? throw failure : bound;
        public void Write(ref RespWriter writer)
        {
            writer.WriteRaw("*1\r\n$4\r\nPI"u8);
            beforeFailure?.Invoke();
            throw failure;
        }
    }

    private readonly struct CallbackCommand(Action serialized) : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void Write(ref RespWriter writer) { serialized(); writer.WriteRaw(FakeRespServer.PingFrame); }
    }

    private readonly struct LargeSetCommand(byte[] payload, bool knownBound) : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public int GetWriteSizeHint() => knownBound ? payload.Length + 64 : 0;
        public void Write(ref RespWriter writer)
        {
            writer.WriteArrayHeader(3);
            writer.WriteBulkString("SET"u8);
            writer.WriteBulkString("partial"u8);
            writer.WriteBulkString(payload);
        }
    }

    private sealed class LatePayload : MemoryStream
    {
        internal TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadEntered.TrySetResult();
            await ReleaseRead.Task;
            buffer.Span.Fill(7);
            return buffer.Length;
        }
    }

    private sealed class ReadOwnershipPool : ArrayPool<byte>
    {
        private int _returnCount;
        internal int ReturnCount => Volatile.Read(ref _returnCount);
        internal TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override byte[] Rent(int minimumLength) => new byte[minimumLength];
        public override void Return(byte[] array, bool clearArray = false)
        {
            Interlocked.Increment(ref _returnCount);
            Returned.TrySetResult();
        }
    }

    private sealed class FailingPayload(bool afterHeader, Exception failure) : MemoryStream
    {
        private bool _first = true;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!afterHeader || !_first) throw failure;
            _first = false;
            buffer.Span.Clear();
            return ValueTask.FromResult(buffer.Length);
        }
    }

    private sealed class GatedTransport(Socket socket, int prefixLength) : NetworkStream(socket, ownsSocket: true)
    {
        private int _disposed;
        internal int WriteCalls { get; private set; }
        internal int BytesWritten { get; private set; }
        internal TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal static async Task<GatedTransport> ConnectAsync(string host, int port, int prefixLength, CancellationToken token)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(host, port, token); return new(socket, prefixLength); }
            catch { socket.Dispose(); throw; }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++WriteCalls == 1)
            {
                if (prefixLength != 0)
                {
                    await base.WriteAsync(buffer[..prefixLength], cancellationToken);
                    BytesWritten += prefixLength;
                    buffer = buffer[prefixLength..];
                }
                WriteEntered.TrySetResult();
                await ReleaseWrite.Task.WaitAsync(cancellationToken);
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            }
            await base.WriteAsync(buffer, cancellationToken);
            BytesWritten += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
            Volatile.Write(ref _disposed, 1);
            ReleaseWrite.TrySetResult();
            base.Dispose(disposing);
        }
    }
}
