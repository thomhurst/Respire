using TUnit.Core;
using Verify = Respire.Analyzers.Tests.AnalyzerVerifier<Respire.Analyzers.PendingReadBeforeFlushAnalyzer>;
using VerifyDisposal = Respire.Analyzers.Tests.AnalyzerVerifier<Respire.Analyzers.UndisposedPooledResultAnalyzer>;

namespace Respire.Analyzers.Tests;

// Exercise the shared exit component through the analyzer's observable diagnostics.
public class ScopeExitAnalysisTests
{
    [Test]
    [Arguments("InvalidOperationException", "catch (Exception) { throw; }", false)]
    [Arguments("T", "catch (Exception) { throw; }", false)]
    [Arguments("Exception", "catch (Exception) { throw; }", false)]
    [Arguments("InvalidOperationException", "catch (NullReferenceException) { }", true)]
    [Arguments("InvalidOperationException", "catch (NullReferenceException) { return; }", false)]
    [Arguments("Exception", "catch (ArgumentException) { }", true)]
    [Arguments("T", "catch (NullReferenceException) { }", true)]
    [Arguments("T", "catch (ArgumentException) { }", false)]
    public async Task ExistingThrownValuePreservesDeclaredTypeAndNullPath(string type, string outerHandler, bool warning)
    {
        var read = warning ? "{|RESP002:pending.Result|}" : "pending.Result";
        await Verify.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            public class Caller
            {
                public async Task RunAsync<T>(RespireClient client, bool choice, bool skip, {{type}} error)
                    where T : InvalidOperationException
                {
                    var first = client.CreateBatch();
                    var second = client.CreateBatch();
                    var pending = choice ? first.GetStringAsync("a") : second.GetStringAsync("b");
                    try
                    {
                        if (choice)
                        {
                            try { if (skip) throw error; }
                            catch (InvalidOperationException) { }
                            await first.SendAsync();
                        }
                        else await second.SendAsync();
                    }
                    {{outerHandler}}
                    Console.WriteLine({{read}});
                }
            }
            """);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExistingThrownValueRequiresReleaseOnNullPath(bool catchNull)
    {
        var result = catchNull ? "result" : "{|RESP001:result|}";
        await VerifyDisposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            public class Caller
            {
                public async Task RunAsync(RespireClient client, RespireResult existing, int choice, bool skip,
                    InvalidOperationException error)
                {
                    var {{result}} = choice switch { 0 => await client.ExecuteAsync("PING"), _ => existing };
                    try { if (skip) throw error; }
                    catch (InvalidOperationException) { }
                    {{(catchNull ? "catch (NullReferenceException) { }" : "")}}
                    if (choice == 0) result.Dispose();
                }
            }
            """);
    }

    [Test]
    [Arguments("break", false)]
    [Arguments("break", true)]
    [Arguments("continue", false)]
    [Arguments("continue", true)]
    [Arguments("return", false)]
    [Arguments("return", true)]
    [Arguments("throw new InvalidOperationException()", false)]
    [Arguments("throw new InvalidOperationException()", true)]
    public async Task ExitKindCrossedWithLoopAndFinallyRead(string jump, bool readInFinally) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool choice, bool skip)
            {
                for (var index = 0; index < 2; index++)
                {
                    var first = client.CreateBatch();
                    var second = client.CreateBatch();
                    var pending = choice ? first.GetStringAsync("a") : second.GetStringAsync("b");
                    {{{(readInFinally ? "try {" : "")}}}
                        if (choice)
                        {
                            if (skip) {{{jump}}};
                            await first.SendAsync();
                        }
                        else await second.SendAsync();
                    {{{(readInFinally ? "} finally { Console.WriteLine({|RESP002:pending.Result|}); }" : "Console.WriteLine(pending.Result);")}}}
                }
            }
        }
        """);

    [Test]
    [Arguments("return;")]
    [Arguments("throw new System.InvalidOperationException();")]
    public async Task TerminalExitStillRequiresCorrelatedRelease(string exit) => await VerifyDisposal.VerifyAsync(
        $$$"""
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, RespireResult existing, int choice, bool skip)
            {
                var {|RESP001:result|} = choice switch
                {
                    0 => await client.ExecuteAsync("PING"),
                    _ => existing,
                };
                if (skip) {{{exit}}}
                if (choice == 0) result.Dispose();
            }
        }
        """);

    [Test]
    public async Task AbandonedIteratorCanBypassCorrelatedRelease() => await VerifyDisposal.VerifyAsync(
        """
        using System.Collections.Generic;
        using Respire;
        public class Caller
        {
            public async IAsyncEnumerable<string> RunAsync(RespireClient client, RespireResult existing, int choice)
            {
                var {|RESP001:result|} = choice switch
                {
                    0 => await client.ExecuteAsync("PING"),
                    _ => existing,
                };
                yield return "before";
                if (choice == 0) result.Dispose();
            }
        }
        """);

}
