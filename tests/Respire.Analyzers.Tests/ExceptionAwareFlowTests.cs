using TUnit.Core;
using Pending = Respire.Analyzers.Tests.AnalyzerVerifier<Respire.Analyzers.PendingReadBeforeFlushAnalyzer>;
using Disposal = Respire.Analyzers.Tests.AnalyzerVerifier<Respire.Analyzers.UndisposedPooledResultAnalyzer>;

namespace Respire.Analyzers.Tests;

public class ExceptionAwareFlowTests
{
    [Test]
    [Arguments("MayThrow()")]
    [Arguments("Property")]
    public async Task ThrowingConditionCanBypassBothFlushBranches(string condition) => await Pending.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            bool MayThrow() => throw new Exception();
            bool Property => throw new Exception();
            async Task Run(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("a");
                try { if ({{condition}}) await batch.SendAsync(); else await batch.SendAsync(); }
                catch (Exception) { }
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task ExplicitExceptionCastCanReachInvalidCastHandler() => await Pending.VerifyAsync("""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            async Task Run(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("a");
                try { throw (InvalidOperationException)(object)new Exception(); }
                catch (InvalidCastException) { }
                catch (Exception) { await batch.SendAsync(); }
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CatchAcquisitionRetainsEnclosingPredicate(bool filtered) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            void MayThrow() => throw new Exception();
            async Task Run(RespireClient client, bool choice, bool filter)
            {
                if (choice)
                {
                    try { MayThrow(); }
                    catch (Exception) {{(filtered ? "when (filter)" : "")}}
                    {
                        var result = await client.ExecuteAsync("PING");
                        if (choice) result.Dispose();
                    }
                }
            }
        }
        """);

    [Test]
    [Arguments("choice = !choice;", "", "", false)]
    [Arguments("", "", "choice = !choice;", false)]
    [Arguments("", "choice = !choice;", "", true)]
    public async Task PredicateWritesOnlyInvalidateTheTraversedInterval(
        string before, string between, string after, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, RespireResult existing, bool choice)
                {
                    {{before}}
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = choice ? await client.ExecuteAsync("PING") : existing;
                    {{between}}
                    if (choice) result.Dispose();
                    {{after}}
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, bool choice)
                {
                    var first = client.CreateBatch();
                    var second = client.CreateBatch();
                    {{before}}
                    var pending = choice ? first.GetStringAsync("a") : second.GetStringAsync("b");
                    {{between}}
                    if (choice) await first.SendAsync(); else await second.SendAsync();
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                    {{after}}
                }
            }
            """);
    }

    [Test]
    public async Task AddressOfArrayElementPreservesIndexPredicate() => await Pending.VerifyUnsafeAsync("""
        using System;
        using Respire;
        class Caller
        {
            unsafe void Run(RespireClient client, bool choice, byte[] buffer)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice ? first.GetStringAsync("a") : second.GetStringAsync("b");
                fixed (byte* pointer = &buffer[choice ? 0 : 1]) { *pointer = 1; }
                if (choice) first.SendAsync().AsTask().GetAwaiter().GetResult();
                else second.SendAsync().AsTask().GetAwaiter().GetResult();
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    [Arguments("choice == false", "!choice")]
    [Arguments("!choice", "choice == false")]
    [Arguments("choice != false", "choice")]
    [Arguments("false == choice", "choice is false")]
    public async Task ComplementaryBooleanPredicatesShareEvidence(string selection, string cleanup)
    {
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, RespireResult existing, bool choice)
                {
                    var result = {{selection}} ? await client.ExecuteAsync("PING") : existing;
                    if ({{cleanup}}) result.Dispose();
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, bool choice)
                {
                    var first = client.CreateBatch();
                    var second = client.CreateBatch();
                    var pending = {{selection}} ? first.GetStringAsync("a") : second.GetStringAsync("b");
                    if ({{cleanup}}) await first.SendAsync(); else await second.SendAsync();
                    Console.WriteLine(pending.Result);
                }
            }
            """);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AddressTakenPredicateCannotProveConditionalFlush(bool local) => await Pending.VerifyUnsafeAsync($$"""
        using System;
        using Respire;
        class Caller
        {
            unsafe void Run(RespireClient client, bool choice)
            {
                {{(local ? "bool condition = choice;" : "")}}
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = {{(local ? "condition" : "choice")}} ? first.GetStringAsync("a") : second.GetStringAsync("b");
                bool* pointer = &{{(local ? "condition" : "choice")}};
                *pointer = false;
                try { throw new Exception(); }
                catch (Exception) when ({{(local ? "condition" : "choice")}}) { first.SendAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception) { second.SendAsync().AsTask().GetAwaiter().GetResult(); }
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task UnrelatedBranchesBeforeAcquisitionDoNotExhaustSearch()
    {
        var parameters = string.Join(", ", Enumerable.Range(0, 14).Select(index => $"bool condition{index}"));
        var branches = string.Join(Environment.NewLine, Enumerable.Range(0, 14)
            .Select(index => $"if (condition{index}) Console.WriteLine({index});"));
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, {{parameters}})
                {
                    {{branches}}
                    var result = await client.ExecuteAsync("PING");
                    result.Dispose();
                }
            }
            """);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FilteredCatchOriginRetainsSelectionForDisposal(bool inverted)
    {
        var local = inverted ? "{|RESP001:result|}" : "result";
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, bool choice)
                {
                    try { Console.WriteLine("may throw"); }
                    catch (Exception) when (choice)
                    {
                        var {{local}} = await client.ExecuteAsync("PING");
                        if ({{(inverted ? "!choice" : "choice")}}) result.Dispose();
                    }
                }
            }
            """);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FilteredCatchOriginRetainsSelectionForFlush(bool inverted)
    {
        var read = inverted ? "{|RESP002:pending.Result|}" : "pending.Result";
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, bool choice)
                {
                    var batch = client.CreateBatch();
                    try { Console.WriteLine("may throw"); }
                    catch (Exception) when (choice)
                    {
                        var pending = batch.GetStringAsync("key");
                        if ({{(inverted ? "!choice" : "choice")}}) await batch.SendAsync();
                        Console.WriteLine({{read}});
                    }
                }
            }
            """);
    }

    [Test]
    [Arguments("bool condition = choice;", "condition", "condition", "", false)]
    [Arguments("", "choice", "choice", "Change(ref choice);", true)]
    [Arguments("", "choice", "choice", "void ChangeChoice() { choice = !choice; } ChangeChoice();", true)]
    [Arguments("", "(byte)number == 0", "number == 0", "", true)]
    [Arguments("", "choice ? other : false", "other", "", true)]
    public async Task CorrelationRequiresStableEquivalentValues(
        string declaration, string select, string flush, string mutation, bool warning)
    {
        var read = warning ? "{|RESP002:pending.Result|}" : "pending.Result";
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                static void Change(ref bool value) { value = !value; }
                async Task Run(RespireClient client, bool choice, bool other, int number)
                {
                    {{declaration}}
                    var first = client.CreateBatch();
                    var second = client.CreateBatch();
                    var pending = ({{select}}) ? first.GetStringAsync("a") : second.GetStringAsync("b");
                    {{mutation}}
                    if ({{flush}}) await first.SendAsync();
                    else await second.SendAsync();
                    Console.WriteLine({{read}});
                }
            }
            """);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task CatchOriginInsideFinallyRetainsContinuation(bool flush)
    {
        var read = flush ? "pending.Result" : "{|RESP002:pending.Result|}";
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client)
                {
                    var batch = client.CreateBatch();
                    RespirePending<string> pending = null;
                    try { Console.WriteLine("work"); }
                    finally
                    {
                        try { Console.WriteLine("may throw"); }
                        catch (Exception) { pending = batch.GetStringAsync("key"); }
                    }
                    {{(flush ? "await batch.SendAsync();" : "")}}
                    Console.WriteLine({{read}});
                }
            }
            """);
    }

    [Test]
    public async Task ThrowNullDispatchesNullReferenceException() => await Pending.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            async Task Run(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try { throw (ArgumentException)null; }
                catch (ArgumentException) { await batch.SendAsync(); }
                catch (NullReferenceException) { Console.WriteLine("not flushed"); }
                finally { Console.WriteLine({|RESP002:pending.Result|}); }
            }
        }
        """);

    [Test]
    [Arguments("true", false)]
    [Arguments("false", true)]
    [Arguments("handle", true)]
    public async Task FilterAcceptanceControlsLaterHandlers(string filter, bool warning)
    {
        var read = warning ? "{|RESP002:pending.Result|}" : "pending.Result";
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, bool handle)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { throw new InvalidOperationException(); }
                    catch (Exception) when ({{filter}}) { await batch.SendAsync(); }
                    catch (Exception) { Console.WriteLine("not flushed"); }
                    finally { Console.WriteLine({{read}}); }
                }
            }
            """);
    }

    [Test]
    [Arguments("goto Flush;", false)]
    [Arguments("goto Done;", true)]
    public async Task CorrelatedPendingFollowsLocalGoto(string jump, bool warning)
    {
        var read = warning ? "{|RESP002:pending.Result|}" : "pending.Result";
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, bool choice)
                {
                    var first = client.CreateBatch();
                    var second = client.CreateBatch();
                    var pending = choice ? first.GetStringAsync("a") : second.GetStringAsync("b");
                    if (choice)
                    {
                        {{jump}}
                    Flush:
                        await first.SendAsync();
                    Done:
                        Console.WriteLine("done");
                    }
                    else await second.SendAsync();
                    Console.WriteLine({{read}});
                }
            }
            """);
    }

    [Test]
    [Arguments("goto Release;", false)]
    [Arguments("goto Done;", true)]
    [Arguments("return;", true)]
    [Arguments("throw new System.Exception();", true)]
    public async Task OwningSwitchArmFollowsExitPath(string jump, bool warning)
    {
        var local = warning ? "{|RESP001:result|}" : "result";
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, RespireResult existing, int choice)
                {
                    var {{local}} = choice switch { 0 => await client.ExecuteAsync("PING"), _ => existing };
                    {{jump}}
                Release:
                    if (choice == 0) result.Dispose();
                Done:
                    System.Console.WriteLine("done");
                }
            }
            """);
    }

    [Test]
    [Arguments("handle", false)]
    [Arguments("!handle", true)]
    public async Task BranchSelectionCorrelatesWithExceptionFilter(string filter, bool warning)
    {
        var read = warning ? "{|RESP002:pending.Result|}" : "pending.Result";
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, bool handle)
                {
                    var first = client.CreateBatch();
                    var second = client.CreateBatch();
                    var pending = handle ? first.GetStringAsync("a") : second.GetStringAsync("b");
                    try { throw new Exception(); }
                    catch (Exception) when ({{filter}}) { await first.SendAsync(); }
                    catch (Exception) { await second.SendAsync(); }
                    Console.WriteLine({{read}});
                }
            }
            """);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task YieldRequiresReleaseOnIteratorDisposal(bool finallyRelease)
    {
        var local = finallyRelease ? "result" : "{|RESP001:result|}";
        var body = finallyRelease
            ? "try { yield return 1; } finally { result.Dispose(); }"
            : "yield return 1; result.Dispose();";
        await Disposal.VerifyAsync($$"""
            using System.Collections.Generic;
            using Respire;
            class Caller
            {
                async IAsyncEnumerable<int> Run(RespireClient client)
                {
                    var {{local}} = await client.ExecuteAsync("PING");
                    {{body}}
                }
            }
            """);
    }

    [Test]
    public async Task FilterRunsBeforeInnerFinallyUnwinds() => await Pending.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            async Task Run(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try
                {
                    try { throw new Exception(); }
                    finally { await batch.SendAsync(); }
                }
                catch (Exception) when ({|RESP002:pending.Result|} != null) { }
            }
        }
        """);

    [Test]
    public async Task RethrowKeepsCatchType() => await Pending.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            async Task Run<T>(RespireClient client, T error) where T : InvalidOperationException
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try
                {
                    try { throw error; }
                    catch (InvalidOperationException) { throw; }
                }
                catch (ArgumentException) { Console.WriteLine("not applicable"); }
                catch (Exception) { await batch.SendAsync(); }
                finally { Console.WriteLine(pending.Result); }
            }
        }
        """);
}
