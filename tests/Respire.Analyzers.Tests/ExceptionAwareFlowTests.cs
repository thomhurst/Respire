using TUnit.Core;
using Pending = Respire.Analyzers.Tests.AnalyzerVerifier<Respire.Analyzers.PendingReadBeforeFlushAnalyzer>;
using Disposal = Respire.Analyzers.Tests.AnalyzerVerifier<Respire.Analyzers.UndisposedPooledResultAnalyzer>;

namespace Respire.Analyzers.Tests;

public class ExceptionAwareFlowTests
{
    [Test]
    [Arguments("_ = $\"{value}{flag = false}\";", false)]
    [Arguments("_ = $\"{flag = false}{value}\";", true)]
    [Arguments("flag = false; _ = new Token();", false)]
    [Arguments("flag = false; _ = new ThrowingToken();", true)]
    public async Task FormattingAndValueConstructionRespectExecutionOrder(string expression, bool warning)
    {
        const string types = """
            struct Token { }
            struct ThrowingToken { public ThrowingToken() { throw new System.InvalidOperationException(); } }
            class Value { public override string ToString() => throw new System.InvalidOperationException(); }
            """;
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{types}}
            class Caller
            {
                async Task Run(RespireClient client, RespireResult existing, bool flag, Value value)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = flag ? await client.ExecuteAsync("PING") : existing;
                    try { {{expression}} result.Dispose(); }
                    catch (InvalidOperationException) { if (flag) result.Dispose(); }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{types}}
            class Caller
            {
                async Task Run(RespireClient client, RespirePending<string> existing, bool flag, Value value)
                {
                    var batch = client.CreateBatch();
                    var pending = flag ? batch.GetStringAsync("key") : existing;
                    try { {{expression}} await batch.SendAsync(); }
                    catch (InvalidOperationException) { if (flag) await batch.SendAsync(); }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("value == null", true)]
    [Arguments("value != null", true)]
    [Arguments("value > 0", true)]
    [Arguments("value is null", false)]
    public async Task DynamicComparisonsCannotProveRepeatedSelection(string condition, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, RespireResult existing, dynamic value)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = {{condition}} ? await client.ExecuteAsync("PING") : existing;
                    if ({{condition}}) result.Dispose();
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, RespirePending<string> existing, dynamic value)
                {
                    var batch = client.CreateBatch();
                    var pending = {{condition}} ? batch.GetStringAsync("key") : existing;
                    if ({{condition}}) await batch.SendAsync();
                    System.Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("Take(result, Throws());", false)]
    [Arguments("Take(result, choice ? Throws() : 0);", false)]
    [Arguments("_ = new Owner(result, Throws());", false)]
    [Arguments("Take(result, Throws());", true)]
    [Arguments("Take(result, 0);", false, false)]
    [Arguments("holder.Take(result, 0);", false)]
    [Arguments("target.Take(result, 0);", false)]
    [Arguments("holder.Take(result, 0);", true)]
    [Arguments("_ = new Owner(result, 0) { Property = Throws() };", false, false)]
    [Arguments("Owner owner = new(result, 0) { Property = Throws() };", false, false)]
    [Arguments("_ = new Owner(result, Throws()) { Property = 0 };", false)]
    public async Task OwnershipTransferWaitsForArguments(string transfer, bool cleanupInCatch, bool warning = true)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Owner { public Owner(RespireResult result, int value) { result.Dispose(); } public int Property { get; set; } public void Take(RespireResult result, int value) { result.Dispose(); } }
            class Caller
            {
                int Throws() => throw new InvalidOperationException();
                void Take(RespireResult result, int value) { result.Dispose(); }
                async Task Run(RespireClient client, bool choice, Owner holder, dynamic target)
                {
                    var {{(warning && !cleanupInCatch ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { {{transfer}} }
                    catch { {{(cleanupInCatch ? "result.Dispose();" : "")}} }
                }
            }
            """);
    }

    [Test]
    [Arguments("_ = \"prefix\" + holder;", true)]
    [Arguments("text += holder;", true)]
    [Arguments("_ = $\"{holder}\";", true)]
    [Arguments("_ = $\"prefix{holder,10}\";", true)]
    [Arguments("_ = text + text;", false)]
    [Arguments("text += text;", false)]
    [Arguments("_ = \"prefix\" + \"suffix\";", false)]
    public async Task StringConcatenationCanInvokeUserCode(string expression, bool warning)
    {
        const string holder = "class Holder { public override string ToString() => throw new System.InvalidOperationException(); }";
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{holder}}
            class Caller
            {
                async Task Run(RespireClient client, Holder holder, string text)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { {{expression}} result.Dispose(); }
                    catch (InvalidOperationException) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{holder}}
            class Caller
            {
                async Task Run(RespireClient client, Holder holder, string text)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{expression}} await batch.SendAsync(); }
                    catch (InvalidOperationException) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("object boxed = value;", "OutOfMemoryException", true)]
    [Arguments("_ = $\"value: {value}\";", "InvalidOperationException", false)]
    [Arguments("_ = $\"value: {value}\";", "OutOfMemoryException", true)]
    [Arguments("_ = $\"value: {value:Q}\";", "FormatException", true)]
    [Arguments("object boxed = value;", "InvalidOperationException", false)]
    [Arguments("object boxed = 1;", "OutOfMemoryException", true)]
    [Arguments("object boxed = (int?)null;", "OutOfMemoryException", false)]
    [Arguments("object boxed = default(int?);", "OutOfMemoryException", false)]
    [Arguments("object boxed = (int?)value;", "OutOfMemoryException", true)]
    [Arguments("_ = new { Value = 1 };", "InvalidOperationException", false)]
    [Arguments("_ = new { Value = 1 };", "OutOfMemoryException", true)]
    [Arguments("_ = new { Value = Throw() };", "InvalidOperationException", true)]
    public async Task BoxingAndAnonymousObjectsHaveAllocationExceptions(string expression, string catchType, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                int Throw() => throw new InvalidOperationException();
                async Task Run(RespireClient client, int value)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { {{expression}} result.Dispose(); }
                    catch ({{catchType}}) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                int Throw() => throw new InvalidOperationException();
                async Task Run(RespireClient client, int value)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{expression}} await batch.SendAsync(); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("Exception", false)]
    [Arguments("InvalidOperationException", false)]
    [Arguments("ArgumentException", false)]
    [Arguments("Exception", true)]
    public async Task ExceptionConstructionCanFailBeforeExplicitThrow(string exceptionType, bool cleanupInCatch)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client)
                {
                    var {{(cleanupInCatch ? "result" : "{|RESP001:result|}")}} = await client.ExecuteAsync("PING");
                    try { throw new {{exceptionType}}(); }
                    catch (OutOfMemoryException) { {{(cleanupInCatch ? "result.Dispose();" : "")}} }
                    catch (Exception) { result.Dispose(); }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { throw new {{exceptionType}}(); }
                    catch (OutOfMemoryException) { {{(cleanupInCatch ? "await batch.SendAsync();" : "")}} }
                    catch (Exception) { await batch.SendAsync(); }
                    Console.WriteLine({{(cleanupInCatch ? "pending.Result" : "{|RESP002:pending.Result|}")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("target.Mutate(ref choice)", true)]
    [Arguments("target.Mutate(out choice)", true)]
    [Arguments("target.Mutate(choice)", false)]
    [Arguments("new Holder(target, ref choice)", true)]
    public async Task DynamicReferenceArgumentsInvalidatePredicates(string call, bool warning)
    {
        const string holder = "class Holder { public Holder(object target, ref bool choice) { choice = false; } }";
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            {{holder}}
            class Caller
            {
                async Task Run(RespireClient client, RespireResult existing, bool choice, dynamic target)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = choice ? await client.ExecuteAsync("PING") : existing;
                    {{call}};
                    if (choice) result.Dispose();
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            {{holder}}
            class Caller
            {
                async Task Run(RespireClient client, RespirePending<string> existing, bool choice, dynamic target)
                {
                    var batch = client.CreateBatch();
                    var pending = choice ? batch.GetStringAsync("key") : existing;
                    {{call}};
                    if (choice) await batch.SendAsync();
                    System.Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("[1, 2]", "InvalidOperationException", false)]
    [Arguments("[1, 2]", "OutOfMemoryException", true)]
    [Arguments("[Throw()]", "InvalidOperationException", true)]
    [Arguments("[..source]", "InvalidOperationException", true)]
    public async Task ArrayCollectionExceptionsRespectCatchType(string expression, string catchType, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                int Throw() => throw new InvalidOperationException();
                async Task Run(RespireClient client, IEnumerable<int> source)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { int[] copy = {{expression}}; result.Dispose(); }
                    catch ({{catchType}}) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                int Throw() => throw new InvalidOperationException();
                async Task Run(RespireClient client, IEnumerable<int> source)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { int[] copy = {{expression}}; await batch.SendAsync(); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("int[] copy = [..source];", false)]
    [Arguments("int[] copy = [1, 2];", false)]
    [Arguments("System.Collections.Generic.List<int> copy = [..source];", false)]
    [Arguments("int[] copy = [..source];", true)]
    public async Task CollectionExpressionsCanBypassCleanup(string expression, bool cleanupInCatch)
    {
        await Disposal.VerifyAsync($$"""
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, IEnumerable<int> source)
                {
                    var {{(cleanupInCatch ? "result" : "{|RESP001:result|}")}} = await client.ExecuteAsync("PING");
                    try { {{expression}} result.Dispose(); }
                    catch { {{(cleanupInCatch ? "result.Dispose();" : "")}} }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, IEnumerable<int> source)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{expression}} await batch.SendAsync(); }
                    catch { {{(cleanupInCatch ? "await batch.SendAsync();" : "")}} }
                    System.Console.WriteLine({{(cleanupInCatch ? "pending.Result" : "{|RESP002:pending.Result|}")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("holder is (0, 0)", false, true)]
    [Arguments("holder is [0]", false, true)]
    [Arguments("holder is [.. var rest]", false, true)]
    [Arguments("holder is [0]", true, false)]
    [Arguments("array is [0]", false, false)]
    [Arguments("text is ['a']", false, false)]
    public async Task PatternCallsCanBypassCleanup(string pattern, bool cleanupInCatch, bool warning)
    {
        const string holder = """
            class Holder
            {
                public int Length => throw new System.Exception();
                public int this[int index] => throw new System.Exception();
                public Holder Slice(int start, int length) => throw new System.Exception();
                public void Deconstruct(out int first, out int second) => throw new System.Exception();
            }
            """;
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            {{holder}}
            class Caller
            {
                async Task Run(RespireClient client, Holder holder, int[] array, string text)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { _ = {{pattern}}; result.Dispose(); }
                    catch { {{(cleanupInCatch ? "result.Dispose();" : "")}} }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            {{holder}}
            class Caller
            {
                async Task Run(RespireClient client, Holder holder, int[] array, string text)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { _ = {{pattern}}; await batch.SendAsync(); }
                    catch { {{(cleanupInCatch ? "await batch.SendAsync();" : "")}} }
                    System.Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LoopCarriedPendingRetainsStableSelection(bool wrongBatch)
    {
        await Pending.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, bool choice, RespirePending<string> existing)
                {
                    var first = client.CreateBatch();
                    var second = client.CreateBatch();
                    var pending = existing;
                    while (true)
                    {
                        if (choice) await {{(wrongBatch ? "second" : "first")}}.SendAsync();
                        else await second.SendAsync();
                        System.Console.WriteLine({{(wrongBatch ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                        pending = choice ? first.GetStringAsync("a") : second.GetStringAsync("b");
                    }
                }
            }
            """);
    }

    [Test]
    [Arguments("holder.Method", false, true)]
    [Arguments("holder.Method", true, false)]
    [Arguments("holder.Method", false, false, "InvalidOperationException")]
    [Arguments("holder.Method", false, true, "OutOfMemoryException")]
    [Arguments("Holder.StaticMethod", false, false)]
    [Arguments("this.Method", false, false)]
    [Arguments("Holder.StaticMethod", false, true, "OutOfMemoryException")]
    [Arguments("() => holder.Method()", false, true, "OutOfMemoryException")]
    public async Task MethodGroupCreationCanBypassCleanup(string methodGroup, bool cleanupInCatch, bool warning,
        string catchType = "NullReferenceException")
    {
        const string holder = "class Holder { public void Method() { } public static void StaticMethod() { } }";
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{holder}}
            class Caller
            {
                void Method() { }
                async Task Run(RespireClient client, Holder holder)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { Action action = {{methodGroup}}; result.Dispose(); }
                    catch ({{catchType}}) { {{(cleanupInCatch ? "result.Dispose();" : "")}} }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{holder}}
            class Caller
            {
                void Method() { }
                async Task Run(RespireClient client, Holder holder)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { Action action = {{methodGroup}}; await batch.SendAsync(); }
                    catch ({{catchType}}) { {{(cleanupInCatch ? "await batch.SendAsync();" : "")}} }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RecordCopyCanBypassCleanup(bool cleanupInCatch)
    {
        const string record = """
            record Holder
            {
                public Holder() { }
                protected Holder(Holder other) { throw new System.Exception(); }
            }
            """;
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            {{record}}
            class Caller
            {
                async Task Run(RespireClient client, Holder value)
                {
                    var {{(cleanupInCatch ? "result" : "{|RESP001:result|}")}} = await client.ExecuteAsync("PING");
                    try { _ = value with { }; result.Dispose(); }
                    catch { {{(cleanupInCatch ? "result.Dispose();" : "")}} }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            {{record}}
            class Caller
            {
                async Task Run(RespireClient client, Holder value)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { _ = value with { }; await batch.SendAsync(); }
                    catch { {{(cleanupInCatch ? "await batch.SendAsync();" : "")}} }
                    System.Console.WriteLine({{(cleanupInCatch ? "pending.Result" : "{|RESP002:pending.Result|}")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("static Holder() { throw new System.Exception(); }", true)]
    [Arguments("static int Other = Throw(); static int Throw() => throw new System.Exception();", true)]
    [Arguments("", false)]
    public async Task StaticFieldAccessCanTriggerTypeInitializer(string constructor, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Holder { public static int Value; {{constructor}} }
            class Caller
            {
                async Task Run(RespireClient client)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { _ = Holder.Value; result.Dispose(); }
                    catch { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Holder { public static int Value; {{constructor}} }
            class Caller
            {
                async Task Run(RespireClient client)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { _ = Holder.Value; await batch.SendAsync(); }
                    catch { }
                    System.Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("value is not null", "value is not null", "", false)]
    [Arguments("value is { }", "value is { }", "", false)]
    [Arguments("value is { }", "value != null", "", false)]
    [Arguments("value is not { }", "value is null", "", false)]
    [Arguments("value is { }", "value is { }", "value = null;", true)]
    [Arguments("value is not null", "value != null", "", false)]
    [Arguments("value is not null", "value is null", "", true)]
    [Arguments("value is string", "value is string", "", false)]
    [Arguments("value is string", "value is string text", "", false)]
    [Arguments("value is not string", "value is not string", "", false)]
    [Arguments("value is string", "value is int", "", true)]
    [Arguments("value is string", "value is string", "value = new object();", true)]
    [Arguments("choice", "choice", "buffer[choice ? 0 : 1] = 1;", false)]
    [Arguments("choice", "choice", "buffer[(choice = false) ? 0 : 1] = 1;", true)]
    public async Task StablePatternAndIndexPredicatesRemainCorrelated(string selection, string cleanup, string write, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, RespireResult existing, object value, bool choice, int[] buffer)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = {{selection}} ? await client.ExecuteAsync("PING") : existing;
                    {{write}}
                    if ({{cleanup}}) result.Dispose();
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, object value, bool choice, int[] buffer)
                {
                    var first = client.CreateBatch();
                    var second = client.CreateBatch();
                    var pending = {{selection}} ? first.GetStringAsync("a") : second.GetStringAsync("b");
                    {{write}}
                    if ({{cleanup}}) await first.SendAsync(); else await second.SendAsync();
                    System.Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("value.Missing", false)]
    [Arguments("value[0]", false)]
    [Arguments("value + value", false)]
    [Arguments("-value", false)]
    [Arguments("!value", false)]
    [Arguments("value++", false)]
    [Arguments("value += 1", false)]
    [Arguments("value.Missing", true)]
    public async Task DynamicAccessCanBypassCleanup(string access, bool cleanupInCatch)
    {
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, dynamic value)
                {
                    var {{(cleanupInCatch ? "result" : "{|RESP001:result|}")}} = await client.ExecuteAsync("PING");
                    try { _ = {{access}}; result.Dispose(); }
                    catch { {{(cleanupInCatch ? "result.Dispose();" : "")}} }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, dynamic value)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { _ = {{access}}; await batch.SendAsync(); }
                    catch { {{(cleanupInCatch ? "await batch.SendAsync();" : "")}} }
                    System.Console.WriteLine({{(cleanupInCatch ? "pending.Result" : "{|RESP002:pending.Result|}")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("new Holder { Value = (flag = false) }", false)]
    [Arguments("new Holder { Property = (flag = false) }", true)]
    [Arguments("new Holder(flag = false) { Value = true }", true)]
    [Arguments("new bool[] { flag = false }", false)]
    [Arguments("new bool[] { flag = false, Throws() }", true)]
    [Arguments("new Holder() with { Value = (flag = false) }", false)]
    [Arguments("(bool[])[flag = false]", false)]
    [Arguments("(bool[])[flag = false, Throws()]", true)]
    public async Task ConstructionExceptionsPrecedeInitializers(string creation, bool warning)
    {
        const string holder = """
            record Holder
            {
                public Holder() { }
                public Holder(bool value) { }
                public bool Value;
                public bool Property { set => throw new System.Exception(); }
            }
            """;
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            {{holder}}
            class Caller
            {
                bool Throws() => throw new System.Exception();
                async Task Run(RespireClient client, RespireResult existing, bool flag)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = flag ? await client.ExecuteAsync("PING") : existing;
                    try { _ = {{creation}}; result.Dispose(); }
                    catch { if (flag) result.Dispose(); }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            {{holder}}
            class Caller
            {
                bool Throws() => throw new System.Exception();
                async Task Run(RespireClient client, RespirePending<string> existing, bool flag)
                {
                    var batch = client.CreateBatch();
                    var pending = flag ? batch.GetStringAsync("key") : existing;
                    try { _ = {{creation}}; await batch.SendAsync(); }
                    catch { if (flag) await batch.SendAsync(); }
                    System.Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("Property = (flag = false);", true)]
    [Arguments("this[0] = (flag = false);", true)]
    [Arguments("Property = Throws(); flag = false;", false)]
    [Arguments("Property &= (flag = false);", true)]
    [Arguments("target.Value = (flag = false);", true)]
    [Arguments("target[0] = (flag = false);", true)]
    [Arguments("target.Value = Throws(); flag = false;", false)]
    [Arguments("target.Value &= (flag = false);", true)]
    [Arguments("Text ??= (flag = false) ? null : \"value\";", true)]
    [Arguments("Text ??= \"value\"; flag = false;", false)]
    [Arguments("target.Value ??= (flag = false) ? null : \"value\";", true)]
    public async Task SetterExceptionsFollowRhsWrites(string assignment, bool warning)
    {
        const string members = """
            bool Property { get => true; set => throw new System.Exception(); }
            string Text { get => null; set => throw new System.Exception(); }
            bool this[int index] { get => true; set => throw new System.Exception(); }
            bool Throws() => throw new System.Exception();
            """;
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                {{members}}
                async Task Run(RespireClient client, RespireResult existing, bool flag, dynamic target)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = flag ? await client.ExecuteAsync("PING") : existing;
                    try { {{assignment}} result.Dispose(); }
                    catch { if (flag) result.Dispose(); }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                {{members}}
                async Task Run(RespireClient client, RespirePending<string> existing, bool flag, dynamic target)
                {
                    var batch = client.CreateBatch();
                    var pending = flag ? batch.GetStringAsync("key") : existing;
                    try { {{assignment}} await batch.SendAsync(); }
                    catch { if (flag) await batch.SendAsync(); }
                    System.Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("publisher.Changed += handler;", false, true)]
    [Arguments("publisher.Changed -= handler;", false, true)]
    [Arguments("publisher.Changed += handler;", true, false)]
    [Arguments("_ = new byte[number];", false, true)]
    [Arguments("_ = new byte[number];", true, false)]
    [Arguments("_ = (long)number;", false, false)]
    [Arguments("_ = (object)text;", false, false)]
    [Arguments("_ = unchecked((byte)number);", false, false)]
    [Arguments("_ = checked((long)number);", false, false)]
    [Arguments("_ = checked((float)floating);", false, false)]
    [Arguments("_ = boxed as string;", false, false)]
    [Arguments("_ = (string)boxed;", false, true)]
    [Arguments("_ = (int)boxed;", false, true)]
    [Arguments("_ = checked((byte)number);", false, true)]
    [Arguments("_ = checked((byte?)number);", false, true)]
    [Arguments("checked { _ = (byte?)number; }", false, true)]
    [Arguments("_ = checked((byte?)nullable);", false, true)]
    [Arguments("_ = unchecked((byte?)number);", false, false)]
    [Arguments("checked { _ = unchecked((byte?)number); }", false, false)]
    [Arguments("unchecked { _ = checked((byte?)number); }", false, true)]
    [Arguments("_ = (int)nullable;", false, true)]
    [Arguments("_ = (long?)nullable;", false, false)]
    [Arguments("_ = (int)amount;", false, true)]
    [Arguments("_ = (decimal)floating;", false, true)]
    public async Task AdditionalExceptionSourcesAndSafeCasts(string operation, bool cleanupInCatch, bool warning)
    {
        const string publisher = """
            class Publisher
            {
                public event System.Action Changed
                {
                    add { throw new System.Exception(); }
                    remove { throw new System.Exception(); }
                }
            }
            """;
        const string parameters = "Publisher publisher, System.Action handler, int number, string text, object boxed, int? nullable, decimal amount, double floating";
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            {{publisher}}
            class Caller
            {
                async Task Run(RespireClient client, {{parameters}})
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { {{operation}} result.Dispose(); }
                    catch { {{(cleanupInCatch ? "result.Dispose();" : "")}} }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            {{publisher}}
            class Caller
            {
                async Task Run(RespireClient client, {{parameters}})
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{operation}} await batch.SendAsync(); }
                    catch { {{(cleanupInCatch ? "await batch.SendAsync();" : "")}} }
                    System.Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("_ = new T();", false, true)]
    [Arguments("_ = new T();", true, false)]
    [Arguments("_ = nameof(holder.Property);", false, false)]
    [Arguments("_ = holder.Property;", false, true)]
    [Arguments("_ = new Holder(value);", false, true)]
    [Arguments("_ = new Holder(value);", true, false)]
    public async Task ExceptionSourcesRespectEvaluation(string operation, bool cleanupInCatch, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Holder { public Holder(object value) { } public int Property => throw new Exception(); }
            class Caller
            {
                async Task Run<T>(RespireClient client, Holder holder, dynamic value) where T : new()
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { {{operation}} result.Dispose(); }
                    catch { {{(cleanupInCatch ? "result.Dispose();" : "")}} }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Holder { public Holder(object value) { } public int Property => throw new Exception(); }
            class Caller
            {
                async Task Run<T>(RespireClient client, Holder holder, dynamic value) where T : new()
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{operation}} await batch.SendAsync(); }
                    catch { {{(cleanupInCatch ? "await batch.SendAsync();" : "")}} }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("Throws() || true", false)]
    [Arguments("Property || true", false)]
    [Arguments("Throws() || true", true)]
    [Arguments("true", false)]
    public async Task ThrowingFilterContinuesHandlerSearch(string filter, bool cleanupInFallback)
    {
        var warning = filter != "true" && !cleanupInFallback;
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                bool Throws() => throw new Exception();
                bool Property => throw new Exception();
                async Task Run(RespireClient client)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { throw new Exception(); }
                    catch (Exception) when ({{filter}}) { result.Dispose(); }
                    catch (Exception) { {{(cleanupInFallback ? "result.Dispose();" : "")}} }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                bool Throws() => throw new Exception();
                bool Property => throw new Exception();
                async Task Run(RespireClient client)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { throw new Exception(); }
                    catch (Exception) when ({{filter}}) { await batch.SendAsync(); }
                    catch (Exception) { {{(cleanupInFallback ? "await batch.SendAsync();" : "")}} }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("_ = 1 / divisor;", true)]
    [Arguments("_ = 1 % divisor;", true)]
    [Arguments("divisor /= divisor;", true)]
    [Arguments("divisor %= divisor;", true)]
    [Arguments("_ = checked(divisor + 1);", true)]
    [Arguments("_ = checked(divisor * 2);", true)]
    [Arguments("_ = checked(-divisor);", true)]
    [Arguments("_ = checked(-(int?)divisor);", true)]
    [Arguments("checked { divisor++; }", true)]
    [Arguments("checked { divisor += 1; }", true)]
    [Arguments("_ = amount * amount;", true)]
    [Arguments("_ = 1.0 / divisor;", false)]
    [Arguments("_ = unchecked(divisor + 1);", false)]
    [Arguments("_ = 4 / 2;", false)]
    public async Task BuiltInArithmeticCanBypassCleanup(string operation, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, int divisor, decimal amount)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { {{operation}} result.Dispose(); }
                    catch { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, int divisor, decimal amount)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{operation}} await batch.SendAsync(); }
                    catch { }
                    System.Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("int", "count > 0", "count <= 0", false)]
    [Arguments("int", "count < 0", "count >= 0", false)]
    [Arguments("int", "0 >= count", "count > 0", false)]
    [Arguments("double", "!(count > 0)", "!(count <= 0)", true)]
    [Arguments("int?", "!(count > 0)", "!(count <= 0)", true)]
    [Arguments("double", "count is not > 0", "count is not <= 0", true)]
    [Arguments("int?", "count is not > 0", "count is not <= 0", true)]
    public async Task RelationalComplementsRequireTotalOrder(string type, string selection, string opposite, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, RespireResult existing, {{type}} count)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = {{selection}} ? await client.ExecuteAsync("PING") : existing;
                    if ({{opposite}}) { } else result.Dispose();
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, {{type}} count)
                {
                    var first = client.CreateBatch();
                    var second = client.CreateBatch();
                    var pending = {{selection}} ? first.GetStringAsync("a") : second.GetStringAsync("b");
                    if ({{opposite}}) await second.SendAsync(); else await first.SendAsync();
                    System.Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("_ = left + right;", false)]
    [Arguments("_ = -left;", false)]
    [Arguments("left += right;", false)]
    [Arguments("left++;", false)]
    [Arguments("left--;", false)]
    [Arguments("_ = left + right;", true)]
    public async Task OverloadedOperatorCanBypassCleanup(string operation, bool cleanupInCatch)
    {
        const string operators = """
            struct Operand
            {
                public static Operand operator +(Operand left, Operand right) => throw new System.Exception();
                public static Operand operator -(Operand value) => throw new System.Exception();
                public static Operand operator ++(Operand value) => throw new System.Exception();
                public static Operand operator --(Operand value) => throw new System.Exception();
            }
            """;
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            {{operators}}
            class Caller
            {
                async Task Run(RespireClient client, Operand left, Operand right)
                {
                    var {{(cleanupInCatch ? "result" : "{|RESP001:result|}")}} = await client.ExecuteAsync("PING");
                    try { {{operation}} result.Dispose(); }
                    catch { {{(cleanupInCatch ? "result.Dispose();" : "")}} }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            {{operators}}
            class Caller
            {
                async Task Run(RespireClient client, Operand left, Operand right)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{operation}} await batch.SendAsync(); }
                    catch { {{(cleanupInCatch ? "await batch.SendAsync();" : "")}} }
                    System.Console.WriteLine({{(cleanupInCatch ? "pending.Result" : "{|RESP002:pending.Result|}")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("GetCommand()", false)]
    [Arguments("Command", false)]
    [Arguments("GetCommand()", true)]
    public async Task AcquisitionArgumentsPrecedeOwnership(string command, bool throwAfterAcquisition)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                string GetCommand() => throw new Exception();
                string Command => throw new Exception();
                async Task Run(RespireClient client)
                {
                    try
                    {
                        var {{(throwAfterAcquisition ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync({{command}});
                        {{(throwAfterAcquisition ? "GetCommand();" : "")}}
                        result.Dispose();
                    }
                    catch { }
                }
            }
            """);
    }

    [Test]
    [Arguments("count > 0", "count > 0", "", false)]
    [Arguments("count >= 0", "count >= 0", "", false)]
    [Arguments("count < 0", "count < 0", "", false)]
    [Arguments("count <= 0", "count <= 0", "", false)]
    [Arguments("0 < count", "count > 0", "", false)]
    [Arguments("count > 0", "count < 0", "", true)]
    [Arguments("count > 0", "count > 0", "count = -1;", true)]
    [Arguments("count is > 0", "count is > 0", "", false)]
    [Arguments("count is >= 0", "count >= 0", "", false)]
    [Arguments("count < 0", "count is < 0", "", false)]
    [Arguments("count is not > 0", "count is <= 0", "", false)]
    [Arguments("count is > 0", "count is > 0", "count = -1;", true)]
    [Arguments("count is > 0", "count is < 0", "", true)]
    public async Task RepeatedRelationalPredicatesRetainSelection(string selection, string cleanup, string write, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, RespireResult existing, int count)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = {{selection}} ? await client.ExecuteAsync("PING") : existing;
                    {{write}}
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
                async Task Run(RespireClient client, int count)
                {
                    var first = client.CreateBatch();
                    var second = client.CreateBatch();
                    var pending = {{selection}} ? first.GetStringAsync("a") : second.GetStringAsync("b");
                    {{write}}
                    if ({{cleanup}}) await first.SendAsync(); else await second.SendAsync();
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("holder?.Field", false)]
    [Arguments("holder?.Next?.Field", false)]
    [Arguments("holder?.Next.Field", true)]
    [Arguments("holder.Field", true)]
    public async Task ConditionalFieldAccessDoesNotInventNullDereference(string access, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Holder { public int Field = 1; public Holder Next = null; }
            class Caller
            {
                async Task Run(RespireClient client, Holder holder)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { _ = {{access}}; result.Dispose(); }
                    catch (NullReferenceException) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Holder { public int Field = 1; public Holder Next = null; }
            class Caller
            {
                async Task Run(RespireClient client, Holder holder)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { _ = {{access}}; await batch.SendAsync(); }
                    catch (NullReferenceException) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments(10, false)]
    [Arguments(14, true)]
    public async Task ProcessedStateLimitRetainsWarning(int predicates, bool warning)
    {
        var parameters = string.Join(", ", Enumerable.Range(0, predicates).Select(index => $"bool flag{index}"));
        var branches = string.Join(Environment.NewLine, Enumerable.Range(0, predicates)
            .Select(index => $"if (flag{index}) System.Console.WriteLine({index});"));
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, {{parameters}})
                {
                    {{branches}}
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    result.Dispose();
                    {{branches}}
                }
            }
            """);
    }

    [Test]
    [Arguments(64, false)]
    [Arguments(65, true)]
    public async Task PredicateLimitRetainsWarning(int predicates, bool warning)
    {
        var parameters = string.Join(", ", Enumerable.Range(0, predicates).Select(index => $"bool flag{index}"));
        var branches = string.Join(Environment.NewLine, Enumerable.Range(0, predicates)
            .Select(index => $"if (flag{index}) return;"));
        await Disposal.VerifyAsync($$"""
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, {{parameters}})
                {
                    {{branches}}
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    {{branches}}
                    result.Dispose();
                }
            }
            """);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InstanceFieldExceptionCanBypassCleanup(bool cleanupInCatch)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Holder { public int Field = 1; }
            class Caller
            {
                async Task Run(RespireClient client, Holder holder)
                {
                    var {{(cleanupInCatch ? "result" : "{|RESP001:result|}")}} = await client.ExecuteAsync("PING");
                    try { _ = holder.Field; result.Dispose(); }
                    catch (NullReferenceException) { {{(cleanupInCatch ? "result.Dispose();" : "")}} }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Holder { public int Field = 1; }
            class Caller
            {
                async Task Run(RespireClient client, Holder holder)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { _ = holder.Field; await batch.SendAsync(); }
                    catch (NullReferenceException) { {{(cleanupInCatch ? "await batch.SendAsync();" : "")}} }
                    Console.WriteLine({{(cleanupInCatch ? "pending.Result" : "{|RESP002:pending.Result|}")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("MayThrow(); choice = false;", false)]
    [Arguments("choice = false; MayThrow();", true)]
    [Arguments("choice = MayThrow();", false)]
    [Arguments("Consume(MayThrow(), choice = false);", true)]
    [Arguments("Consume(choice = false, MayThrow());", true)]
    public async Task ExceptionalPredicatesReflectOnlyCompletedWrites(string operations, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                bool MayThrow() => throw new Exception();
                void Consume(bool first, bool second) { }
                async Task Run(RespireClient client, RespireResult existing, bool choice)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = choice ? await client.ExecuteAsync("PING") : existing;
                    try { {{operations}} result.Dispose(); }
                    catch (Exception) { if (choice) result.Dispose(); }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                bool MayThrow() => throw new Exception();
                void Consume(bool first, bool second) { }
                async Task Run(RespireClient client, RespirePending<string> existing, bool choice)
                {
                    var batch = client.CreateBatch();
                    var pending = choice ? batch.GetStringAsync("a") : existing;
                    try { {{operations}} await batch.SendAsync(); }
                    catch (Exception)
                    {
                        if (choice) await batch.SendAsync();
                    }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

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
    [Arguments("", "choice", "choice", "Reset(out choice);", true)]
    [Arguments("", "choice", "choice", "Action mutate = () => choice = !choice; mutate();", true)]
    [Arguments("", "choice", "choice", "choice ^= true;", true)]
    [Arguments("", "choice", "choice", "void ChangeChoice() { choice = !choice; } ChangeChoice();", true)]
    [Arguments("", "(byte)number == 0", "number == 0", "", true)]
    [Arguments("", "choice ? other : false", "other", "", true, false)]
    public async Task CorrelationRequiresStableEquivalentValues(
        string declaration, string select, string flush, string mutation, bool warning, bool? disposalWarning = null)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                static void Change(ref bool value) { value = !value; }
                static void Reset(out bool value) { value = false; }
                async Task Run(RespireClient client, RespireResult existing, bool choice, bool other, int number)
                {
                    {{declaration}}
                    var {{((disposalWarning ?? warning) ? "{|RESP001:result|}" : "result")}} = ({{select}}) ? await client.ExecuteAsync("PING") : existing;
                    {{mutation}}
                    if ({{flush}}) result.Dispose();
                }
            }
            """);
        var read = warning ? "{|RESP002:pending.Result|}" : "pending.Result";
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                static void Change(ref bool value) { value = !value; }
                static void Reset(out bool value) { value = false; }
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
