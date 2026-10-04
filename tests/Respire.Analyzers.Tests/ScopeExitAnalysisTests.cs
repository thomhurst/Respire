using TUnit.Core;
using Verify = Respire.Analyzers.Tests.AnalyzerVerifier<Respire.Analyzers.PendingReadBeforeFlushAnalyzer>;
using VerifyDisposal = Respire.Analyzers.Tests.AnalyzerVerifier<Respire.Analyzers.UndisposedPooledResultAnalyzer>;

namespace Respire.Analyzers.Tests;

// Exercise the shared exit component through the analyzer's observable diagnostics.
public class ScopeExitAnalysisTests
{
    [Test]
    [Arguments("T", "where T : Exception, IMarker", true)]
    [Arguments("T", "where T : InvalidOperationException, IMarker", true)]
    [Arguments("T", "where T : ArgumentException, IMarker", false)]
    [Arguments("T, U", "where T : U where U : Exception, IMarker", true)]
    [Arguments("T, U", "where T : U where U : ArgumentException, IMarker", false)]
    public async Task GenericInterfaceConstraintsPreservePossibleCatch(string parameters, string constraints, bool warning)
    {
        await VerifyDisposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            interface IMarker { }
            class Caller
            {
                async Task Run<{{parameters}}>(RespireClient client, T error) {{constraints}}
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { throw error; }
                    catch (InvalidOperationException) { }
                    catch (Exception) { result.Dispose(); }
                }
            }
            """);
        await Verify.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            interface IMarker { }
            class Caller
            {
                async Task Run<{{parameters}}>(RespireClient client, T error) {{constraints}}
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { throw error; }
                    catch (InvalidOperationException) { }
                    catch (Exception) { await batch.SendAsync(); }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

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

    [Test]
    [Arguments("", "throw null", "catch (NullReferenceException) { }", false)]
    [Arguments("", "throw (Exception)null", "catch (NullReferenceException) { }", false)]
    [Arguments("", "throw default(InvalidOperationException)", "catch (NullReferenceException) { }", false)]
    [Arguments("", "throw null", "catch (InvalidOperationException) { }", true)]
    [Arguments("", "throw (InvalidOperationException)null", "catch (InvalidOperationException) { }", true)]
    [Arguments("var error = new InvalidOperationException();", "throw error", "catch (InvalidOperationException) { }", false)]
    [Arguments("Exception error = new InvalidOperationException();", "throw error", "catch (InvalidOperationException) { }", false)]
    [Arguments("var error = new InvalidOperationException(); error = null;", "throw error", "catch (InvalidOperationException) { }", true)]
    [Arguments("var error = new InvalidOperationException(); Reset(ref error);", "throw error", "catch (InvalidOperationException) { }", true)]
    [Arguments("var error = new InvalidOperationException(); Action reset = () => error = null; reset();", "throw error", "catch (InvalidOperationException) { }", true)]
    [Arguments("var error = skip ? new InvalidOperationException() : null;", "throw error", "catch (InvalidOperationException) { }", false)]
    [Arguments("var error = skip ? null : new InvalidOperationException();", "throw error", "catch (InvalidOperationException) { }", true)]
    [Arguments("var error = new InvalidOperationException();", "throw error", "catch (ArgumentException) { }", true)]
    public async Task ExactNullOrUnreassignedThrownValueSelectsReleaseHandler(
        string setup, string thrown, string handler, bool warning)
    {
        var result = warning ? "{|RESP001:result|}" : "result";
        await VerifyDisposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            public class Caller
            {
                private static void Reset<T>(ref T value) where T : class => value = null;

                public async Task RunAsync(RespireClient client, RespireResult existing, int choice, bool skip)
                {
                    {{setup}}
                    var {{result}} = choice switch { 0 => await client.ExecuteAsync("PING"), _ => existing };
                    try { if (skip) {{thrown}}; }
                    {{handler}}
                    if (choice == 0) result.Dispose();
                }
            }
            """);
    }

    [Test]
    [Arguments("", "null", false)]
    [Arguments("", "(Exception)null", false)]
    [Arguments("", "error", true)]
    [Arguments("var local = new ArgumentException(\"x\");", "local", false)]
    [Arguments("Exception local = new ArgumentException(\"x\");", "local", false)]
    // Both the declared exception type and the null-throw path are handled by a flush.
    [Arguments("var local = new ArgumentException(\"x\"); if (skip) local = null;", "local", false)]
    [Arguments("var local = new ArgumentException(\"x\"); var (copy, other) = (local, local); (local, other) = (null, copy);", "local", false)]
    public async Task ExactNullOrUnreassignedThrownValueSelectsFinallyHandler(string setup, string thrown, bool warning)
    {
        var read = warning ? "{|RESP002:pending.Result|}" : "pending.Result";
        await Verify.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            public class Caller
            {
                public async Task RunAsync(RespireClient client, bool skip, Exception error)
                {
                    {{setup}}
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { throw {{thrown}}; }
                    catch (NullReferenceException) { await batch.SendAsync(); }
                    catch (ArgumentException) { await batch.SendAsync(); }
                    catch (InvalidOperationException) { }
                    finally { Console.WriteLine({{read}}); }
                }
            }
            """);
    }

    [Test]
    [Arguments("catch (InvalidOperationException) { await batch.SendAsync(); } catch (ArgumentException) { }", true)]
    [Arguments("catch (InvalidOperationException) { await batch.SendAsync(); }", true)]
    [Arguments("catch (Exception) { await batch.SendAsync(); }", false)]
    [Arguments("catch { await batch.SendAsync(); }", false)]
    public async Task GenericConstructionKeepsLaterCatchesReachable(string handlers, bool warning)
    {
        // new T() runs T's constructor, which can raise a different exception first.
        var read = warning ? "{|RESP002:pending.Result|}" : "pending.Result";
        await Verify.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            public class Caller
            {
                public async Task RunAsync<T>(RespireClient client) where T : InvalidOperationException, new()
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { throw new T(); }
                    {{handlers}}
                    finally { Console.WriteLine({{read}}); }
                }
            }
            """);
    }

}
