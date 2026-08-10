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
}
