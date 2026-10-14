using TUnit.Core;
using Verify = Respire.Analyzers.Tests.AnalyzerVerifier<Respire.Analyzers.SyncOverAsyncAnalyzer>;
using VerifyFix = Respire.Analyzers.Tests.CodeFixVerifier<Respire.Analyzers.SyncOverAsyncAnalyzer, Respire.Analyzers.SyncOverAsyncCodeFixProvider>;

namespace Respire.Analyzers.Tests;

public class SyncOverAsyncAnalyzerTests
{
    [Test]
    public Task IgnoresNameOfResult() => Verify.VerifyAsync("""
        using Respire;
        class C
        {
            void M(RespireClient client)
            {
                var task = client.ExecuteAsync("PING");
                var name = nameof(task.Result);
                var result = {|RESP009:task.Result|};
            }
        }
        """);

    [Test]
    public Task ReportsLocalReadsInAssignmentTargets() => Verify.VerifyAsync("""
        using System.Collections.Generic;
        using Respire;
        class C
        {
            void M(RespireClient client, Dictionary<string, string> dictionary)
            {
                var task = client.ExecuteAsync("PING");
                dictionary[{|RESP009:task.Result|}.AsString()] = "value";
                dictionary[{|RESP009:task.Result|}.AsString()] += "suffix";
                (dictionary[{|RESP009:task.Result|}.AsString()], dictionary["other"]) = ("one", "two");
                var result = {|RESP009:task.Result|};
            }
        }
        """);

    [Test]
    public Task IgnoresDeconstructionReassignedLocal() => Verify.VerifyAsync("""
        using Respire;
        class C
        {
            void M(RespireClient client)
            {
                var task = client.ExecuteAsync("PING");
                var other = client.ExecuteAsync("PONG");
                (task, other) = (other, task);
                var result = task.Result;
                result = other.Result;
            }
        }
        """);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task FixAllAddsAsyncOnce(bool localFunction)
    {
        var source = """
            using System.Threading.Tasks;
            using Respire;
            class C
            {
                Task M(RespireClient client)
                {
                    var first = {|RESP009:client.ExecuteAsync("PING").Result|};
                    var second = {|RESP009:client.ExecuteAsync("PONG").Result|};
                    throw new System.Exception();
                }
            }
            """;
        var fixedSource = """
            using System.Threading.Tasks;
            using Respire;
            class C
            {
                async Task M(RespireClient client)
                {
                    var first = await client.ExecuteAsync("PING");
                    var second = await client.ExecuteAsync("PONG");
                    throw new System.Exception();
                }
            }
            """;
        if (localFunction)
        {
            source = NestInOuter(source);
            fixedSource = NestInOuter(fixedSource);
        }
        return VerifyFix.VerifyAsync(source.ReplaceLineEndings(), fixedSource.ReplaceLineEndings());

        static string NestInOuter(string text)
        {
            var lines = text.Split('\n');
            return string.Join("\n", lines.Take(4)) + "\n    void Outer()\n    {\n"
                + string.Join("\n", lines.Skip(4).Take(lines.Length - 5).Select(line => "    " + line))
                + "\n    }\n}";
        }
    }

    [Test]
    [Arguments("client.ExecuteAsync(\"PING\").Result")]
    [Arguments("client.ExecuteAsync(\"PING\").AsTask().Result")]
    [Arguments("client.ExecuteAsync(\"PING\").GetAwaiter().GetResult()")]
    [Arguments("client.ExecuteAsync(\"PING\").ConfigureAwait(false).GetAwaiter().GetResult()")]
    [Arguments("client.ExecuteAsync(\"PING\").AsTask().ConfigureAwait(false).GetAwaiter().GetResult()")]
    [Arguments("client.ExecuteAsync(\"PING\").AsTask().WaitAsync(System.TimeSpan.FromSeconds(1)).Result")]
    public Task ReportsResultConsumption(string expression) => Verify.VerifyAsync($$"""
        using Respire;
        class C
        {
            void M(RespireClient client) { var result = {|RESP009:{{expression}}|}; }
        }
        """);

    [Test]
    [Arguments("client.ExecuteAsync(\"PING\").AsTask().Wait()")]
    [Arguments("client.ExecuteAsync(\"PING\").AsTask().Wait(100)")]
    [Arguments("client.CreateBatch().SendAsync().GetAwaiter().GetResult()")]
    [Arguments("client.CreateBatch().SendAsync().AsTask().Wait()")]
    public Task ReportsVoidAndTimedWaits(string expression) => Verify.VerifyAsync($$"""
        using Respire;
        class C
        {
            void M(RespireClient client) { {|RESP009:{{expression}}|}; }
        }
        """);

    [Test]
    public Task FollowsLocalAliases() => Verify.VerifyAsync("""
        using Respire;
        class C
        {
            void M(RespireClient client)
            {
                var task = client.ExecuteAsync("PING");
                var alias = task.AsTask();
                var configured = alias.ConfigureAwait(false);
                var result = {|RESP009:configured.GetAwaiter().GetResult()|};
                {|RESP009:alias.Wait()|};
                result = {|RESP009:task.Result|};
            }
        }
        """);

    [Test]
    public Task IgnoresUnrelatedTasksPendingResultsAndReassignedLocals() => Verify.VerifyAsync("""
        using System.Threading.Tasks;
        using Respire;
        class C
        {
            async Task M(RespireClient client, Task<int> other)
            {
                var value = other.Result;
                other.Wait();
                value = other.GetAwaiter().GetResult();
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                await batch.SendAsync();
                var text = pending.Result;
                text = pending.GetAwaiter().GetResult();
                var task = client.ExecuteAsync("PING");
                task = default;
                var result = task.Result;
                var another = client.ExecuteAsync("PING");
                Replace(ref another);
                result = another.Result;
                result = await client.ExecuteAsync("PING");
            }
            void Replace(ref ValueTask<RespireResult> task) { task = default; }
        }
        """);

    [Test]
    public Task DetectsExtensionsAndGeneratedInterfaces() => Verify.VerifyAsync("""
        using System;
        using System.Threading.Tasks;
        using Respire;
        namespace Respire
        {
            public class RespireCommandsAttribute : Attribute { }
        }
        namespace Respire.Custom
        {
            public static class Extensions
            {
                public static Task<int> CustomAsync(this RespireClient client) => Task.FromResult(1);
            }
        }
        namespace App
        {
            using Respire.Custom;
            [RespireCommands]
            interface ICommands { Task<int> PingAsync(); }
            class Commands : ICommands
            {
                public Task<int> PingAsync() => Task.FromResult(1);
                public Task<int> UnrelatedAsync() => Task.FromResult(2);
            }
            class C
            {
                void M(RespireClient client, ICommands commands)
                {
                    var x = {|RESP009:client.CustomAsync().Result|};
                    x = {|RESP009:commands.PingAsync().Result|};
                    x = {|RESP009:new Commands().PingAsync().Result|};
                    x = new Commands().UnrelatedAsync().Result;
                }
            }
        }
        """);

    [Test]
    [Arguments("client.ExecuteAsync(\"PING\").Result", "client.ExecuteAsync(\"PING\")")]
    [Arguments("client.ExecuteAsync(\"PING\").AsTask().Result", "client.ExecuteAsync(\"PING\").AsTask()")]
    [Arguments("client.ExecuteAsync(\"PING\").ConfigureAwait(false).GetAwaiter().GetResult()", "client.ExecuteAsync(\"PING\").ConfigureAwait(false)")]
    public Task FixesResultInAsyncMethod(string expression, string operand) => VerifyFix.VerifyAsync($$"""
        using System.Threading.Tasks;
        using Respire;
        class C
        {
            async Task M(RespireClient client)
            {
                var result = {|RESP009:{{expression}}|};
                await Task.Yield();
            }
        }
        """, $$"""
        using System.Threading.Tasks;
        using Respire;
        class C
        {
            async Task M(RespireClient client)
            {
                var result = await {{operand}};
                await Task.Yield();
            }
        }
        """);

    [Test]
    public Task FixesWaitAndAddsAsyncWithoutChangingSignature() => VerifyFix.VerifyAsync("""
        using System.Threading.Tasks;
        using Respire;
        class C
        {
            Task<RespireResult> M(RespireClient client)
            {
                var result = {|RESP009:client.ExecuteAsync("PING").Result|};
                throw new System.Exception();
            }
        }
        """, """
        using System.Threading.Tasks;
        using Respire;
        class C
        {
            async Task<RespireResult> M(RespireClient client)
            {
                var result = await client.ExecuteAsync("PING");
                throw new System.Exception();
            }
        }
        """);

    [Test]
    public Task FixesVoidWait() => VerifyFix.VerifyAsync("""
        using System.Threading.Tasks;
        using Respire;
        class C
        {
            async Task M(RespireClient client)
            {
                {|RESP009:client.CreateBatch().SendAsync().AsTask().Wait()|};
                await Task.Yield();
            }
        }
        """, """
        using System.Threading.Tasks;
        using Respire;
        class C
        {
            async Task M(RespireClient client)
            {
                await client.CreateBatch().SendAsync().AsTask();
                await Task.Yield();
            }
        }
        """);

    [Test]
    [Arguments("lock (client) { var result = {|RESP009:client.ExecuteAsync(\"PING\").Result|}; }")]
    [Arguments("{|RESP009:client.ExecuteAsync(\"PING\").AsTask().Wait(100)|};")]
    public Task DoesNotOfferInvalidOrSemanticsChangingFix(string statement) => VerifyFix.VerifyNoFixAsync($$"""
        using System.Threading.Tasks;
        using Respire;
        class C
        {
            async Task M(RespireClient client)
            {
                {{statement}}
                await Task.Yield();
            }
        }
        """);

    [Test]
    public Task FixAllPreservesMemberAccessAndLocalAliases() => VerifyFix.VerifyAsync("""
        using System.Threading.Tasks;
        using Respire;
        class C
        {
            async Task M(RespireClient client)
            {
                var task = client.ExecuteAsync("PING");
                var text = {|RESP009:task.Result|}.AsString();
                var length = {|RESP009:client.ExecuteAsync("PING").GetAwaiter().GetResult()|}.Count;
                await Task.Yield();
            }
        }
        """, """
        using System.Threading.Tasks;
        using Respire;
        class C
        {
            async Task M(RespireClient client)
            {
                var task = client.ExecuteAsync("PING");
                var text = (await task).AsString();
                var length = (await client.ExecuteAsync("PING")).Count;
                await Task.Yield();
            }
        }
        """);

    [Test]
    public Task FixesInsideAsyncLambda() => VerifyFix.VerifyAsync("""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class C
        {
            void M(RespireClient client)
            {
                Func<Task> action = async () =>
                {
                    {|RESP009:client.ExecuteAsync("PING").AsTask().Wait()|};
                    await Task.Yield();
                };
            }
        }
        """, """
        using System;
        using System.Threading.Tasks;
        using Respire;
        class C
        {
            void M(RespireClient client)
            {
                Func<Task> action = async () =>
                {
                    await client.ExecuteAsync("PING").AsTask();
                    await Task.Yield();
                };
            }
        }
        """);

    [Test]
    public Task ReportsTopLevelBlocking() => Verify.VerifyTopLevelAsync("""
        using Respire;
        var client = new RespireClient();
        var task = client.ExecuteAsync("PING");
        var result = {|RESP009:task.Result|};
        """);

    [Test]
    public Task DoesNotRewriteTaskReturningMethodWithIncompatibleReturns() => VerifyFix.VerifyNoFixAsync("""
        using System.Threading.Tasks;
        using Respire;
        class C
        {
            Task<RespireResult> M(RespireClient client)
            {
                return Task.FromResult({|RESP009:client.ExecuteAsync("PING").Result|});
            }
        }
        """);

    [Test]
    public Task DoesNotChangeSynchronousSignature() => VerifyFix.VerifyNoFixAsync("""
        using Respire;
        class C
        {
            RespireResult M(RespireClient client) => {|RESP009:client.ExecuteAsync("PING").Result|};
        }
        """);
}
