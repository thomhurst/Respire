using TUnit.Core;
using Verify = Respire.Analyzers.Tests.AnalyzerVerifier<Respire.Analyzers.PendingReadBeforeFlushAnalyzer>;

namespace Respire.Analyzers.Tests;

public class PendingReadBeforeFlushAnalyzerTests
{
    [Test]
    [Arguments("class", false)]
    [Arguments("class", true)]
    [Arguments("struct", false)]
    [Arguments("struct", true)]
    public async Task UserDefinedConversionCannotProveConditionalReturnNonNull(string kind, bool assignment)
    {
        var initializer = assignment
            ? "Produce(new Wrapper()); pending ??= batch.GetStringAsync(\"key\")"
            : "Produce(new Wrapper()) ?? batch.GetStringAsync(\"key\")";
        await Verify.VerifyAsync($$"""
            #nullable enable
            using System;
            using System.Diagnostics.CodeAnalysis;
            using Respire;
            public {{kind}} Wrapper
            {
                public static implicit operator string?(Wrapper value) => null;
            }
            public class Caller
            {
                [return: NotNullIfNotNull(nameof(value))]
                private static RespirePending<string> Produce(string? value) => null!;
                public void Run(RespireClient client)
                {
                    var batch = client.CreateBatch();
                    var pending = {{initializer}};
                    Console.WriteLine({|RESP002:pending.Result|});
                }
            }
            """);
    }

    [Test]
    [Arguments("new int?()", true)]
    [Arguments("default(int?)", true)]
    [Arguments("default(string)!", true)]
    [Arguments("null", true)]
    [Arguments("\"value\"", false)]
    [Arguments("new object()", false)]
    [Arguments("42", false)]
    public async Task ConditionalReturnContractUsesActualArgumentNullness(string argument, bool warning)
    {
        var read = warning ? "{|RESP002:pending.Result|}" : "pending.Result";
        await Verify.VerifyAsync($$"""
            #nullable enable
            using System;
            using System.Diagnostics.CodeAnalysis;
            using Respire;
            public class Caller
            {
                [return: NotNullIfNotNull(nameof(value))]
                private static RespirePending<string> Produce(object? value) => throw new Exception();
                public void Run(RespireClient client)
                {
                    var batch = client.CreateBatch();
                    var pending = Produce({{argument}}) ?? batch.GetStringAsync("key");
                    Console.WriteLine({{read}});
                }
            }
            """);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ExceptionConstructorCanReachCorrelatedCatchExit(bool insideBranch)
    {
        var before = insideBranch ? "try { if (skip) throw new CustomException(); } catch (System.IO.IOException) { break; }"
            : "if (skip) throw new CustomException();";
        var outerCatch = insideBranch ? "catch (Exception) { throw; }" : "catch (System.IO.IOException) { }";
        await Verify.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            public class CustomException : Exception
            {
                public CustomException() { throw new System.IO.IOException(); }
            }
            public class Caller
            {
                public async Task RunAsync(RespireClient client, int choice, bool skip)
                {
                    var first = client.CreateBatch();
                    var second = client.CreateBatch();
                    var pending = choice switch { 0 => first.GetStringAsync("a"), _ => second.GetStringAsync("b") };
                    try
                    {
                        switch (choice)
                        {
                            case 0:
                                {{before}}
                                await first.SendAsync();
                                break;
                            default:
                                await second.SendAsync();
                                break;
                        }
                    }
                    {{outerCatch}}
                    Console.WriteLine({|RESP002:pending.Result|});
                }
            }
            """);
    }

    [Test]
    [Arguments("if (skip) throw new InvalidOperationException();", false)]
    [Arguments("if (skip) break;", true)]
    public async Task ExitInsideFinallyBeforeCorrelatedFlushDoesNotReachRead(string exit, bool warning)
    {
        var read = warning ? "{|RESP002:pending.Result|}" : "pending.Result";
        await Verify.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            public class Caller
            {
                public async Task RunAsync(RespireClient client, int choice, bool skip)
                {
                    try { Console.WriteLine("work"); }
                    finally
                    {
                        var first = client.CreateBatch();
                        var second = client.CreateBatch();
                        var pending = choice switch { 0 => first.GetStringAsync("a"), _ => second.GetStringAsync("b") };
                        switch (choice)
                        {
                            case 0:
                                {{exit}}
                                await first.SendAsync();
                                break;
                            default:
                                await second.SendAsync();
                                break;
                        }
                        Console.WriteLine({{read}});
                    }
                }
            }
            """);
    }

    [Test]
    [Arguments("InvalidOperationException", "new ArgumentException()", false)]
    [Arguments("ArgumentException", "new ArgumentException()", true)]
    [Arguments("Exception", "new ArgumentException()", true)]
    [Arguments("InvalidOperationException", "error", true)]
    [Arguments("T", "new ArgumentException()", true)]
    public async Task FilteredCatchApplicabilityBeforeFinallyRead(string catchType, string thrown, bool warning)
    {
        var read = warning ? "{|RESP002:pending.Result|}" : "pending.Result";
        await Verify.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            public class Caller
            {
                public async Task RunAsync<T>(RespireClient client, bool handle, Exception error) where T : Exception
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { throw {{thrown}}; }
                    catch ({{catchType}}) when (handle) { Console.WriteLine("unflushed"); }
                    catch (Exception) { await batch.SendAsync(); }
                    finally { Console.WriteLine({{read}}); }
                }
            }
            """);
    }

    [Test]
    [Arguments("new CustomException(Compute())", true)]
    [Arguments("new CustomException()", true)]
    [Arguments("new ArgumentException(Message())", true)]
    [Arguments("new ArgumentException(\"message\")", false)]
    public async Task OpaqueExceptionConstructionKeepsCatchesReachableBeforeFinallyRead(string thrown, bool warning)
    {
        // Constructor bodies and argument evaluation can raise a different exception
        // before the explicit throw, so only simple framework constructions are exact.
        var read = warning ? "{|RESP002:pending.Result|}" : "pending.Result";
        await Verify.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            public class CustomException : Exception
            {
                public CustomException() { }
                public CustomException(int value) : base(value.ToString()) { }
            }
            public class Caller
            {
                private static int Compute() => throw new InvalidOperationException();
                private static string Message() => throw new InvalidOperationException();

                public async Task RunAsync(RespireClient client)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { throw {{thrown}}; }
                    catch (InvalidOperationException) { return; }
                    catch (Exception) { await batch.SendAsync(); }
                    finally { Console.WriteLine({{read}}); }
                }
            }
            """);
    }

    [Test]
    [Arguments("T")]
    [Arguments("dynamic")]
    public async Task UnknownFilteredCatchRemainsPossibleBeforeFinallyRead(string exceptionType) => await Verify.VerifyAsync(
        $$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync<T>(RespireClient client, bool handle, {{exceptionType}} error) where T : Exception
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try { throw error; }
                catch (InvalidOperationException) when (handle) { Console.WriteLine("unflushed"); }
                catch (Exception) { await batch.SendAsync(); }
                finally { Console.WriteLine({|RESP002:pending.Result|}); }
            }
        }
        """);

    [Test]
    public async Task ContinueWithoutReplacingPendingCanReachUnflushedRead() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool choice, bool skip)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice ? first.GetStringAsync("a") : second.GetStringAsync("b");
                for (var index = 0; index < 2; index++)
                {
                    if (choice)
                    {
                        if (skip) { choice = false; continue; }
                        await first.SendAsync();
                    }
                    else await second.SendAsync();
                    Console.WriteLine({|RESP002:pending.Result|});
                }
            }
        }
        """);

    [Test]
    [Arguments("exception")]
    [Arguments("new T()")]
    public async Task UnknownRuntimeExceptionTypeKeepsCompatibleCatchReachable(string thrown) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync<T>(RespireClient client, bool choice, bool skip, Exception exception)
                where T : Exception, new()
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice ? first.GetStringAsync("a") : second.GetStringAsync("b");
                try
                {
                    if (choice)
                    {
                        if (skip) throw {{{thrown}}};
                        await first.SendAsync();
                    }
                    else await second.SendAsync();
                }
                catch (InvalidOperationException) { }
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    [Arguments("catch (ArgumentException) { }")]
    [Arguments("catch (InvalidOperationException) when (false) { }")]
    [Arguments("catch (InvalidOperationException) { return; } catch (Exception) { }")]
    public async Task InapplicableCatchCannotReachCorrelatedRead(string handlers) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool choice, bool skip)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice ? first.GetStringAsync("a") : second.GetStringAsync("b");
                try
                {
                    if (choice)
                    {
                        if (skip) throw new InvalidOperationException();
                        await first.SendAsync();
                    }
                    else await second.SendAsync();
                }
                {{{handlers}}}
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task LocalGotoConservativelyInvalidatesCorrelatedFlush() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool choice, bool skip)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice ? first.GetStringAsync("a") : second.GetStringAsync("b");
                if (choice)
                {
                    if (skip) { goto Resume; Resume:; }
                    await first.SendAsync();
                }
                else await second.SendAsync();
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task RethrowCaughtBeforeFinallyFlush_IsNotFlagged() => await Verify.VerifyAsync(
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
                try
                {
                    try { throw new InvalidOperationException(); }
                    catch (InvalidOperationException) { throw; }
                }
                catch (Exception) { await batch.SendAsync(); }
                finally { Console.WriteLine(pending.Result); }
            }
        }
        """);

    [Test]
    public async Task DerivedCatchBeforeFinallyRead_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, Exception error)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try { throw error; }
                catch (InvalidOperationException) { Console.WriteLine("unflushed"); }
                catch (Exception) { await batch.SendAsync(); }
                finally { Console.WriteLine({|RESP002:pending.Result|}); }
            }
        }
        """);

    [Test]
    public async Task FilterInsideFinally_DoesNotResumeBeforeCatchFlush() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool handle)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try { Console.WriteLine("work"); }
                finally
                {
                    try { throw new InvalidOperationException(); }
                    catch (Exception) when (handle) { await batch.SendAsync(); }
                    catch (Exception) { await batch.SendAsync(); }
                }
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task CaughtThrowInsideFinally_PreservesContinuation_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using Respire;
        public class Caller
        {
            public void Run(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try { Console.WriteLine("work"); }
                finally
                {
                    try { throw new InvalidOperationException(); }
                    catch (InvalidOperationException) { Console.WriteLine("caught"); }
                }
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task ReturnThroughFinally_DoesNotResumeAtFollowingRead() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool skip)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try
                {
                    if (skip) return;
                    await batch.SendAsync();
                }
                finally { Console.WriteLine("cleanup"); }
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task PendingCreatedInFinally_ReadAfterFinally_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using Respire;
        public class Caller
        {
            public void Run(RespireClient client)
            {
                var batch = client.CreateBatch();
                RespirePending<string> pending;
                try { Console.WriteLine("work"); }
                finally { pending = batch.GetStringAsync("key"); }
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task CaughtThrow_FlushBeforeFinallyRead_IsNotFlagged() => await Verify.VerifyAsync(
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
                try { throw new InvalidOperationException(); }
                catch (InvalidOperationException) { await batch.SendAsync(); }
                finally { Console.WriteLine(pending.Result); }
            }
        }
        """);

    [Test]
    public async Task CorrelatedFlush_ReadInFinally_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool choice)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice ? first.GetStringAsync("a") : second.GetStringAsync("b");
                try
                {
                    if (choice) await first.SendAsync();
                    else await second.SendAsync();
                }
                finally { Console.WriteLine(pending.Result); }
            }
        }
        """);

    [Test]
    [Arguments("return;")]
    [Arguments("throw new InvalidOperationException();")]
    public async Task EarlyExitBeforeFlush_ReadInFinally_IsFlagged(string exit) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool skip)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try
                {
                    if (skip) { {{{exit}}} }
                    await batch.SendAsync();
                }
                finally
                {
                    Console.WriteLine({|RESP002:pending.Result|});
                }
            }
        }
        """);

    [Test]
    [Arguments("return;")]
    [Arguments("throw new InvalidOperationException();")]
    public async Task CorrelatedFlushBypassed_ReadInFinally_IsFlagged(string exit) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool choice, bool skip)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice ? first.GetStringAsync("a") : second.GetStringAsync("b");
                try
                {
                    if (choice)
                    {
                        if (skip) { {{{exit}}} }
                        await first.SendAsync();
                    }
                    else await second.SendAsync();
                }
                finally
                {
                    Console.WriteLine({|RESP002:pending.Result|});
                }
            }
        }
        """);

    [Test]
    [Arguments("return;")]
    [Arguments("throw new InvalidOperationException();")]
    public async Task FlushBeforeEarlyExit_ReadInFinally_IsNotFlagged(string exit) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool skip)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try
                {
                    await batch.SendAsync();
                    if (skip) { {{{exit}}} }
                }
                finally
                {
                    Console.WriteLine(pending.Result);
                }
            }
        }
        """);

    [Test]
    public async Task FinallyFlush_BeforeFollowingRead_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool skip)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try { if (skip) return; }
                finally { await batch.SendAsync(); }
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    [Arguments("return;")]
    [Arguments("throw new InvalidOperationException();")]
    public async Task InnerFinallyFlush_BeforeOuterFinallyRead_IsNotFlagged(string exit) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool skip)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try
                {
                    try { if (skip) { {{{exit}}} } }
                    finally { await batch.SendAsync(); }
                }
                finally { Console.WriteLine(pending.Result); }
            }
        }
        """);

    [Test]
    [Arguments("return;")]
    [Arguments("throw;")]
    [Arguments("if (skip) return; else throw;")]
    public async Task TerminatingCatchCannotReachCorrelatedRead(string handler) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool choice, bool skip)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice ? first.GetStringAsync("a") : second.GetStringAsync("b");
                try
                {
                    if (choice)
                    {
                        if (skip) throw new InvalidOperationException();
                        await first.SendAsync();
                    }
                    else await second.SendAsync();
                }
                catch (InvalidOperationException) { {{{handler}}} }
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    [Arguments("return;")]
    [Arguments("throw new System.InvalidOperationException();")]
    public async Task TerminalExitBeforeCorrelatedSwitchFlushCannotReachRead(string exit) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, int choice, bool skip)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice switch { 0 => first.GetStringAsync("a"), _ => second.GetStringAsync("b") };
                switch (choice)
                {
                    case 0:
                        if (skip) {{{exit}}}
                        await first.SendAsync();
                        break;
                    default:
                        await second.SendAsync();
                        break;
                }
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task ReturnBeforeCorrelatedConditionalFlushCannotReachRead() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool choice, bool skip)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice ? first.GetStringAsync("a") : second.GetStringAsync("b");
                if (choice)
                {
                    if (skip) return;
                    await first.SendAsync();
                }
                else await second.SendAsync();
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    [Arguments("catch (InvalidOperationException) { }")]
    [Arguments("catch (InvalidOperationException) { if (skip) return; }")]
    [Arguments("catch (InvalidOperationException) when (skip) { return; } catch (Exception) { }")]
    public async Task CaughtThrowBeforeFlushStillReachesRead(string handlers) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool choice, bool skip)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice ? first.GetStringAsync("a") : second.GetStringAsync("b");
                try
                {
                    if (choice)
                    {
                        if (skip) throw new InvalidOperationException();
                        await first.SendAsync();
                    }
                    else await second.SendAsync();
                }
                {{{handlers}}}
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    [Arguments("var pending = MaybePending() ?? batch.GetStringAsync(\"key\");")]
    [Arguments("var pending = MaybePending(); pending ??= batch.GetStringAsync(\"key\");")]
    public async Task ObliviousProducerStillRequiresCoalescedBatchFlush(string declaration) => await Verify.VerifyAsync(
        $$$"""
        #nullable disable
        using System;
        using Respire;
        public class Caller
        {
            private static RespirePending<string> MaybePending() => null;
            public void Run(RespireClient client)
            {
                var batch = client.CreateBatch();
                {{{declaration}}}
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    [Arguments("yield return \"before\";")]
    [Arguments("if (skip) yield break;")]
    public async Task IteratorReadStillCrossesCorrelatedFlush(string suspension) => await Verify.VerifyAsync(
        $$$"""
        using System.Collections.Generic;
        using Respire;
        public class Caller
        {
            public async IAsyncEnumerable<string> RunAsync(RespireClient client, int choice, bool skip)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice switch
                {
                    0 => first.GetStringAsync("a"),
                    _ => second.GetStringAsync("b"),
                };
                switch (choice)
                {
                    case 0:
                        {{{suspension}}}
                        await first.SendAsync();
                        break;
                    default:
                        await second.SendAsync();
                        break;
                }
                yield return pending.Result;
            }
        }
        """);

    [Test]
    [Arguments("var pending = MaybePending() ?? batch.GetStringAsync(\"key\");")]
    [Arguments("var pending = MaybePending(); pending ??= batch.GetStringAsync(\"key\");")]
    public async Task NullableProducerStillRequiresCoalescedBatchFlush(string declaration) => await Verify.VerifyAsync(
        $$$"""
        #nullable enable
        using System;
        using Respire;
        public class Caller
        {
            private static RespirePending<string>? MaybePending() => null;
            public void Run(RespireClient client)
            {
                var batch = client.CreateBatch();
                {{{declaration}}}
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    [Arguments("[return: MaybeNull]", "enable", "Producers.MaybePending() ?? batch.GetStringAsync(\"key\")")]
    [Arguments("[return: MaybeNull]", "disable", "Producers.MaybePending() ?? batch.GetStringAsync(\"key\")")]
    [Arguments("[return: MaybeNull]", "enable", "Producers.MaybePending(); pending ??= batch.GetStringAsync(\"key\")")]
    [Arguments("[return: MaybeNull]", "disable", "Producers.MaybePending(); pending ??= batch.GetStringAsync(\"key\")")]
    [Arguments("[return: NotNullIfNotNull(nameof(fallback))]", "enable", "Producers.MaybePending() ?? batch.GetStringAsync(\"key\")")]
    [Arguments("[return: NotNullIfNotNull(nameof(fallback))]", "disable", "Producers.MaybePending() ?? batch.GetStringAsync(\"key\")")]
    [Arguments("[return: NotNullIfNotNull(nameof(fallback))]", "enable", "Producers.MaybePending(null) ?? batch.GetStringAsync(\"key\")")]
    public async Task MaybeNullProducerStillRequiresCoalescedBatchFlush(
        string returnContract, string callerContext, string initializer) => await Verify.VerifyAsync(
        $$$"""
        #nullable enable
        using System;
        using System.Diagnostics.CodeAnalysis;
        using Respire;
        public static class Producers
        {
            {{{returnContract}}}
            public static RespirePending<string> MaybePending(RespirePending<string>? fallback = null) => fallback!;
        }
        #nullable {{{callerContext}}}
        public class Caller
        {
            public void Run(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = {{{initializer}}};
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task NotNullIfNotNullProducerWithNonNullArgumentSkipsCoalescedBatch() => await Verify.VerifyAsync(
        """
        #nullable enable
        using System;
        using System.Diagnostics.CodeAnalysis;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            [return: NotNullIfNotNull(nameof(fallback))]
            private static RespirePending<string> Produce(RespirePending<string>? fallback) => fallback!;
            public async Task RunAsync(RespireClient client)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = Produce(first.GetStringAsync("a")) ?? second.GetStringAsync("b");
                await first.SendAsync();
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    [Arguments("[return: NotNull]", "Produce(first)")]
    [Arguments("[return: NotNullIfNotNull(nameof(fallback))]", "Produce(first, first.GetStringAsync(\"c\"))")]
    public async Task NullableProducerWithNotNullReturnContractSkipsCoalescedBatch(
        string returnContract, string call) => await Verify.VerifyAsync(
        $$$"""
        #nullable enable
        using System;
        using System.Diagnostics.CodeAnalysis;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            {{{returnContract}}}
            private static RespirePending<string>? Produce(RespireBatch batch, RespirePending<string>? fallback = null)
                => fallback ?? batch.GetStringAsync("a");
            public async Task RunAsync(RespireClient client)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = {{{call}}} ?? second.GetStringAsync("b");
                await first.SendAsync();
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task NullableProducerWithUnsatisfiedNotNullIfNotNullStillRequiresCoalescedBatchFlush() => await Verify.VerifyAsync(
        """
        #nullable enable
        using System;
        using System.Diagnostics.CodeAnalysis;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            [return: NotNullIfNotNull(nameof(fallback))]
            private static RespirePending<string>? Produce(RespirePending<string>? fallback) => fallback;
            public async Task RunAsync(RespireClient client)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = Produce(null) ?? second.GetStringAsync("b");
                await first.SendAsync();
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task NonNullProducerWithUnrelatedReturnAttributeSkipsCoalescedBatch() => await Verify.VerifyAsync(
        """
        #nullable enable
        using System;
        using System.Diagnostics.CodeAnalysis;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            [return: NotNull]
            private static RespirePending<string> Produce(RespireBatch batch) => batch.GetStringAsync("a");
            public async Task RunAsync(RespireClient client)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = Produce(first) ?? second.GetStringAsync("b");
                await first.SendAsync();
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    [Arguments("for (var index = 0; index < 2; index++) { if (skip) break; }")]
    [Arguments("for (var index = 0; index < 2; index++) { if (skip) continue; }")]
    [Arguments("while (skip) { break; }")]
    [Arguments("do { if (skip) continue; } while (false);")]
    [Arguments("foreach (var index in new[] { 1, 2 }) { if (skip) break; }")]
    [Arguments("switch (skip) { case true: break; default: break; }")]
    public async Task NestedExitDoesNotBypassCorrelatedSwitchFlush(string nested) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, int choice, bool skip)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice switch
                {
                    0 => first.GetStringAsync("a"),
                    _ => second.GetStringAsync("b"),
                };
                switch (choice)
                {
                    case 0:
                        {{{nested}}}
                        await first.SendAsync();
                        break;
                    default:
                        await second.SendAsync();
                        break;
                }
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    [Arguments("try { if (skip) throw new InvalidOperationException(); } catch (ArgumentException) { break; }")]
    [Arguments("try { if (skip) throw new InvalidOperationException(\"x\"); } catch (InvalidOperationException) { throw; } catch (Exception) { break; }")]
    [Arguments("try { try { if (skip) throw new InvalidOperationException(); } catch (InvalidOperationException) { } } catch { break; }")]
    [Arguments("try { try { if (skip) throw new InvalidOperationException(); } catch (ArgumentException) { } } catch (ArgumentException) { break; }")]
    [Arguments("try { try { if (skip) throw new InvalidOperationException(); } catch (InvalidOperationException) { throw new ArgumentException(); } } catch (InvalidOperationException) { break; }")]
    [Arguments("try { try { if (skip) throw null; } catch (NullReferenceException) { } } catch { break; }")]
    public async Task InapplicableHandlerExitDoesNotBypassCorrelatedSwitchFlush(string nested) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, int choice, bool skip)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice switch
                {
                    0 => first.GetStringAsync("a"),
                    _ => second.GetStringAsync("b"),
                };
                switch (choice)
                {
                    case 0:
                        {{{nested}}}
                        await first.SendAsync();
                        break;
                    default:
                        await second.SendAsync();
                        break;
                }
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    [Arguments("try { if (skip) throw new ArgumentException(); } catch (ArgumentException) { break; }")]
    [Arguments("try { if (skip) throw new InvalidOperationException(); } catch (ArgumentException) when (skip) { } catch (Exception) { break; }")]
    [Arguments("try { Console.WriteLine(); } catch (ArgumentException) { break; }")]
    [Arguments("try { try { if (skip) throw new InvalidOperationException(); } catch (ArgumentException) { } } catch { break; }")]
    [Arguments("try { try { if (skip) throw new InvalidOperationException(); } catch (InvalidOperationException) { throw; } } catch { break; }")]
    [Arguments("try { try { if (skip) throw new InvalidOperationException(); } catch (InvalidOperationException) { throw new ArgumentException(); } } catch (ArgumentException) { break; }")]
    [Arguments("try { try { if (skip) throw new InvalidOperationException(); } catch (InvalidOperationException) when (skip) { } } catch { break; }")]
    [Arguments("try { try { if (skip) throw new InvalidOperationException(); } finally { } } catch { break; }")]
    [Arguments("try { try { Console.WriteLine(); } catch (ArgumentException) { } } catch { break; }")]
    public async Task ApplicableHandlerExitCanBypassCorrelatedSwitchFlush(string nested) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, int choice, bool skip)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice switch
                {
                    0 => first.GetStringAsync("a"),
                    _ => second.GetStringAsync("b"),
                };
                switch (choice)
                {
                    case 0:
                        {{{nested}}}
                        await first.SendAsync();
                        break;
                    default:
                        await second.SendAsync();
                        break;
                }
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task ConditionalBreakCanBypassCorrelatedSwitchFlush() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client, int choice, bool skip)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice switch
                {
                    0 => first.GetStringAsync("a"),
                    _ => second.GetStringAsync("b"),
                };
                switch (choice)
                {
                    case 0:
                        if (skip) break;
                        await first.SendAsync();
                        break;
                    default:
                        await second.SendAsync();
                        break;
                }
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task NonNullCoalescingProducerDoesNotRequireTheUnreachableBatchFlush() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;
        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = first.GetStringAsync("a") ?? second.GetStringAsync("b");
                await first.SendAsync();
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    [Arguments("ExecuteAsync", "")]
    [Arguments("TryExecuteAsync", "")]
    [Arguments("ExecuteAndWaitForReplicationAsync", "1, TimeSpan.Zero")]
    [Arguments("ExecuteAndWaitForAofAsync", "true, 1, TimeSpan.Zero")]
    public async Task ExecuteThenRead_IsNotFlagged(string method, string arguments) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                var summary = await batch.{{{method}}}({{{arguments}}}).ConfigureAwait(false);
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    [Arguments("ExecuteAsync", "")]
    [Arguments("TryExecuteAsync", "")]
    [Arguments("ExecuteAndWaitForReplicationAsync", "1, TimeSpan.Zero")]
    [Arguments("ExecuteAndWaitForAofAsync", "true, 1, TimeSpan.Zero")]
    public async Task StoredExecuteThenRead_IsNotFlagged(string method, string arguments) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                var flush = batch.{{{method}}}({{{arguments}}});
                await flush;
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    [Arguments("ExecuteAsync", "")]
    [Arguments("TryExecuteAsync", "")]
    [Arguments("ExecuteAndWaitForReplicationAsync", "1, TimeSpan.Zero")]
    [Arguments("ExecuteAndWaitForAofAsync", "true, 1, TimeSpan.Zero")]
    public async Task UnawaitedExecuteThenRead_IsFlagged(string method, string arguments) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using Respire;

        public class Caller
        {
            public void Run(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                var flush = batch.{{{method}}}({{{arguments}}});
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    [Arguments("ExecuteAsync")]
    [Arguments("TryExecuteAsync")]
    [Arguments("ExecuteAndWaitForReplicationAsync")]
    [Arguments("ExecuteAndWaitForAofAsync")]
    public async Task SameNamedExecuteExtension_IsFlagged(string method) => await Verify.VerifyAsync(
        $$$"""
        using System;
        using System.Threading.Tasks;
        using Respire;

        public static class Extensions
        {
            public static ValueTask {{{method}}}(this RespireBatch batch, bool ignored) => default;
        }

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                await batch.{{{method}}}(false);
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

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
    public async Task WatchedTransactionResultReadBeforeCommit_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                await using var transaction = await client.CreateTransactionAsync("key");
                var pending = transaction.GetStringAsync("key");
                Console.WriteLine({|RESP002:pending.Result|});
                _ = await transaction.CommitAsync();
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
    public async Task SiblingDefaultFlushDefinition_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool condition)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                ValueTask flush;
                if (condition)
                {
                    flush = default;
                }
                else
                {
                    flush = batch.SendAsync();
                }

                await flush;
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task MatchingFlushDefinitionsInEveryBranch_AreNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool condition)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                ValueTask flush;
                if (condition)
                {
                    flush = batch.SendAsync();
                }
                else
                {
                    flush = batch.SendAsync();
                }

                await flush;
                Console.WriteLine(pending.Result);
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
    public async Task StoredWhenAllFlushBeforeRead_IsNotFlagged() => await Verify.VerifyAsync(
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
                var flush = Task.WhenAll(batch.SendAsync().AsTask());
                await flush;
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task StoredCollectionWhenAllFlushBeforeRead_IsNotFlagged() => await Verify.VerifyAsync(
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
                var flushes = new[] { batch.SendAsync().AsTask() };
                await Task.WhenAll(flushes);
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task AdditiveCollectionChangesPreserveWhenAllFlush_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                var flushes = new List<Task> { batch.SendAsync().AsTask() };
                flushes.Add(Task.CompletedTask);
                flushes.AddRange(new[] { Task.CompletedTask });
                await Task.WhenAll(flushes);
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task MutatedStoredCollectionWhenAllBeforeRead_IsFlagged() => await Verify.VerifyAsync(
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
                var flushes = new[] { batch.SendAsync().AsTask() };
                flushes[0] = Task.CompletedTask;
                await Task.WhenAll(flushes);
                Console.WriteLine({|RESP002:pending.Result|});
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
    public async Task WrappedBatchFlushBeforeRead_IsNotFlagged() => await Verify.VerifyAsync(
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
                await (batch!).SendAsync();
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
    public async Task SiblingBranchBatchAssignmentsThenFlush_AreNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool firstPath)
            {
                RespireBatch batch;
                RespirePending<string> pending;
                if (firstPath)
                {
                    batch = client.CreateBatch();
                    pending = batch.GetStringAsync("first");
                }
                else
                {
                    batch = client.CreateBatch();
                    pending = batch.GetStringAsync("second");
                }

                await batch.SendAsync();
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

    [Test]
    public async Task ConditionalPendingReadBeforeSend_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool condition)
            {
                var batch = client.CreateBatch();
                Console.WriteLine({|RESP002:(condition
                    ? batch.GetStringAsync("a")
                    : batch.GetStringAsync("b")).Result|});
                await batch.SendAsync();
            }
        }
        """);

    [Test]
    public async Task CoalescingPendingReadBeforeSend_IsFlagged() => await Verify.VerifyAsync(
        """
        #nullable enable
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, RespirePending<string>? fallback)
            {
                var batch = client.CreateBatch();
                Console.WriteLine({|RESP002:(fallback ?? batch.GetStringAsync("key")).Result|});
                await batch.SendAsync();
            }
        }
        """);

    [Test]
    public async Task BranchSelectedPendingWithMatchingFlush_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool condition)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = condition
                    ? first.GetStringAsync("a")
                    : second.GetStringAsync("b");

                if (condition)
                {
                    await first.SendAsync();
                }
                else
                {
                    await second.SendAsync();
                }

                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task ConditionalFlushInsideSelectedBranch_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool condition, bool flushNow)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = condition
                    ? first.GetStringAsync("a")
                    : second.GetStringAsync("b");

                if (condition)
                {
                    if (flushNow)
                    {
                        await first.SendAsync();
                    }
                }
                else
                {
                    await second.SendAsync();
                }

                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task MatchingFlushInsideOuterGuard_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool condition, bool flushNow)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = condition
                    ? first.GetStringAsync("a")
                    : second.GetStringAsync("b");

                if (flushNow)
                {
                    if (condition)
                    {
                        await first.SendAsync();
                    }
                    else
                    {
                        await second.SendAsync();
                    }
                }

                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task PendingAliasReadBeforeSend_IsFlagged() => await Verify.VerifyAsync(
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
                var alias = pending;
                Console.WriteLine({|RESP002:alias.Result|});
            }
        }
        """);

    [Test]
    public async Task ReassignedConditionInvalidatesBranchCorrelation() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, bool condition)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = condition
                    ? first.GetStringAsync("a")
                    : second.GetStringAsync("b");
                condition = !condition;

                if (condition)
                {
                    await first.SendAsync();
                }
                else
                {
                    await second.SendAsync();
                }

                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task SwitchSelectedPendingWithMatchingFlush_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, int choice)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice switch
                {
                    0 => first.GetStringAsync("a"),
                    _ => second.GetStringAsync("b"),
                };

                switch (choice)
                {
                    case 0:
                        await first.SendAsync();
                        break;
                    default:
                        await second.SendAsync();
                        break;
                }

                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task EarlierOverlappingSwitchCaseDoesNotCountAsCorrelatedFlush() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, int choice)
            {
                var first = client.CreateBatch();
                var second = client.CreateBatch();
                var pending = choice switch
                {
                    > 0 => first.GetStringAsync("a"),
                    _ => second.GetStringAsync("b"),
                };

                switch (choice)
                {
                    case > 5:
                        await second.SendAsync();
                        break;
                    case > 0:
                        await first.SendAsync();
                        break;
                    default:
                        await second.SendAsync();
                        break;
                }

                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task MutatedListWhenAllBeforeRead_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                var flushes = new List<Task> { batch.SendAsync().AsTask() };
                flushes.Clear();
                flushes.Add(Task.CompletedTask);
                await Task.WhenAll(flushes);
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task SameNamedExtensionDoesNotCountAsFlush() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public static class Extensions
        {
            public static ValueTask SendAsync(this RespireBatch batch, bool ignored) => default;
        }

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                await batch.SendAsync(false);
                Console.WriteLine({|RESP002:pending.Result|});
            }
        }
        """);

    [Test]
    public async Task SwitchPendingReadBeforeSend_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client, int choice)
            {
                var batch = client.CreateBatch();
                Console.WriteLine({|RESP002:(choice switch
                {
                    0 => batch.GetStringAsync("a"),
                    _ => batch.GetStringAsync("b"),
                }).Result|});
                await batch.SendAsync();
            }
        }
        """);

    [Test]
    public async Task CoalesceAssignedPendingBeforeSend_IsFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch = client.CreateBatch();
                RespirePending<string>? pending = null;
                pending ??= batch.GetStringAsync("key");
                Console.WriteLine({|RESP002:pending.Result|});
                await batch.SendAsync();
            }
        }
        """);

    [Test]
    public async Task SkippedCoalesceAssignmentPreservesEarlierPendingOrigin() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch1 = client.CreateBatch();
                var batch2 = client.CreateBatch();
                RespirePending<string>? pending = batch1.GetStringAsync("first");
                pending ??= batch2.GetStringAsync("second");
                await batch2.SendAsync();
                Console.WriteLine({|RESP002:pending.Result|});
                await batch1.SendAsync();
            }
        }
        """);

    [Test]
    public async Task ImpossibleCoalesceAssignmentDoesNotAddPendingOrigin() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch1 = client.CreateBatch();
                var batch2 = client.CreateBatch();
                RespirePending<string>? pending = batch1.GetStringAsync("first");
                await batch1.SendAsync();
                pending ??= batch2.GetStringAsync("second");
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task LaterCoalesceAssignmentIsImpossibleAfterSuccessfulCoalesce() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public async Task RunAsync(RespireClient client)
            {
                var batch1 = client.CreateBatch();
                var batch2 = client.CreateBatch();
                RespirePending<string>? pending = null;
                pending ??= batch1.GetStringAsync("first");
                await batch1.SendAsync();
                pending ??= batch2.GetStringAsync("second");
                Console.WriteLine(pending.Result);
            }
        }
        """);

    [Test]
    public async Task SynchronousGetResultSendBeforeRead_IsNotFlagged() => await Verify.VerifyAsync(
        """
        using System;
        using System.Threading.Tasks;
        using Respire;

        public class Caller
        {
            public void Run(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                batch.SendAsync().AsTask().GetAwaiter().GetResult();
                Console.WriteLine(pending.Result);
            }
        }
        """);
}
