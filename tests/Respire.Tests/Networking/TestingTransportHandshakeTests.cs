using System.Net.Sockets;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class TestingTransportHandshakeTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LateFactoryStreamCleanupAwaitsAsynchronousDisposal(bool disposalFails)
    {
        using var caller = new CancellationTokenSource();
        using var stream = new GatedDisposalStream(disposalFails);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new RespireConnectionOptions
        {
            ConnectTimeout = TimeSpan.FromMinutes(1),
            TestingStreamFactory = async (_, _, _) =>
            {
                entered.TrySetResult();
                await releaseFactory.Task;
                return stream;
            },
        };
        var pending = RespireConnection.ConnectAsync("testing", 6379, options, cancellationToken: caller.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            releaseFactory.TrySetResult();
            var first = await Task.WhenAny(stream.DisposalStarted.Task, pending).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(ReferenceEquals(first, stream.DisposalStarted.Task)).IsTrue();
            await Assert.That(pending.IsCompleted).IsFalse();
            stream.ReleaseDisposal.TrySetResult();
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
            await Assert.That(stream.CanRead).IsFalse();
        }
        finally
        {
            releaseFactory.TrySetResult();
            stream.ReleaseDisposal.TrySetResult();
            try { await using var connection = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class GatedDisposalStream(bool disposalFails) : MemoryStream
    {
        internal TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask DisposeAsync()
        {
            DisposalStarted.TrySetResult();
            await ReleaseDisposal.Task;
            await base.DisposeAsync();
            if (disposalFails) throw new IOException("Asynchronous stream cleanup failed.");
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task FactoryCancellationPreservesOriginAndDisposesLateStream(bool callerCancellation, bool returnsAfterCancellation)
    {
        using var caller = new CancellationTokenSource();
        using var stream = new MemoryStream();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new RespireConnectionOptions
        {
            ConnectTimeout = callerCancellation ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(200),
            TestingStreamFactory = async (_, _, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (returnsAfterCancellation) { }
                return stream;
            },
        };
        var pending = RespireConnection.ConnectAsync("testing", 6379, options, cancellationToken: caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancellation) caller.Cancel();
        var error = await Assert.That(async () =>
        {
            // Also close a wrongly accepted connection, so a failed regression leaks no work.
            await using var connection = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken == caller.Token).IsEqualTo(callerCancellation);
        await Assert.That(caller.IsCancellationRequested).IsEqualTo(callerCancellation);
        if (returnsAfterCancellation) await Assert.That(stream.CanRead).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task SocketAndTestingStreamHandshakeShareCancellationTimeoutAndCleanup(bool testingStream, bool callerCancellation)
        => await AssertHandshakeCleanupAsync(testingStream, callerCancellation, disposalFails: false);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HandshakeFailureSurvivesAsynchronousDisposalFailure(bool callerCancellation)
        => await AssertHandshakeCleanupAsync(testingStream: true, callerCancellation, disposalFails: true);

    private static async Task AssertHandshakeCleanupAsync(bool testingStream, bool callerCancellation, bool disposalFails)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        using var caller = new CancellationTokenSource();
        var options = new RespireConnectionOptions
        {
            Database = 1,
            CommandTimeout = callerCancellation ? null : TimeSpan.FromMilliseconds(200),
            TestingStreamFactory = testingStream ? (host, port, token) => OpenStream(host, port, token, disposalFails) : null,
        };
        var pending = RespireConnection.ConnectAsync("127.0.0.1", server.Port, options, cancellationToken: caller.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (callerCancellation) caller.Cancel();
        if (callerCancellation)
        {
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        }
        else
        {
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<RespireTimeoutException>();
        }
        await server.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async ValueTask<Stream> OpenStream(string host, int port, CancellationToken token, bool disposalFails)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(host, port, token);
            return disposalFails ? new FaultingDisposalNetworkStream(socket) : new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class FaultingDisposalNetworkStream(Socket socket) : NetworkStream(socket, ownsSocket: true)
    {
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            throw new IOException("Asynchronous stream cleanup failed.");
        }
    }
}
