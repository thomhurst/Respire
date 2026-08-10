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
}
