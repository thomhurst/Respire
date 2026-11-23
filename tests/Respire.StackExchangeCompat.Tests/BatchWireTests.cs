using System.Runtime.CompilerServices;
using Respire.Tests.Networking;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

public class BatchWireTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReusedBatchDispatchesInOrderAndDrainsOnGracefulClose(bool failFirst)
    {
        var firstReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new CountingPipelineHook();
        await using var server = new FakeRespServer(2, ":1\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => failFirst && command == "HSET key field first"
                ? "-WRONGTYPE first write failed\r\n"u8.ToArray() : null,
        };
        server.GateCommand("HSET key field first", firstReply.Task, () => firstReceived.SetResult());

        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespireProtocol.Resp2,
            Connections = 2,
            PipelineHook = hook,
        });
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var batch = connection.GetDatabase().CreateBatch();
        var first = batch.HashSetAsync("key", "field", "first");
        batch.Execute();
        Task<bool> second;
        Task close;

        try
        {
            await firstReceived.Task.WaitAsync(s_timeout);
            second = batch.HashSetAsync("key", "field", "second");
            batch.Execute();
            batch.Execute();

            // Admission runs inline when a native batch starts. The first reply is still gated.
            Assert.Equal(1, hook.Executions);
            close = connection.CloseAsync();
            Assert.False(close.IsCompleted);
        }
        finally
        {
            firstReply.TrySetResult();
        }

        if (failFirst)
        {
            Assert.Equal("WRONGTYPE", (await Assert.ThrowsAsync<RespireServerException>(
                () => first.WaitAsync(s_timeout))).Code);
        }
        else
        {
            Assert.True(await first.WaitAsync(s_timeout));
        }

        Assert.True(await second.WaitAsync(s_timeout));
        await close.WaitAsync(s_timeout);
        Assert.Equal(2, hook.Executions);
        Assert.Equal(new[] { "HSET key field first", "HSET key field second" },
            server.ReceivedCommands.Where(command => command.StartsWith("HSET ", StringComparison.Ordinal)));
    }

    [Test]
    [NotInParallel]
    public async Task CanceledBatchScanObservesLatePageError()
    {
        const string PageError = "abandoned batch scan page";
        int unobservedPages = 0;
        int unobservedControls = 0;
        var controlError = new InvalidOperationException("unobserved positive control");

        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            foreach (var error in args.Exception.Flatten().InnerExceptions)
            {
                if (error is RespireServerException serverError
                    && serverError.Message.Contains(PageError, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref unobservedPages);
                    args.SetObserved();
                }

                if (ReferenceEquals(error, controlError))
                {
                    Interlocked.Increment(ref unobservedControls);
                    args.SetObserved();
                }
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;

        try
        {
            await CancelScanBeforeErrorReplyAsync(PageError);
            CreateUnobservedFault(controlError);

            // The page fault and the positive control can only publish from the finalizer.
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.Equal(1, Volatile.Read(ref unobservedControls));
            Assert.Equal(0, Volatile.Read(ref unobservedPages));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateUnobservedFault(Exception error)
    {
        _ = Task.FromException(error);
    }

    private static async Task CancelScanBeforeErrorReplyAsync(string message)
    {
        var scanReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(":0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("ZSCAN ", StringComparison.Ordinal)
                ? System.Text.Encoding.UTF8.GetBytes($"-WRONGTYPE {message}\r\n") : null,
        };
        server.GateCommand("ZSCAN ", scanReply.Task, () => scanReceived.SetResult());
        await using var connection = RespireConnectionMultiplexer.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespireProtocol.Resp2,
        });
        using var cancellation = new CancellationTokenSource();
        var batch = connection.GetDatabase().CreateBatch();

        try
        {
            await using var scan = batch.SortedSetScanAsync("key").GetAsyncEnumerator(cancellation.Token);
            var move = scan.MoveNextAsync().AsTask();
            batch.Execute();
            await scanReceived.Task.WaitAsync(s_timeout);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move.WaitAsync(s_timeout));
        }
        finally
        {
            scanReply.TrySetResult();
        }

        var barrier = batch.KeyExistsAsync("barrier");
        batch.Execute();
        Assert.False(await barrier.WaitAsync(s_timeout));
        await connection.CloseAsync().WaitAsync(s_timeout);
    }

    private sealed class CountingPipelineHook : RespirePipelineHook
    {
        private int _executions;

        internal int Executions => Volatile.Read(ref _executions);

        public override object? OnExecuting(RespirePipelineContext context)
        {
            Interlocked.Increment(ref _executions);
            return null;
        }

        public override void OnCompleted(RespirePipelineContext context, object? state,
            RespirePipelineOutcome outcome, Exception? error)
        {
        }
    }
}
