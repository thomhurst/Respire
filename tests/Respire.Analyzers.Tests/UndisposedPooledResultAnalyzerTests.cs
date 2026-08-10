using TUnit.Core;
using Verify = Respire.Analyzers.Tests.AnalyzerVerifier<Respire.Analyzers.UndisposedPooledResultAnalyzer>;
using VerifyFix = Respire.Analyzers.Tests.CodeFixVerifier<
    Respire.Analyzers.UndisposedPooledResultAnalyzer, Respire.Analyzers.UndisposedPooledResultCodeFixProvider>;

namespace Respire.Analyzers.Tests;

public class UndisposedPooledResultAnalyzerTests
{
    [Test]
    public async Task AwaitedResultNeverDisposed_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var {|RESP001:result|} = await client.ExecuteAsync("PING");
                Console.WriteLine(result.AsString());
            }
        }
        """);

    [Test]
    public async Task AwaitedLeaseNeverDisposed_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var {|RESP001:lease|} = await client.GetLeaseAsync("key");
                Console.WriteLine(lease.Length);
            }
        }
        """);

    [Test]
    public async Task UsingDeclaration_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                using var result = await client.ExecuteAsync("PING");
                Console.WriteLine(result.AsString());
            }
        }
        """);

    [Test]
    public async Task ExplicitDispose_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var result = await client.ExecuteAsync("PING");
                Console.WriteLine(result.AsString());
                result.Dispose();
            }
        }
        """);

    [Test]
    public async Task ConditionalDispose_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool dispose)
            {
                var {|RESP001:result|} = await client.ExecuteAsync("PING");
                Console.WriteLine(result.AsString());
                if (dispose)
                {
                    result.Dispose();
                }
            }
        }
        """);

    [Test]
    public async Task UsingStatement_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var result = await client.ExecuteAsync("PING");
                using (result)
                {
                    Console.WriteLine(result.AsString());
                }
            }
        }
        """);

    [Test]
    public async Task ConditionalUsingStatement_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool dispose)
            {
                var {|RESP001:result|} = await client.ExecuteAsync("PING");
                if (dispose)
                {
                    using (result)
                    {
                    }
                }
            }
        }
        """);

    [Test]
    public async Task DirectNameOf_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var {|RESP001:result|} = await client.ExecuteAsync("PING");
                Console.WriteLine(nameof(result));
            }
        }
        """);

    [Test]
    public async Task ResultPassedToExtensionMethod_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var result = await client.ExecuteAsync("PING");
                result.Consume();
            }
        }

        public static class ResultExtensions
        {
            public static void Consume(this RespireResult result) => result.Dispose();
        }
        """);

    [Test]
    public async Task DisposeMethodGroup_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var result = await client.ExecuteAsync("PING");
                Action dispose = result.Dispose;
                dispose();
            }
        }
        """);

    [Test]
    public async Task TopLevelDispose_IsNotFlagged() => await Verify.VerifyTopLevelAsync(
        """
        using Respire;

        var client = new RespireClient();
        var result = await client.ExecuteAsync("PING");
        result.Dispose();
        """);

    [Test]
    public async Task LambdaDispose_IsNotFlagged() => await Verify.VerifyAsync(
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
                    var result = await client.ExecuteAsync("PING");
                    result.Dispose();
                };

                await run();
            }
        }
        """);

    [Test]
    public async Task LocalFunctionDispose_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                await RunLocalAsync();

                async Task RunLocalAsync()
                {
                    var result = await client.ExecuteAsync("PING");
                    result.Dispose();
                }
            }
        }
        """);

    [Test]
    public async Task EarlyReturnBeforeDispose_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool stop)
            {
                var {|RESP001:result|} = await client.ExecuteAsync("PING");
                if (stop)
                {
                    return;
                }

                Console.WriteLine(result.AsString());
                result.Dispose();
            }
        }
        """);

    [Test]
    public async Task AwaitedForwardedResult_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireResult existingResult)
            {
                var copy = await Task.FromResult(existingResult);
                Console.WriteLine(copy.AsString());
            }
        }
        """);

    [Test]
    public async Task AsTaskResultNeverDisposed_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var {|RESP001:result|} = await client.ExecuteAsync("PING").AsTask();
                Console.WriteLine(result.AsString());
            }
        }
        """);

    [Test]
    public async Task DisposeInsideNameOf_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var {|RESP001:result|} = await client.ExecuteAsync("PING");
                Console.WriteLine(nameof(result.Dispose));
            }
        }
        """);

    [Test]
    public async Task ReturnedResult_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task<RespireResult> RunAsync(RespireClient client)
            {
                var result = await client.ExecuteAsync("PING");
                return result;
            }
        }
        """);

    [Test]
    public async Task ResultPassedToAnotherMethod_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var result = await client.ExecuteAsync("PING");
                Consume(result);
            }

            private static void Consume(RespireResult result) => result.Dispose();
        }
        """);

    [Test]
    public async Task ResultStoredInField_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            private RespireResult _last;

            public async Task RunAsync(RespireClient client)
            {
                var result = await client.ExecuteAsync("PING");
                _last = result;
            }
        }
        """);

    [Test]
    public async Task NestedIndexerView_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                using var result = await client.ExecuteAsync("MGET", "a", "b");
                var first = result[0];
                var second = result[1];
                Console.WriteLine(first.AsString() + second.AsString());
            }
        }
        """);

    [Test]
    public async Task ResultCapturedByLambda_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var result = await client.ExecuteAsync("PING");
                Action dispose = () => result.Dispose();
                dispose();
            }
        }
        """);

    [Test]
    public async Task CodeFix_AddsUsingDeclaration() => await VerifyFix.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var {|RESP001:result|} = await client.ExecuteAsync("PING");
                Console.WriteLine(result.AsString());
            }
        }
        """,
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                using var result = await client.ExecuteAsync("PING");
                Console.WriteLine(result.AsString());
            }
        }
        """);

    [Test]
    public async Task CodeFix_IsNotOfferedInSwitchSection() => await VerifyFix.VerifyNoFixAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, int value)
            {
                switch (value)
                {
                    case 1:
                        var {|RESP001:result|} = await client.ExecuteAsync("PING");
                        Console.WriteLine(result.AsString());
                        break;
                }
            }
        }
        """);

    [Test]
    public async Task ChainedAdaptersWithoutDispose_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var {|RESP001:result|} = await client.ExecuteAsync("PING").AsTask().ConfigureAwait(false);
                Console.WriteLine(result.AsString());
            }
        }
        """);

    [Test]
    public async Task NamespaceHelperReturningExistingResult_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;

        namespace Respire
        {
            public static class Forwarder
            {
                public static Task<RespireResult> ForwardAsync(RespireResult result) => Task.FromResult(result);
            }

            public class Caller
            {
                public async Task RunAsync(RespireResult existingResult)
                {
                    var copy = await Forwarder.ForwardAsync(existingResult);
                    Console.WriteLine(copy.AsString());
                }
            }
        }
        """);

    [Test]
    public async Task OverwrittenResultWithoutDispose_IsFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var {|RESP001:result|} = await client.ExecuteAsync("PING");
                result = default;
            }
        }
        """);

    [Test]
    public async Task DisposingReplacementAfterOverwrite_IsFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var {|RESP001:result|} = await client.ExecuteAsync("PING");
                result = default;
                result.Dispose();
            }
        }
        """);

    [Test]
    public async Task ResultAssignedAfterDeclarationWithoutDispose_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                RespireResult result;
                {|RESP001:result|} = await client.ExecuteAsync("PING");
                Console.WriteLine(result.AsString());
            }
        }
        """);

    [Test]
    public async Task ResultAssignedAfterDeclarationAndDisposed_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                RespireResult result;
                result = await client.ExecuteAsync("PING");
                result.Dispose();
            }
        }
        """);

    [Test]
    public async Task RepeatedAssignmentInLoop_IsFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool condition)
            {
                RespireResult result = default;
                while (condition)
                {
                    {|RESP001:result|} = await client.ExecuteAsync("PING");
                }

                result.Dispose();
            }
        }
        """);

    [Test]
    public async Task RepeatedAssignmentDisposedEachIteration_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool condition)
            {
                RespireResult result;
                do
                {
                    result = await client.ExecuteAsync("PING");
                    result.Dispose();
                }
                while (condition);
            }
        }
        """);

    [Test]
    public async Task AssignmentConsumedByUsing_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                RespireResult result;
                using (result = await client.ExecuteAsync("PING"))
                {
                }
            }
        }
        """);

    [Test]
    public async Task AssignmentConsumedByReturn_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task<RespireResult> RunAsync(RespireClient client)
            {
                RespireResult result;
                return result = await client.ExecuteAsync("PING");
            }
        }
        """);

    [Test]
    public async Task NullableResultWithoutDispose_IsFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                RespireResult? {|RESP001:result|} = await client.ExecuteAsync("PING");
            }
        }
        """);

    [Test]
    public async Task CoalesceAssignedResultWithoutDispose_IsFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                RespireResult? result = null;
                {|RESP001:result|} ??= await client.ExecuteAsync("PING");
            }
        }
        """);

    [Test]
    public async Task CoalesceAssignedResultThenDisposed_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                RespireResult? result = null;
                result ??= await client.ExecuteAsync("PING");
                result?.Dispose();
            }
        }
        """);

    [Test]
    public async Task SiblingBranchAssignmentThenDispose_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool condition)
            {
                RespireResult result;
                if (condition)
                {
                    result = await client.ExecuteAsync("PING");
                }
                else
                {
                    result = default;
                }

                result.Dispose();
            }
        }
        """);

    [Test]
    public async Task FieldInitializerLambdaDispose_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public Func<RespireClient, Task> Run { get; } = async client =>
            {
                var result = await client.ExecuteAsync("PING");
                Console.WriteLine(result.AsString());
                result.Dispose();
            };
        }
        """);

    [Test]
    public async Task DiscardedResultWithoutDispose_IsFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var {|RESP001:result|} = await client.ExecuteAsync("PING");
                _ = result;
            }
        }
        """);

    [Test]
    public async Task WrappedResultUseWithoutDispose_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var {|RESP001:result|} = await client.ExecuteAsync("PING");
                Console.WriteLine(((result!)).AsString());
            }
        }
        """);

    [Test]
    public async Task ConditionalOwnershipTransfer_IsFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool transfer)
            {
                var {|RESP001:result|} = await client.ExecuteAsync("PING");
                if (transfer)
                {
                    Consume(result);
                }
            }

            private static void Consume(RespireResult result) { }
        }
        """);

    [Test]
    public async Task ExhaustiveBranchReleaseOrTransfer_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool transfer)
            {
                var result = await client.ExecuteAsync("PING");
                if (transfer)
                {
                    Consume(result);
                }
                else
                {
                    result.Dispose();
                }
            }

            private static void Consume(RespireResult result) { }
        }
        """);
}
