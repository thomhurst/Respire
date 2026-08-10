using TUnit.Core;
using Verify = Respire.Analyzers.Tests.AnalyzerVerifier<Respire.Analyzers.PendingReadBeforeFlushAnalyzer>;

namespace Respire.Analyzers.Tests;

public class PendingReadBeforeFlushAnalyzerTests
{
    [Test]
    public async Task ResultReadBeforeSend_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                Console.WriteLine({|RESP002:pending.Result|});
                await batch.SendAsync();
            }
        }
        """);

    [Test]
    public async Task AwaitedPendingBeforeSend_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                Console.WriteLine({|RESP002:await pending|});
                await batch.SendAsync();
            }
        }
        """);

    [Test]
    public async Task InlineAwaitOfQueuedCommand_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                Console.WriteLine({|RESP002:await batch.GetStringAsync("key")|});
                await batch.SendAsync();
            }
        }
        """);

    [Test]
    public async Task TransactionResultReadBeforeCommit_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var transaction = client.CreateTransaction();
                var pending = transaction.GetStringAsync("key");
                Console.WriteLine({|RESP002:pending.Result|});
                await transaction.CommitAsync();
            }
        }
        """);

    [Test]
    public async Task SendThenRead_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                await batch.SendAsync();
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task CommitThenRead_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                await using var transaction = client.CreateTransaction();
                var pending = transaction.GetStringAsync("key");
                await transaction.CommitAsync();
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task StartedButUnawaitedSendBeforeRead_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                var send = batch.SendAsync();
                Console.WriteLine({|RESP002:pending.Result|});
                await send;
            }
        }
        """);

    [Test]
    public async Task StartedButUnawaitedCommitBeforeRead_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                await using var transaction = client.CreateTransaction();
                var pending = transaction.GetStringAsync("key");
                var commit = transaction.CommitAsync();
                Console.WriteLine({|RESP002:pending.Result|});
                await commit;
            }
        }
        """);

    [Test]
    public async Task AwaitedSendLocalBeforeRead_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                var send = batch.SendAsync();
                await send;
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task ReassignedSendLocalBeforeRead_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                var flush = batch.SendAsync();
                flush = default;
                await flush;
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task ConfigureAwaitSendBeforeRead_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                await batch.SendAsync().ConfigureAwait(false);
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task AsTaskSendBeforeRead_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                await batch.SendAsync().AsTask();
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task LambdaSendThenRead_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                Func<Task> run = async () =>
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    await batch.SendAsync();
                    Console.WriteLine(pending.Result);
                };

                await run();
            }
        }
        """);

    [Test]
    public async Task ConditionalSendBeforeRead_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool send)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                if (send)
                {
                    await batch.SendAsync();
                }

                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task ConditionallyAwaitedSendLocalBeforeRead_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool send)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                var flush = batch.SendAsync();
                if (send)
                {
                    await flush;
                }

                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task ConditionalResultReadBeforeSend_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                Console.WriteLine({|RESP002:pending?.Result|});
                await batch.SendAsync();
            }
        }
        """);

    [Test]
    public async Task ResultInsideNameOf_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using Respire;

        public class Caller
        {
            public void Run(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                Console.WriteLine(nameof(pending.Result));
            }
        }
        """);

    [Test]
    public async Task PendingPassedToAnotherMethod_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                Register(pending);
                Console.WriteLine(pending.Result);
                await batch.SendAsync();
            }

            private static void Register(RespirePending<string> pending)
            {
            }
        }
        """);

    [Test]
    public async Task BatchFlushedByAnotherMethod_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                await FlushAsync(batch);
                Console.WriteLine(pending.Result);
            }

            private static async Task FlushAsync(RespireBatch batch) => await batch.SendAsync();
        }
        """);

    [Test]
    public async Task BatchFlushedByExtensionMethod_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                await batch.FlushAsync();
                Console.WriteLine(pending.Result);
            }
        }

        public static class BatchExtensions
        {
            public static Task FlushAsync(this RespireBatch batch) => batch.SendAsync().AsTask();
        }
        """);

    [Test]
    public async Task BatchFlushedByMethodGroup_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                Func<ValueTask> flush = batch.SendAsync;
                await flush();
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task TopLevelSendThenRead_IsNotFlagged() => await Verify.VerifyTopLevelAsync(
        """
        using System;
        using Respire;

        var client = new RespireClient();
        var batch = client.CreateBatch();
        var pending = batch.GetStringAsync("key");
        await batch.SendAsync();
        Console.WriteLine(pending.Result);
        """);

    [Test]
    public async Task PendingReturnedFromMethod_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public RespirePending<string> Queue(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                return pending;
            }
        }
        """);

    [Test]
    public async Task BatchOwnedByField_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            private readonly RespireBatch _batch = new RespireClient().CreateBatch();

            public void Read()
            {
                var pending = _batch.GetStringAsync("key");
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task PendingReadInLambda_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                await batch.SendAsync();
                Action print = () => Console.WriteLine(pending.Result);
                print();
            }
        }
        """);

    [Test]
    public async Task BatchFacetResultReadBeforeSend_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.Hashes.GetStringAsync("key", "field");
                Console.WriteLine({|RESP002:pending.Result|});
                await batch.SendAsync();
            }
        }
        """);

    [Test]
    public async Task AssignedTransactionFacetReadBeforeCommit_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                await using var transaction = client.CreateTransaction();
                RespirePending<string> pending;
                pending = transaction.Hashes.GetStringAsync("key", "field");
                Console.WriteLine({|RESP002:pending.Result|});
                await transaction.CommitAsync();
            }
        }
        """);

    [Test]
    public async Task BatchReassignedAfterRead_DoesNotSuppressDiagnostic() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                Console.WriteLine({|RESP002:pending.Result|});
                batch = client.CreateBatch();
                await batch.SendAsync();
            }
        }
        """);

    [Test]
    public async Task ReassignedBatchFlush_DoesNotFlushOriginalBatch() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                batch = client.CreateBatch();
                await batch.SendAsync();
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task NameOfBatchAndPending_DoNotSuppressDiagnostic() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                Console.WriteLine(nameof(batch) + nameof(pending));
                Console.WriteLine({|RESP002:pending.Result|});
                await batch.SendAsync();
            }
        }
        """);

    [Test]
    public async Task StoredAsTaskFlushBeforeRead_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                var flush = batch.SendAsync().AsTask();
                await flush;
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task FlushAssignedAfterDeclarationBeforeRead_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                ValueTask flush;
                flush = batch.SendAsync();
                await flush;
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task NullForgivingPendingReadBeforeSend_IsFlagged() => await Verify.VerifyAsync(
        """
        #nullable enable
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                RespirePending<string>? pending = batch.GetStringAsync("key");
                Console.WriteLine({|RESP002:pending!.Result|});
                await batch.SendAsync();
            }
        }
        """);

    [Test]
    public async Task FieldInitializerLambdaSendThenRead_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public Func<RespireClient, Task> Run { get; } = async client =>
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                await batch.SendAsync();
                Console.WriteLine(pending.Result);
            };
        }
        """);

    [Test]
    public async Task NullForgivingBatchPassedToHelper_IsNotFlagged() => await Verify.VerifyAsync(
        """
        #nullable enable
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                RespireBatch? batch = client.CreateBatch();
                var pending = batch!.GetStringAsync("key");
                await FlushAsync(batch!);
                Console.WriteLine(pending.Result);
            }

            private static async Task FlushAsync(RespireBatch batch) => await batch.SendAsync();
        }
        """);

    [Test]
    public async Task WhenAllFlushBeforeRead_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                await Task.WhenAll(batch.SendAsync().AsTask());
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task WaitAsyncFlushBeforeRead_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, CancellationToken cancellationToken)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                await batch.SendAsync().AsTask().WaitAsync(cancellationToken);
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task DiscardedBatchAndPending_DoNotSuppressDiagnostic() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                _ = batch;
                _ = pending;
                Console.WriteLine({|RESP002:pending.Result|});
                await batch.SendAsync();
            }
        }
        """);

    [Test]
    public async Task EscapesAfterRead_DoNotSuppressDiagnostic() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                Console.WriteLine({|RESP002:pending.Result|});
                Consume(batch);
                Consume(pending);
                await batch.SendAsync();
            }

            private static void Consume(object value) { }
        }
        """);

    [Test]
    public async Task ConditionalEscapeBeforeRead_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool handled)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                if (handled)
                {
                    Consume(batch);
                }

                Console.WriteLine({|RESP002:pending.Result|});
                await batch.SendAsync();
            }

            private static void Consume(RespireBatch batch) { }
        }
        """);

    [Test]
    public async Task ConditionalPendingOverwriteFromUnflushedBatch_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool overwrite)
            {
                var batch = client.CreateBatch();
                var otherBatch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                await batch.SendAsync();
                if (overwrite)
                {
                    pending = otherBatch.GetStringAsync("other");
                }

                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task ExhaustiveBranchFlushesBeforeRead_AreNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool firstPath)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                if (firstPath)
                {
                    await batch.SendAsync();
                }
                else
                {
                    await batch.SendAsync();
                }

                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task ManualGetResultBeforeSend_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                Console.WriteLine({|RESP002:pending.GetAwaiter().GetResult()|});
                await batch.SendAsync();
            }
        }
        """);
}
