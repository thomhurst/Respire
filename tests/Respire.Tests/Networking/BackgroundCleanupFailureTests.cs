using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class BackgroundCleanupFailureTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task PoolDisposalObservesEntryFailuresAfterFinalization(bool retireFirst)
        => AssertCleanupObservedAsync(failure => DisposePoolAsync(failure, retireFirst));

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task ServerTeardownObservesHandlersAfterFinalization(bool failAcceptance)
        => AssertCleanupObservedAsync(failure => DisposeServerAsync(failure, failAcceptance));

    private static async Task AssertCleanupObservedAsync(Func<Exception, Task<WeakReference[]>> cleanup)
    {
        var failure = new InvalidOperationException("Controlled cleanup failure.");
        var control = new InvalidOperationException("Unobserved positive control.");
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            foreach (var error in args.Exception.Flatten().InnerExceptions)
                if (ReferenceEquals(error, failure) || ReferenceEquals(error, control))
                    unobserved.Enqueue(error);
            // Only the deliberately abandoned control is suppressed. Real cleanup failures
            // remain unobserved, and unrelated tests' exceptions are never suppressed.
            if (args.Exception.InnerExceptions.Count == 1
                && ReferenceEquals(args.Exception.InnerException, control))
                args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var references = await cleanup(failure).WaitAsync(TimeSpan.FromSeconds(10));
            var controlReference = CreateUnobservedControl(control);
            // Run all bounded collections, including after weak targets disappear: task
            // exception holders may finalize on a later collection than their owner.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Yield();
            }

            await Assert.That(controlReference.IsAlive).IsFalse();
            foreach (var reference in references)
                await Assert.That(reference.IsAlive).IsFalse();
            await Assert.That(unobserved.Any(error => ReferenceEquals(error, control))).IsTrue();
            await Assert.That(unobserved.Any(error => ReferenceEquals(error, failure))).IsFalse();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateUnobservedControl(Exception failure)
        => new(Task.FromException(failure));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> DisposePoolAsync(Exception failure, bool retireFirst)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var pool = new DedicatedConnectionPool("127.0.0.1", server.Port,
            RespireConnectionOptions.Default, new FailingDisconnectLogger(failure));
        var first = await pool.RentAsync(CancellationToken.None);
        var second = await pool.RentAsync(CancellationToken.None);
        pool.Return(first);
        pool.Return(second);
        var retirement = retireFirst ? pool.RetireAsync().AsTask() : null;
        await pool.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        retirement ??= pool.RetireAsync().AsTask();
        var error = await Assert.That(async () => await retirement).ThrowsExactly<InvalidOperationException>();
        await Assert.That(error).IsSameReferenceAs(failure);
        await Assert.That(first.IsConnected || second.IsConnected).IsFalse();
        return [new(pool), new(retirement)];
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> DisposeServerAsync(Exception failure, bool failAcceptance)
    {
        // Leave one acceptance pending after starting two handlers. Both handlers fail
        // before teardown, so observing only the accept task cannot hide an orphan.
        await using var server = new FakeRespServer(3, FakeRespServer.PongReply);
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        server.ReplyOverride = (_, _) =>
        {
            if (Interlocked.Increment(ref count) == 2) handled.TrySetResult();
            throw failure;
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var first = new TcpClient();
        using var second = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        await second.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        await first.GetStream().WriteAsync(FakeRespServer.PingFrame, timeout.Token);
        await second.GetStream().WriteAsync(FakeRespServer.PingFrame, timeout.Token);
        await handled.Task.WaitAsync(timeout.Token);

        var acceptTask = (Task)typeof(FakeRespServer)
            .GetField("_acceptTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        if (failAcceptance)
        {
            var listener = (TcpListener)typeof(FakeRespServer)
                .GetField("_listener", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
            listener.Stop();
            // Finish the non-cancellation accept failure before DisposeAsync cancels its
            // token, deterministically exercising the exceptional exit from the loop.
            try { await acceptTask.WaitAsync(timeout.Token); }
            catch (Exception error) when (error is not OperationCanceledException) { }
        }
        await server.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        return [new(server), new(acceptTask)];
    }

    private sealed class FailingDisconnectLogger(Exception failure) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug
                && formatter(state, exception).StartsWith("Disconnected from", StringComparison.Ordinal))
                throw failure;
        }
    }
}
