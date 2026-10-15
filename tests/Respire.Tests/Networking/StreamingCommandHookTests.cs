using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public sealed class StreamingCommandHookTests
{
    [Test]
    [Arguments("download")]
    [Arguments("stream")]
    [Arguments("sequence")]
    [Arguments("gathered")]
    public async Task RedirectAttemptsAndReentrantCallsHaveIndependentBoundaries(string route)
    {
        await using var first = Server();
        await using var target = Server();
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n");
        var operation = route == "download" ? "GET" : "SET";
        first.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology
            : command.StartsWith(operation + " key", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes($"-MOVED {ClusterHash.GetSlot("key")} 127.0.0.1:{target.Port}\r\n")
                : command == "STRLEN nested" ? ":2\r\n"u8.ToArray() : null;
        target.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology
            : command == "GET key" ? "$4\r\ndata\r\n"u8.ToArray() : null;
        var hook = new Hook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(first, hook) with
        {
            UseCluster = true, ClusterTopologyRefreshInterval = null,
        });
        Task<long>? nested = null;
        hook.Before = context =>
        {
            if (context.Command == operation) nested = client.Strings.LengthAsync("nested").AsTask();
        };
        using var source = new MemoryStream(new byte[16]);
        await Execute(client, route, source);
        await Assert.That(await nested!).IsEqualTo(2L);
        await Assert.That(hook.Entered.Select(c => c.Command)).IsEquivalentTo([operation, "STRLEN"]);
        await Assert.That(hook.Completed.Count).IsEqualTo(2);
        await Assert.That(hook.Completed.All(c => c.Outcome == RespireCommandOutcome.Succeeded)).IsTrue();
        await Assert.That(first.ReceivedCommands.Count(c => c.StartsWith(operation + " key", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands.Count(c => c.StartsWith(operation + " key", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    [Arguments("download")]
    [Arguments("stream")]
    [Arguments("sequence")]
    [Arguments("gathered")]
    public async Task RejectionIsSynchronousAndLeavesTheSourceAndWireUntouched(string route)
    {
        await using var server = Server();
        var denied = new InvalidOperationException("Denied");
        var hook = new Hook { Before = _ => throw denied, ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        using var source = new MemoryStream(new byte[16]);
        var baseline = server.CommandsSeen;
        var pending = Execute(client, route, source);
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        var failure = await Failure(async () => await pending);
        await Assert.That(failure).IsSameReferenceAs(denied);
        await Assert.That(source.Position).IsEqualTo(0L);
        await Assert.That(source.CanRead).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
        await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(denied);
        await Assert.That(hook.Completed.Single().State).IsNull();
    }

    [Test]
    [Arguments("stream", false)]
    [Arguments("sequence", false)]
    [Arguments("gathered", false)]
    [Arguments("stream", true)]
    [Arguments("sequence", true)]
    [Arguments("gathered", true)]
    public async Task UploadResultIncludesConversionAndCleanupOnce(string route, bool serverError)
    {
        await using var server = Server();
        if (serverError) server.ReplyOverride = (_, command) => command.StartsWith("SET ", StringComparison.Ordinal)
            ? "-ERR failed\r\n"u8.ToArray() : null;
        var hook = new Hook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var borrowedAtCompletion = -1;
        hook.After = () => borrowedAtCompletion = client.Core.DedicatedPool.CaptureRetirementState().Borrowed;
        using var source = new MemoryStream(new byte[16]);
        Exception? error = null;
        try { await Execute(client, route, source); }
        catch (Exception failure) { error = failure; }
        var completion = hook.Completed.Single();
        await Assert.That(completion.Context.Command).IsEqualTo("SET");
        await Assert.That(completion.Outcome).IsEqualTo(serverError ? RespireCommandOutcome.Failed : RespireCommandOutcome.Succeeded);
        await Assert.That(completion.Error).IsSameReferenceAs(error);
        await Assert.That(completion.State).IsSameReferenceAs(completion.Context);
        await Assert.That(hook.Entered.Count).IsEqualTo(1);
        await Assert.That(borrowedAtCompletion).IsEqualTo(0);
        await Assert.That(source.CanRead).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SourceFailureCompletesAfterDedicatedLeaseCleanup(bool canceled)
    {
        await using var server = Server();
        var hook = new Hook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var borrowedAtCompletion = -1;
        hook.After = () => borrowedAtCompletion = client.Core.DedicatedPool.CaptureRetirementState().Borrowed;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception expected = canceled ? new OperationCanceledException(cancellation.Token) : new IOException("Source failed");
        using var source = new FailingSource(expected);
        var error = await Failure(async () => await client.Strings.SetAsync("key", source, 16));
        await Assert.That(error).IsSameReferenceAs(expected);
        await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(expected);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(canceled ? RespireCommandOutcome.Canceled : RespireCommandOutcome.Failed);
        await Assert.That(borrowedAtCompletion).IsEqualTo(0);
        await Assert.That(source.Disposed).IsFalse();
        await Assert.That(await client.Strings.SetAsync("after", "ok")).IsTrue();
    }

    [Test]
    [Arguments("download", false)]
    [Arguments("stream", false)]
    [Arguments("sequence", false)]
    [Arguments("gathered", false)]
    [Arguments("download", true)]
    [Arguments("stream", true)]
    [Arguments("sequence", true)]
    [Arguments("gathered", true)]
    public async Task CancellationBeforeAndAfterDispatchCompletesOnce(string route, bool dispatched)
    {
        await using var server = Server();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = command =>
        {
            if (!command.StartsWith("GET ", StringComparison.Ordinal) && !command.StartsWith("SET ", StringComparison.Ordinal)) return false;
            received.TrySetResult();
            return true;
        };
        var hook = new Hook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        using var source = new MemoryStream(new byte[16]);
        using var cancellation = new CancellationTokenSource();
        var baseline = server.CommandsSeen;
        if (!dispatched) cancellation.Cancel();
        var pending = Execute(client, route, source, cancellation.Token);
        if (dispatched)
        {
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }
        var error = await Failure(async () => await pending);
        await Assert.That(error is OperationCanceledException).IsTrue();
        await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(error);
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
        await Assert.That(hook.Entered.Single().CancellationToken).IsEqualTo(cancellation.Token);
        if (!dispatched) await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
    }

    [Test]
    [Arguments("payload")]
    [Arguments("null")]
    [Arguments("error")]
    [Arguments("later-failure")]
    public async Task DownloadCompletesAtStreamTransferAndKeepsPayloadOwnership(string reply)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command == "GET key" ? reply switch
        {
            "null" => "$-1\r\n"u8.ToArray(),
            "error" => "-ERR failed\r\n"u8.ToArray(),
            "later-failure" => "$100\r\npart"u8.ToArray(),
            _ => "$4\r\ndata\r\n"u8.ToArray(),
        } : null;
        var hook = new Hook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        Stream? stream = null;
        Exception? error = null;
        try { stream = await client.Strings.GetStreamAsync("key"); }
        catch (Exception failure) { error = failure; }
        await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(reply == "error" ? RespireCommandOutcome.Failed : RespireCommandOutcome.Succeeded);
        await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(error);
        if (stream is not null)
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var bytes = new byte[4];
            await stream.ReadExactlyAsync(bytes, guard.Token);
            if (reply == "later-failure")
            {
                server.CloseConnections();
                await Assert.That(async () => await stream.ReadAsync(bytes, guard.Token)).Throws<Exception>();
            }
            else await Assert.That(await stream.ReadAsync(bytes, guard.Token)).IsEqualTo(0);
            await stream.DisposeAsync();
            await stream.DisposeAsync();
        }
        await Assert.That(hook.Completed.Count).IsEqualTo(1);
    }

    [Test]
    [ParallelLimiter<TimingSensitive>]
    public async Task GatheredCancellationDoesNotCompleteTheHookWhileTheSocketBorrowsMemory()
    {
        var reads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply) { ReadGate = reads.Task };
        var hook = new Hook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with { CommandTimeout = null });
        await server.ConnectionAccepted;
        var connection = client.Core.Multiplexer.GetConnection();
        var socket = (Socket)typeof(RespireConnection).GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
        socket.SendBufferSize = 0;
        var payload = new byte[5 * 1024 * 1024];
        payload.AsSpan().Fill((byte)'a');
        hook.After = () => payload.AsSpan().Fill((byte)'b');
        using var cancellation = new CancellationTokenSource();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = client.SetAsync("key", payload, cancellationToken: cancellation.Token).AsTask();
        try
        {
            while (connection.CaptureTimeoutDiagnostics().InflightBytes == 0 || socket.Poll(0, SelectMode.SelectWrite))
            {
                if (pending.IsCompleted) await pending;
                await Task.Delay(1, guard.Token);
            }
            cancellation.Cancel();
            // Observe response cancellation independently of the write barrier.
            while (!connection.InspectForTests().Inflight.TryPeek(out var response)
                || !PendingResponse.IsCompleted(response.State) || response.InspectForTests().ReferenceCount != 1)
                await Task.Delay(1, guard.Token);
            await Assert.That(pending.IsCompleted).IsFalse();
            await Assert.That(hook.Completed.Count).IsEqualTo(0);
            reads.TrySetResult();
            await Assert.That(async () => await pending.WaitAsync(guard.Token)).Throws<OperationCanceledException>();
            await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
            // A following command is a FIFO fence for the peer having read the complete upload.
            hook.After = null;
            await client.SetAsync("after", "ok", cancellationToken: guard.Token);
            await Assert.That(server.ReceivedArguments[0][2].All(value => value == (byte)'a')).IsTrue();
        }
        finally { reads.TrySetResult(); }
    }

    private static async Task Execute(RespireClient client, string route, Stream source, CancellationToken token = default)
    {
        switch (route)
        {
            case "download": using (await client.Strings.GetStreamAsync("key", token)) { } break;
            case "stream": await client.Strings.SetAsync("key", source, 16, cancellationToken: token); break;
            case "sequence": await client.Strings.SetAsync("key", new ReadOnlySequence<byte>(new byte[16]), cancellationToken: token); break;
            case "gathered": await client.SetAsync("key", new byte[16 * 1024], cancellationToken: token); break;
            default: throw new ArgumentOutOfRangeException(nameof(route));
        }
    }

    private static FakeRespServer Server() => new(64, FakeRespServer.OkReply);
    private static RespireOptions Options(FakeRespServer server, RespireCommandHook hook) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespireProtocol.Resp2,
        Connections = 1, ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(5), CommandHook = hook,
    };
    private static async Task<Exception> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure");
    }
    private sealed class FailingSource(Exception error) : MemoryStream
    {
        public override bool CanSeek => false;
        internal bool Disposed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(error);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class Hook : RespireCommandHook
    {
        internal Action<RespireCommandContext>? Before;
        internal Action? After;
        internal bool ThrowOnCompleted;
        internal readonly ConcurrentQueue<RespireCommandContext> Entered = new();
        internal readonly ConcurrentQueue<(RespireCommandContext Context, object? State, RespireCommandOutcome Outcome, Exception? Error)> Completed = new();
        public override object? OnExecuting(RespireCommandContext context)
        {
            Entered.Enqueue(context);
            Before?.Invoke(context);
            return context;
        }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
            Completed.Enqueue((context, state, outcome, error));
            After?.Invoke();
            if (ThrowOnCompleted) throw new InvalidOperationException("Observer failure");
        }
    }
}
