using TUnit.Assertions;
using TUnit.Core;
using Pending = Respire.Analyzers.Tests.AnalyzerVerifier<Respire.Analyzers.PendingReadBeforeFlushAnalyzer>;
using Disposal = Respire.Analyzers.Tests.AnalyzerVerifier<Respire.Analyzers.UndisposedPooledResultAnalyzer>;

namespace Respire.Analyzers.Tests;

public class ExceptionAwareFlowTests
{
    [Test]
    [Arguments("publisher.Changed -= result.Dispose;", "", "", true)]
    [Arguments("publisher.Changed -= () => result.Dispose();", "", "", true)]
    [Arguments("publisher.Changed -= new Action(result.Dispose);", "", "", true)]
    [Arguments("publisher.Changed += result.Dispose;", "InvalidOperationException", "", false)]
    [Arguments("publisher.Changed += result.Dispose;", "NullReferenceException", "", true)]
    [Arguments("publisher.Changed += result.Dispose;", "OutOfMemoryException", "", true)]
    [Arguments("publisher.Changed += result.Dispose;", "NullReferenceException", "result.Dispose();", false)]
    [Arguments("Publisher.StaticChanged += result.Dispose;", "TypeInitializationException", "", true)]
    public async Task EventCaptureTransfersOnlyAtAddEntry(string operation, string catchType, string cleanup, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Publisher
        {
            static Publisher() { }
            public event Action Changed { add { throw new InvalidOperationException(); } remove { } }
            public static event Action StaticChanged { add { } remove { } }
        }
        class Caller
        {
            async Task Run(RespireClient client, Publisher publisher)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                {{(catchType.Length == 0 ? operation : $"try {{ {operation} }} catch ({catchType}) {{ {cleanup} }}")}}
            }
        }
        """);

    [Test]
    [Arguments("if (flag) Take(owner); else await owner.SendAsync();", false)]
    [Arguments("if (flag) Take(owner);", true)]
    [Arguments("if (flag) await owner.SendAsync();", true)]
    [Arguments("if (flag) await owner.SendAsync(); else { bool seen = owner != null; }", true)]
    [Arguments("if (flag) await owner.SendAsync(); else { bool seen = owner is not null; }", true)]
    [Arguments("if (flag) await owner.SendAsync(); else { bool seen = (object)owner != null; }", true)]
    [Arguments("if (flag) await owner.SendAsync(); else { bool seen = object.ReferenceEquals(owner, null); }", true)]
    [Arguments("bool seen = owner != null;", true)]
    [Arguments("if (flag) Take(owner); else { owner = client.CreateBatch(); Take(owner); }", true)]
    [Arguments("owner = client.CreateBatch(); Take(owner);", true)]
    [Arguments("if (flag) Take(owner); else { await owner.SendAsync(); owner = client.CreateBatch(); Take(owner); }", false)]
    [Arguments("if (flag) Take(owner); else Take(owner);", false)]
    [Arguments("if (owner is var alias) Take(alias);", false)]
    [Arguments("bool matched = owner is var alias; Take(alias);", false)]
    [Arguments("if (owner is var alias) { }", true)]
    [Arguments("if (owner is var alias) { alias = client.CreateBatch(); Take(alias); }", true)]
    [Arguments("if (owner is var alias) { owner = client.CreateBatch(); Take(alias); }", false)]
    [Arguments("if (flag) await owner.SendAsync(); else if (owner is var alias) Take(alias);", false)]
    [Arguments("if (owner is var alias) { if (flag) Take(alias); }", true)]
    [Arguments("if (owner is var alias && alias is var copy) Take(copy);", false)]
    [Arguments("if (owner is { } alias) Take(alias); else return;", false)]
    [Arguments("if (owner is { } alias) await FlushAsync(alias); else return;", false)]
    [Arguments("if (owner is var alias) await alias.SendAsync();", false)]
    [Arguments("if (owner is { } alias) await alias.SendAsync(); else return;", false)]
    [Arguments("if (flag) await owner.SendAsync(); else if (owner is var alias) await alias.SendAsync();", false)]
    [Arguments("if (owner is var alias) { alias = client.CreateBatch(); await alias.SendAsync(); }", true)]
    [Arguments("if (owner is var alias) { var flush = alias.SendAsync(); await flush; }", false)]
    [Arguments("if (owner is var alias) { if (flag) await alias.SendAsync(); }", true)]
    [Arguments("if (owner is var alias && alias is { } copy) await copy.SendAsync(); else return;", false)]
    [Arguments("Take(alias);", true, "bool matched = owner is var alias; alias = client.CreateBatch();")]
    [Arguments("await alias.SendAsync();", true, "bool matched = owner is var alias; alias = client.CreateBatch();")]
    [Arguments("await alias.SendAsync();", false, "bool matched = owner is var alias;")]
    [Arguments("Take(alias);", false, "bool matched = owner is var alias;")]
    [Arguments("Take(alias);", true, "bool matched = owner is var alias; if (flag) alias = client.CreateBatch();")]
    [Arguments("Take(copy);", true, "bool matched = owner is var alias; alias = client.CreateBatch(); bool copied = alias is var copy;")]
    [Arguments("Take(copy);", true, "bool matched = owner is var alias; bool copied = alias is var copy; copy = client.CreateBatch();")]
    [Arguments("Take(copy);", false, "bool matched = owner is var alias; bool copied = alias is var copy; alias = client.CreateBatch();")]
    public async Task BatchTransferAndFlushCoverDifferentPaths(string operation, bool warning, string setup = "") => await Pending.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static void Take(RespireBatch batch) { }
            static async Task FlushAsync(RespireBatch batch) => await batch.SendAsync();
            async Task Run(RespireClient client, bool flag)
            {
                var owner = client.CreateBatch();
                {{setup}}
                var pending = owner.GetStringAsync("key");
                {{operation}}
                Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
            }
        }
        """);

    [Test]
    [Arguments("Take(owner);", "OutOfMemoryException", false, true)]
    [Arguments("Take(owner, owner);", "OutOfMemoryException", false, true)]
    [Arguments("Take(owner);", "OutOfMemoryException", true, false)]
    [Arguments("Take(owner);", "InvalidOperationException", false, false)]
    [Arguments("Take(owner);", "OverflowException", false, false)]
    [Arguments("Take(new[] { owner });", "OutOfMemoryException", false, true)]
    public async Task ExpandedParamsAllocateBeforeTransfer(string call, string catchType, bool cleanup, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                static void Take(params RespireResult[] values) { }
                async Task Run(RespireClient client)
                {
                    var {{(warning ? "{|RESP001:owner|}" : "owner")}} = await client.ExecuteAsync("PING");
                    try { {{call}} }
                    catch ({{catchType}}) { {{(cleanup ? "owner.Dispose();" : "")}} }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                static void Take(params RespireBatch[] values) { }
                async Task Run(RespireClient client)
                {
                    var owner = client.CreateBatch();
                    var pending = owner.GetStringAsync("key");
                    try { {{call}} }
                    catch ({{catchType}}) { {{(cleanup ? "await owner.SendAsync();" : "")}} }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("if (divisor == 0) return;", "_ = 1 / divisor;", "DivideByZeroException", false)]
    [Arguments("if (divisor == 0) return;", "_ = 1 % divisor;", "DivideByZeroException", false)]
    [Arguments("if (divisor == 0) return;", "int quotient = 1; quotient /= divisor;", "DivideByZeroException", false)]
    [Arguments("if (divisor <= 0) return;", "_ = 1 / divisor;", "DivideByZeroException", false)]
    [Arguments("if (divisor < 1) return;", "_ = 1 / divisor;", "DivideByZeroException", false)]
    [Arguments("if (divisor <= 7) return;", "_ = 1 % divisor;", "DivideByZeroException", false)]
    [Arguments("if (divisor > -1) return;", "_ = 1 / divisor;", "DivideByZeroException", false)]
    [Arguments("if (divisor >= -7) return;", "_ = 1 / divisor;", "DivideByZeroException", false)]
    [Arguments("if (1 > divisor) return;", "_ = 1 / divisor;", "DivideByZeroException", false)]
    [Arguments("if (divisor != 3) return;", "_ = 1 / divisor;", "DivideByZeroException", false)]
    [Arguments("if (divisor < 1) return; divisor = 0;", "_ = 1 / divisor;", "DivideByZeroException", true)]
    [Arguments("if (divisor < -1) return;", "_ = 1 / divisor;", "DivideByZeroException", true)]
    [Arguments("if (divisor > 1) return;", "_ = 1 / divisor;", "DivideByZeroException", true)]
    [Arguments("if (divisor < 0) return;", "_ = 1 / divisor;", "DivideByZeroException", true)]
    [Arguments("if (divisor == 3) return;", "_ = 1 / divisor;", "DivideByZeroException", true)]
    [Arguments("if (divisor == 0) return; divisor = 0;", "_ = 1 / divisor;", "DivideByZeroException", true)]
    [Arguments("", "_ = 1 / divisor;", "DivideByZeroException", true)]
    [Arguments("if (divisor == 0) return;", "_ = int.MinValue / divisor;", "OverflowException", true)]
    [Arguments("if (value is not string) return;", "_ = (string)value;", "InvalidCastException", false)]
    [Arguments("if (!(value is string)) return;", "_ = (string)value;", "InvalidCastException", false)]
    [Arguments("if (value is not string) return; value = new object();", "_ = (string)value;", "InvalidCastException", true)]
    [Arguments("if (value is not string) return;", "_ = (int)value;", "InvalidCastException", true)]
    [Arguments("", "_ = (string)value;", "InvalidCastException", true)]
    [Arguments("if (value is not int) return;", "_ = (int)value;", "InvalidCastException", false)]
    [Arguments("if (value is not string) return;", "_ = (IComparable)value;", "InvalidCastException", false)]
    [Arguments("if (value is not int) return;", "_ = (long)value;", "InvalidCastException", true)]
    [Arguments("if (value is not null) return;", "_ = (string)value;", "InvalidCastException", false)]
    [Arguments("if (value != null) return;", "_ = (IComparable)value;", "InvalidCastException", false)]
    [Arguments("if (value is not null) return;", "_ = (int?)value;", "InvalidCastException", false)]
    [Arguments("if (value is not null) return;", "_ = (int)value;", "InvalidCastException", false)]
    [Arguments("if (value is not null) return;", "_ = (int)value;", "NullReferenceException", true)]
    [Arguments("if (value is not null) return;", "_ = (int?)value;", "NullReferenceException", false)]
    [Arguments("if (value is not null) return; value = new object();", "_ = (string)value;", "InvalidCastException", true)]
    [Arguments("if (value is not null) return; value = new object();", "_ = (int?)value;", "InvalidCastException", true)]
    [Arguments("", "_ = (int?)value;", "InvalidCastException", true)]
    [Arguments("value = \"ready\";", "_ = ((string)value).Length;", "NullReferenceException", false)]
    [Arguments("var source = \"ready\"; value = source;", "_ = ((string)value).Length;", "NullReferenceException", false)]
    [Arguments("object source = value; if (source is null) return; value = source;", "_ = ((string)value).Length;", "NullReferenceException", false)]
    [Arguments("if (value is null) return; value = value;", "_ = ((string)value).Length;", "NullReferenceException", false)]
    [Arguments("value = \"ready\"; value = null;", "_ = ((string)value).Length;", "NullReferenceException", true)]
    [Arguments("value = null;", "_ = (string)value;", "InvalidCastException", false)]
    [Arguments("value = \"ready\"; value = Console.ReadLine();", "_ = ((string)value).Length;", "NullReferenceException", true)]
    public async Task GuardsExcludeImpossibleArithmeticAndCastFailures(string setup, string operation, string catchType, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, int divisor, object value)
                {
                    {{setup}}
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { {{operation}} result.Dispose(); }
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
                async Task Run(RespireClient client, int divisor, object value)
                {
                    {{setup}}
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{operation}} await batch.SendAsync(); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    public async Task DecimalNegationHasSymmetricRange()
    {
        var minimum = decimal.MinValue;
        var maximum = decimal.MaxValue;
        await Assert.That(-minimum).IsEqualTo(maximum);
        await Assert.That(checked(-minimum)).IsEqualTo(maximum);
        await Assert.That(-maximum).IsEqualTo(minimum);
    }

    [Test]
    public async Task CheckedByteCompoundShiftChecksNarrowingAtRuntime()
    {
        byte value = 128;
        var overflow = false;
        try { checked { value <<= 1; } }
        catch (OverflowException) { overflow = true; }
        await Assert.That(overflow).IsTrue();
    }

    [Test]
    [Arguments("checked((object)result)", "Throws()", "", true)]
    [Arguments("unchecked((object)result)", "Throws()", "", true)]
    [Arguments("checked((object)result)", "0", "", false)]
    [Arguments("checked((object)result)", "Throws()", "result.Dispose();", false)]
    public async Task CheckedOwnerWaitsForLaterArguments(string owner, string argument, string cleanup, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static int Throws() => throw new InvalidOperationException();
            static void Take(object owner, int other) { }
            async Task Run(RespireClient client)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { Take({{owner}}, {{argument}}); }
                catch (InvalidOperationException) { {{cleanup}} }
            }
        }
        """);

    [Test]
    [Arguments("if (error is not null) return;", false)]
    [Arguments("if (error != null) return;", false)]
    [Arguments("", true)]
    [Arguments("if (error is not null) return; error = new ArgumentException();", true)]
    public async Task NullThrowCannotEnterDeclaredTypeHandler(string setup, bool warning) => await Pending.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            async Task Run(RespireClient client, ArgumentException error)
            {
                {{setup}}
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try { throw error; }
                catch (ArgumentException) { Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}}); }
                catch (NullReferenceException) { await batch.SendAsync(); }
            }
        }
        """);

    [Test]
    [Arguments("if (optional is not null) return;", false)]
    [Arguments("if (optional.HasValue) return;", false)]
    [Arguments("if (optional.HasValue == true) return;", false)]
    [Arguments("if (optional != null) return;", false)]
    [Arguments("if (optional is not null) return; optional = 1;", true)]
    [Arguments("", true)]
    [Arguments("if (optional is null) return;", true)]
    public async Task EmptyNullableBoxingCannotAllocate(string setup, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, int? optional)
                {
                    {{setup}}
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { object boxed = optional; result.Dispose(); }
                    catch (OutOfMemoryException) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, int? optional)
                {
                    {{setup}}
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { object boxed = optional; await batch.SendAsync(); }
                    catch (OutOfMemoryException) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("await batch.ExecuteAsync(ThrowToken());", "", true)]
    [Arguments("await batch.ExecuteAsync(default);", "", false)]
    [Arguments("await batch.ExecuteAsync(ThrowToken());", "await batch.SendAsync();", false)]
    [Arguments("await batch.ExecuteAsync(ThrowToken()).ConfigureAwait(false);", "", true)]
    [Arguments("await batch.ExecuteAsync(default).ConfigureAwait(false);", "", false)]
    [Arguments("await batch.ExecuteAsync(default).ConfigureAwait(ThrowFlag());", "", true)]
    [Arguments("await batch.ExecuteAsync(ThrowToken()).AsTask();", "", true)]
    [Arguments("await Task.WhenAll(batch.ExecuteAsync(default).AsTask());", "", false)]
    [Arguments("await Task.WhenAll(batch.ExecuteAsync(default).AsTask(), ThrowTask());", "", true)]
    [Arguments("batch.SendAsync().AsTask().GetAwaiter().GetResult();", "", false)]
    [Arguments("batch.ExecuteAsync(default).GetAwaiter().GetResult();", "", false)]
    [Arguments("batch.ExecuteAsync(ThrowToken()).GetAwaiter().GetResult();", "", true)]
    [Arguments("batch.ExecuteAsync(default).ConfigureAwait(false).GetAwaiter().GetResult();", "", false)]
    [Arguments("batch.ExecuteAsync(default).ConfigureAwait(ThrowFlag()).GetAwaiter().GetResult();", "", true)]
    [Arguments("batch.ExecuteAsync(ThrowToken()).GetAwaiter().GetResult();", "batch.SendAsync().GetAwaiter().GetResult();", false)]
    [Arguments("await Task.WhenAll(batch.ExecuteAsync(default).AsTask());", "", true, "OutOfMemoryException")]
    [Arguments("await Task.WhenAll(batch.ExecuteAsync(default).AsTask());", "await batch.SendAsync();", false, "OutOfMemoryException")]
    public async Task AwaitedFlushWaitsForArguments(string operation, string cleanup, bool warning, string catchType = "InvalidOperationException") => await Pending.VerifyAsync($$"""
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static CancellationToken ThrowToken() => throw new InvalidOperationException();
            static bool ThrowFlag() => throw new InvalidOperationException();
            static Task ThrowTask() => throw new InvalidOperationException();
            async Task Run(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try { {{operation}} }
                catch ({{catchType}}) { {{cleanup}} }
                Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
            }
        }
        """);

    [Test]
    [Arguments("(left, 0) == (right, 0)", true)]
    [Arguments("(left, 0) != (right, 0)", true)]
    [Arguments("((left, 0), 1) == ((right, 0), 1)", true)]
    [Arguments("(converted, 0) == (1, 0)", true)]
    [Arguments("(number, 0) == (1, 0)", false)]
    [Arguments("(text, 0) == (\"value\", 0)", false)]
    [Arguments("(optional, 0) == ((int?)1, 0)", false)]
    [Arguments("pair == (left, 0)", true)]
    [Arguments("(maybe, 0) == ((Element?)left, 0)", true)]
    [Arguments("(plain, 0) == ((object)text, 0)", false)]
    public async Task TupleEqualityIncludesElementOperators(string expression, bool warning)
    {
        const string types = """
            struct Element
            {
                public static bool operator ==(Element left, Element right) => throw new InvalidOperationException();
                public static bool operator !=(Element left, Element right) => throw new InvalidOperationException();
                public override bool Equals(object other) => false;
                public override int GetHashCode() => 0;
            }
            struct Converted { public static implicit operator int(Converted value) => throw new InvalidOperationException(); }
            """;
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{types}}
            class Caller
            {
                async Task Run(RespireClient client, Element left, Element right, Converted converted, int number, string text, int? optional, (Element, int) pair, Element? maybe, object plain)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { _ = {{expression}}; result.Dispose(); }
                    catch (InvalidOperationException) { }
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
                async Task Run(RespireClient client, Element left, Element right, Converted converted, int number, string text, int? optional, (Element, int) pair, Element? maybe, object plain)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { _ = {{expression}}; await batch.SendAsync(); }
                    catch (InvalidOperationException) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("default(ValueTask)", false)]
    [Arguments("default(ValueTask<int>)", false)]
    [Arguments("new ValueTask()", false)]
    [Arguments("new ValueTask<int>()", false)]
    [Arguments("unknown", true)]
    [Arguments("default(Task)", true, "NullReferenceException")]
    [Arguments("default(Task<int>)", true, "NullReferenceException")]
    public async Task DefaultValueTaskCannotBypassCleanup(string expression, bool warning, string catchType = "InvalidOperationException")
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, ValueTask unknown)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { await {{expression}}; result.Dispose(); }
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
                async Task Run(RespireClient client, ValueTask unknown)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { await {{expression}}; await batch.SendAsync(); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("Throws()", "", true)]
    [Arguments("0", "", false)]
    [Arguments("Throws()", "result.Dispose();", false)]
    public async Task AsConversionWaitsForLaterArguments(string argument, string cleanup, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static int Throws() => throw new InvalidOperationException();
            static void Take(object owner, int other) { }
            async Task Run(RespireClient client)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { Take(result as object, {{argument}}); }
                catch (InvalidOperationException) { {{cleanup}} }
            }
        }
        """);

    [Test]
    [Arguments("byte", "_ = checked(left + right);", false)]
    [Arguments("byte", "_ = checked(left - right);", false)]
    [Arguments("byte", "_ = checked(left * right);", false)]
    [Arguments("sbyte", "_ = checked(left * right);", false)]
    [Arguments("short", "_ = checked(left + right);", false)]
    [Arguments("short", "_ = checked(left * right);", false)]
    [Arguments("ushort", "_ = checked(left + right);", false)]
    [Arguments("ushort", "_ = checked(left * right);", true)]
    [Arguments("char", "_ = checked(left - right);", false)]
    [Arguments("byte?", "_ = checked(left + right);", false)]
    [Arguments("int", "_ = checked(left + right);", true)]
    [Arguments("byte", "checked { left += right; }", true)]
    [Arguments("byte", "_ = left / -1;", false)]
    [Arguments("sbyte", "_ = left / -1;", false)]
    [Arguments("short", "_ = left % -1;", false)]
    [Arguments("ushort", "_ = left / -1;", false)]
    [Arguments("char", "_ = left / -1;", false)]
    [Arguments("byte?", "_ = left / -1;", false)]
    [Arguments("int", "_ = left / -1;", true)]
    [Arguments("short", "checked { left /= -1; }", true)]
    [Arguments("byte", "checked { left /= 2; }", false)]
    [Arguments("ushort", "checked { left /= right; }", false)]
    [Arguments("uint", "checked { left /= right; }", false)]
    [Arguments("byte", "checked { left <<= 1; }", true)]
    [Arguments("sbyte", "checked { left <<= 1; }", true)]
    [Arguments("short", "checked { left <<= 1; }", true)]
    [Arguments("ushort", "checked { left <<= 1; }", true)]
    [Arguments("char", "checked { left <<= 1; }", true)]
    [Arguments("byte?", "checked { left <<= 1; }", true)]
    [Arguments("byte", "unchecked { left <<= 1; }", false)]
    [Arguments("int", "checked { left <<= 1; }", false)]
    [Arguments("byte", "checked { left >>= 1; }", false)]
    [Arguments("byte", "checked { left <<= 0; }", false)]
    [Arguments("byte", "checked { left <<= 32; }", false)]
    [Arguments("byte?", "left = null; checked { left <<= 1; }", false)]
    [Arguments("int", "_ = 1 / right;", false)]
    [Arguments("byte", "_ = checked(-left);", false)]
    [Arguments("sbyte", "_ = checked(-left);", false)]
    [Arguments("short", "_ = checked(-left);", false)]
    [Arguments("ushort", "_ = checked(-left);", false)]
    [Arguments("char", "_ = checked(-left);", false)]
    [Arguments("byte?", "_ = checked(-left);", false)]
    [Arguments("int", "_ = checked(-left);", true)]
    [Arguments("long", "_ = checked(-left);", true)]
    public async Task PromotedArithmeticRetainsOperandRanges(string type, string operation, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, {{type}} left, {{type}} right)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { {{operation}} result.Dispose(); }
                    catch (OverflowException) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, {{type}} left, {{type}} right)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{operation}} await batch.SendAsync(); }
                    catch (OverflowException) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("byte", "checked { value /= -1; }", "CS0031")]
    [Arguments("byte", "checked { value /= -2; }", "CS0031")]
    [Arguments("byte", "unchecked { value /= -1; }", "CS0031")]
    [Arguments("ushort", "checked { value /= -1; }", "CS0031")]
    [Arguments("char", "checked { value /= -1; }", "CS0266")]
    [Arguments("uint", "checked { value /= -1L; }", "CS0266")]
    public async Task UnsignedCompoundDivisionRejectsNegativeOperands(string type, string operation, string diagnosticId)
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText($$"""
            class Caller
            {
                static {{type}} Divide({{type}} value) { {{operation}} return value; }
            }
            """);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("UnsignedDivision",
            [tree], [Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
        await Assert.That(errors.Length).IsEqualTo(1);
        await Assert.That(errors[0].Id).IsEqualTo(diagnosticId);
    }

    [Test]
    [Arguments("if (left is not null) return;", "_ = left / 0;", "DivideByZeroException", false)]
    [Arguments("if (right is not null) return;", "_ = left / right;", "DivideByZeroException", false)]
    [Arguments("if (left is not null) return;", "_ = checked(left * int.MaxValue);", "OverflowException", false)]
    [Arguments("if (left is not null) return;", "_ = checked(-left);", "OverflowException", false)]
    [Arguments("if (left is not null) return;", "checked { left++; }", "OverflowException", false)]
    [Arguments("if (left is not null) return;", "left /= 0;", "DivideByZeroException", false)]
    [Arguments("if (left is not null) return; left = 1;", "_ = left / 0;", "DivideByZeroException", true)]
    [Arguments("", "_ = left / 0;", "DivideByZeroException", true)]
    [Arguments("if (left is not null) return;", "_ = left / Throws();", "InvalidOperationException", true)]
    [Arguments("if (left is not null) return;", "_ = left / (right = 0);", "DivideByZeroException", false)]
    [Arguments("if (left is not null) return;", "Count += left;", "InvalidOperationException", true)]
    [Arguments("if (left is not null) return;", "Count *= left;", "InvalidOperationException", true)]
    public async Task EmptyLiftedOperandsSkipArithmetic(string setup, string operation, string catchType, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                static int? Throws() => throw new InvalidOperationException();
                static int? Count { get => 1; set => throw new InvalidOperationException(); }
                async Task Run(RespireClient client, int? left, int? right)
                {
                    {{setup}}
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { {{operation}} result.Dispose(); }
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
                static int? Throws() => throw new InvalidOperationException();
                static int? Count { get => 1; set => throw new InvalidOperationException(); }
                async Task Run(RespireClient client, int? left, int? right)
                {
                    {{setup}}
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{operation}} await batch.SendAsync(); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("decimal?", "if (value is not null) return;", "(int?)value", "OverflowException", false)]
    [Arguments("long?", "if (value is not null) return;", "checked((int?)value)", "OverflowException", false)]
    [Arguments("double?", "if (value is not null) return;", "(decimal?)value", "OverflowException", false)]
    [Arguments("decimal?", "", "(int?)value", "OverflowException", true)]
    [Arguments("decimal?", "if (value is not null) return; value = decimal.MaxValue;", "(int?)value", "OverflowException", true)]
    [Arguments("decimal?", "if (value is not null) return;", "(int)value", "InvalidOperationException", true)]
    [Arguments("decimal?", "if (value is not null) return;", "(int)value", "OverflowException", false)]
    public async Task NullNumericConversionsSkipOverflow(string type, string setup, string conversion, string catchType, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, {{type}} value)
                {
                    {{setup}}
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { _ = {{conversion}}; result.Dispose(); }
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
                async Task Run(RespireClient client, {{type}} value)
                {
                    {{setup}}
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { _ = {{conversion}}; await batch.SendAsync(); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EmptyLiftedAssignmentStillInvokesSetter(bool cleanup) => await Pending.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static int? Count { get => 1; set => throw new InvalidOperationException(); }
            async Task Run(RespireClient client)
            {
                var batch = client.CreateBatch();
                RespirePending<string> pending = null!;
                int? none = null;
                try
                {
                    Count += (pending = batch.GetStringAsync("key")) is var value ? none : none;
                    await batch.SendAsync();
                }
                catch (InvalidOperationException) { {{(cleanup ? "await batch.SendAsync();" : "")}} }
                Console.WriteLine({{(cleanup ? "pending.Result" : "{|RESP002:pending.Result|}")}});
            }
        }
        """);

    [Test]
    [Arguments("holder = new { Owner = result, Other = Throws() };", "InvalidOperationException", "", true)]
    [Arguments("holder = new { Owner = result, Other = 0 };", "InvalidOperationException", "", false)]
    [Arguments("holder = new { Owner = result, Other = 0 };", "OutOfMemoryException", "", true)]
    [Arguments("holder = new { Owner = result, Other = Throws() };", "InvalidOperationException", "result.Dispose();", false)]
    [Arguments("holder = new { result, Other = Throws() };", "InvalidOperationException", "", true)]
    [Arguments("Take(new { Owner = result }, Throws());", "InvalidOperationException", "", true)]
    [Arguments("var owner = new { Owner = result, Other = Throws() };", "InvalidOperationException", "", true)]
    [Arguments("_ = new { Owner = result, Other = Throws() };", "InvalidOperationException", "", true)]
    [Arguments("_ = new { Owner = result, Other = 0 };", "InvalidOperationException", "", false)]
    [Arguments("_ = new { Owner = result, Other = 0 };", "OutOfMemoryException", "", true)]
    [Arguments("_ = new { Owner = result, Other = Throws() };", "InvalidOperationException", "result.Dispose();", false)]
    public async Task AnonymousOwnerWaitsForConstruction(string operation, string catchType, string cleanup, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static int Throws() => throw new InvalidOperationException();
            static void Take(object owner, int other) { }
            async Task Run(RespireClient client)
            {
                object holder;
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { {{operation}} }
                catch ({{catchType}}) { {{cleanup}} }
            }
        }
        """);

    [Test]
    [Arguments("goto Flush;", "await batch.SendAsync();", false)]
    [Arguments("if (flag) goto Read; goto Flush;", "await batch.SendAsync();", true)]
    [Arguments("goto Flush;", "if (flag) await batch.SendAsync();", true)]
    [Arguments("goto Flush;", "batch = client.CreateBatch(); await batch.SendAsync();", true)]
    [Arguments("goto Flush;", "if (batch is var alias) await alias.SendAsync();", false)]
    [Arguments("goto Flush;", "if (flag) Take(batch); else await batch.SendAsync();", false)]
    public async Task ForwardJumpFlushPrecedesRead(string jump, string completion, bool warning) => await Pending.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static void Take(RespireBatch batch) { }
            async Task Run(RespireClient client, bool flag)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                {{jump}}
                Read:
                Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                return;
                Flush:
                {{completion}}
                goto Read;
            }
        }
        """);

    [Test]
    [Arguments("holder = new Holder { Owner = result, Other = Throws() };", "", true)]
    [Arguments("holder = new Holder { Owner = result, Other = 0 };", "", false)]
    [Arguments("holder = new Holder { Owner = result, Other = Throws() };", "result.Dispose();", false)]
    [Arguments("var owner = new Holder { Owner = result, Other = Throws() };", "", true)]
    [Arguments("var owner = new Holder { Owner = result, Other = 0 };", "", false)]
    [Arguments("holder = new() { Owner = result, Other = Throws() };", "", true)]
    [Arguments("Take(new Holder { Owner = result, Other = 0 }, Throws());", "", true)]
    [Arguments("Take(new Holder { Owner = result, Other = 0 }, 0);", "", false)]
    [Arguments("var owner = existing with { Owner = result, Other = Throws() };", "", true)]
    [Arguments("var owner = existing with { Owner = result, Other = Throws() };", "result.Dispose();", false)]
    [Arguments("new Holder() { Owner = result, Other = Throws() };", "", true)]
    [Arguments("new Holder() { Owner = result, Other = 0 };", "", false)]
    [Arguments("new Holder() { Owner = result, Other = Throws() };", "result.Dispose();", false)]
    [Arguments("new Holder() { Owner = result, Other = flag ? Throws() : 0 };", "", true)]
    [Arguments("new Holder() { Owner = result, Other = flag ? 1 : 0 };", "", false)]
    [Arguments("new Holder() { Owner = result, Other = Throws() };", "", false, "public RespireResult Owner { set { value.Dispose(); } }")]
    [Arguments("holder = new Holder() { Owner = result, Other = Throws() };", "", false, "public RespireResult Owner { set { value.Dispose(); } }")]
    [Arguments("var owner = new Holder() { Owner = result, Other = Throws() };", "", false, "public RespireResult Owner { set { value.Dispose(); } }")]
    public async Task MemberInitializerWaitsForConstruction(string operation, string cleanup, bool warning, string ownerMember = "public RespireResult Owner;") => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Holder { {{ownerMember}} public int Other; }
        record RecordHolder { public RespireResult Owner; public int Other; }
        class Caller
        {
            static int Throws() => throw new InvalidOperationException();
            static void Take(object owner, int other) { }
            async Task Run(RespireClient client, RecordHolder existing, bool flag)
            {
                Holder holder;
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { {{operation}} }
                catch (InvalidOperationException) { {{cleanup}} }
            }
        }
        """);

    [Test]
    [Arguments("if (fail) throw new InvalidOperationException();", "", true)]
    [Arguments("if (fail) throw new InvalidOperationException();", "result.Dispose();", false)]
    [Arguments("", "", false)]
    [Arguments("result.Dispose(); if (fail) throw new InvalidOperationException();", "", false)]
    [Arguments("Throws();", "", true)]
    public async Task ReturnTransfersAfterFinallyCompletes(string finalizer, string cleanup, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static void Throws() => throw new InvalidOperationException();
            async Task<RespireResult> Run(RespireClient client, bool fail)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try
                {
                    try { return result; }
                    finally { {{finalizer}} }
                }
                catch (InvalidOperationException) { {{cleanup}} return default; }
            }
        }
        """);

    [Test]
    [Arguments("(new object[1])[0] = result;", "InvalidOperationException", false)]
    [Arguments("(new object[1])[1] = result;", "IndexOutOfRangeException", true)]
    [Arguments("values[0] = result;", "NullReferenceException", true)]
    [Arguments("values[0] = result;", "ArrayTypeMismatchException", true)]
    public async Task ArrayTransferUsesSpecificFailures(string operation, string catchType, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            async Task Run(RespireClient client, object[] values)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { {{operation}} }
                catch ({{catchType}}) { }
            }
        }
        """);

    [Test]
    [Arguments("if (skip) continue; result.Dispose();", true)]
    [Arguments("result.Dispose(); if (skip) continue;", false)]
    [Arguments("if (skip) { result.Dispose(); continue; } result.Dispose();", false)]
    [Arguments("if (skip) goto next; result.Dispose(); next:;", true)]
    public async Task InfiniteLoopMustReleaseBeforeReacquisition(string body, bool warning) => await Disposal.VerifyAsync($$"""
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            async Task Run(RespireClient client, bool skip)
            {
                while (true)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("BLPOP", "q", "0");
                    {{body}}
                }
            }
        }
        """);

    [Test]
    [Arguments("if (error is null) return;", false)]
    [Arguments("", true)]
    [Arguments("if (error is null) return; error = null;", true)]
    [Arguments("error = new Failure();", false)]
    public async Task NonNullThrownExceptionCannotEnterNullCatch(string setup, bool warning) => await Pending.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        sealed class Failure : Exception { }
        class Caller
        {
            async Task Run(RespireClient client, Failure error)
            {
                {{setup}}
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try { throw error; }
                catch (NullReferenceException) { }
                Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
            }
        }
        """);

    [Test]
    [Arguments("Take(new[] { result }, Throws());", "InvalidOperationException", "", true)]
    [Arguments("Take(new RespireResult[] { result }, Throws());", "InvalidOperationException", "", true)]
    [Arguments("Take(new[] { result, ThrowsResult() }, 0);", "InvalidOperationException", "", true)]
    [Arguments("Take(new[] { result }, 0);", "InvalidOperationException", "", false)]
    [Arguments("Take(new[] { result }, 0);", "OutOfMemoryException", "", true)]
    [Arguments("Take(new[] { result }, Throws());", "InvalidOperationException", "result.Dispose();", false)]
    [Arguments("Take(new[,] { { result } }, Throws());", "InvalidOperationException", "", true)]
    [Arguments("Take(new[] { new[] { result } }, Throws());", "InvalidOperationException", "", true)]
    [Arguments("_ = new[] { result };", "InvalidOperationException", "", true)]
    [Arguments("var owners = new[] { result };", "InvalidOperationException", "", false)]
    [Arguments("Take([result], Throws());", "InvalidOperationException", "", true)]
    [Arguments("Take([result, ThrowsResult()], 0);", "InvalidOperationException", "", true)]
    [Arguments("Take([result], 0);", "InvalidOperationException", "", false)]
    [Arguments("Take([result], 0);", "OutOfMemoryException", "", true)]
    [Arguments("Take([result], Throws());", "InvalidOperationException", "result.Dispose();", false)]
    [Arguments("_ = (RespireResult[])[result];", "InvalidOperationException", "", true)]
    [Arguments("RespireResult[] owners = [result];", "InvalidOperationException", "", false)]
    public async Task ArrayInitializerWaitsForOuterTransfer(string operation, string catchType, string cleanup, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static int Throws() => throw new InvalidOperationException();
            static RespireResult ThrowsResult() => throw new InvalidOperationException();
            static void Take(RespireResult[] values, int other) { foreach (var value in values) value.Dispose(); }
            static void Take(RespireResult[,] values, int other) { foreach (var value in values) value.Dispose(); }
            static void Take(RespireResult[][] values, int other) { foreach (var row in values) Take(row, other); }
            async Task Run(RespireClient client)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { {{operation}} }
                catch ({{catchType}}) { {{cleanup}} }
            }
        }
        """);

    [Test]
    [Arguments("result + ThrowsOwner()", "", "", "InvalidOperationException", true)]
    [Arguments("result + existing", "", "", "InvalidOperationException", false)]
    [Arguments("result + ThrowsOwner()", "result.Dispose();", "", "InvalidOperationException", false)]
    [Arguments("(flag ? result : result) + ThrowsOwner()", "", "", "InvalidOperationException", true)]
    [Arguments("(flag ? result : result) + existing", "", "", "InvalidOperationException", false)]
    [Arguments("Take(result + existing, Throws())", "", "", "InvalidOperationException", false)]
    [Arguments("result + existing", "", "static Owner() { throw new Exception(); }", "TypeInitializationException", true)]
    public async Task OperatorTransferWaitsForOperands(string expression, string cleanup, string constructor, string catchType, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Owner
        {
            {{constructor}}
            public static Owner operator +(RespireResult value, Owner owner) { value.Dispose(); return owner; }
        }
        class Caller
        {
            static Owner ThrowsOwner() => throw new InvalidOperationException();
            static int Throws() => throw new InvalidOperationException();
            static Owner Take(Owner owner, int other) => owner;
            async Task Run(RespireClient client, Owner existing, bool flag)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { _ = {{expression}}; }
                catch ({{catchType}}) { {{cleanup}} }
            }
        }
        """);

    [Test]
    [Arguments("(owner, Throws())", false, "", "InvalidOperationException", true)]
    [Arguments("(owner, 0)", false, "", "InvalidOperationException", false)]
    [Arguments("(owner, Throws())", true, "", "InvalidOperationException", false)]
    [Arguments("(owner, flag ? Throws() : 0)", false, "", "InvalidOperationException", true)]
    [Arguments("(flag ? owner : owner, 0)", false, "", "InvalidOperationException", false)]
    [Arguments("(owner, 0)", false, "static Holder() { throw new Exception(); }", "TypeInitializationException", true)]
    public async Task CompoundOperatorTransferWaitsForOperands(
        string operand, bool cleanup, string constructor, string catchType, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Holder
            {
                {{constructor}}
                public static Holder operator +(Holder holder, (RespireResult Value, int Other) item)
                { item.Value.Dispose(); throw new InvalidOperationException(); }
            }
            class Caller
            {
                static int Throws() => throw new InvalidOperationException();
                async Task Run(RespireClient client, Holder holder, bool flag)
                {
                    var {{(warning ? "{|RESP001:owner|}" : "owner")}} = await client.ExecuteAsync("PING");
                    try { holder += {{operand}}; }
                    catch ({{catchType}}) { {{(cleanup ? "owner.Dispose();" : "")}} }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Holder
            {
                {{constructor}}
                public static Holder operator +(Holder holder, (RespireBatch Value, int Other) item)
                { throw new InvalidOperationException(); }
            }
            class Caller
            {
                static int Throws() => throw new InvalidOperationException();
                async Task Run(RespireClient client, Holder holder, bool flag)
                {
                    var owner = client.CreateBatch();
                    var pending = owner.GetStringAsync("key");
                    try { holder += {{operand}}; }
                    catch ({{catchType}}) { {{(cleanup ? "await owner.SendAsync();" : "")}} }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("result ?? existing", "Throws()", "", true)]
    [Arguments("result ?? existing", "0", "", false)]
    [Arguments("result ?? existing", "Throws()", "result?.Dispose();", false)]
    [Arguments("(result ?? existing)", "Throws()", "", true)]
    [Arguments("optional ?? result ?? existing", "Throws()", "", true)]
    [Arguments("optional ?? result ?? existing", "0", "result?.Dispose();", false)]
    public async Task CoalescedOwnerWaitsForLaterArguments(string value, string laterArgument, string cleanup, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static int Throws() => throw new InvalidOperationException();
            static void Take(RespireResult value, int other) => value.Dispose();
            async Task Run(RespireClient client, RespireResult existing, RespireResult? optional)
            {
                RespireResult? {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { Take({{value}}, {{laterArgument}}); {{cleanup}} }
                catch (InvalidOperationException) { {{cleanup}} }
            }
        }
        """);

    [Test]
    [Arguments("new Owner { { result, Throws() } }", "", true)]
    [Arguments("new Owner { { result, 0 } }", "", false)]
    [Arguments("new Owner { result }", "", false)]
    [Arguments("new Owner { { result, Throws() } }", "result.Dispose();", false)]
    [Arguments("new Owner { { (result, 0), Throws() } }", "", true)]
    [Arguments("new Owner { { (result, 0), 0 } }", "", false)]
    [Arguments("new Owner { (object)result }", "", true, "OutOfMemoryException", "struct")]
    [Arguments("new Owner { (object)result }", "result.Dispose();", false, "OutOfMemoryException", "struct")]
    [Arguments("new Owner { (object)result }", "", false, "InvalidOperationException", "struct")]
    public async Task CollectionInitializerWaitsForAddArguments(string creation, string cleanup, bool warning, string catchType = "InvalidOperationException", string ownerKind = "class") => await Disposal.VerifyAsync($$"""
        using System;
        using System.Collections;
        using System.Threading.Tasks;
        using Respire;
        {{ownerKind}} Owner : IEnumerable
        {
            public void Add(object value) { }
            public void Add(RespireResult value) => value.Dispose();
            public void Add(RespireResult value, int other) => value.Dispose();
            public void Add((RespireResult, int) value, int other) => value.Item1.Dispose();
            public IEnumerator GetEnumerator() => throw new NotImplementedException();
        }
        class Caller
        {
            static int Throws() => throw new InvalidOperationException();
            async Task Run(RespireClient client)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { _ = {{creation}}; }
                catch ({{catchType}}) { {{cleanup}} }
            }
        }
        """);

    [Test]
    [Arguments(2, false, false)]
    [Arguments(65, false, true)]
    [Arguments(2, true, true)]
    [Arguments(65, true, true)]
    public async Task TransferFlagLimitRetainsWarning(int arms, bool throwingArgument, bool warning)
    {
        // Each selected owner reference reserves a distinct transfer flag before traversal.
        var selections = string.Join(", ", Enumerable.Range(0, arms - 1)
            .Select(index => $"{index} => (result, 0)").Append("_ => (result, 0)"));
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                static void Take((RespireResult, int) value, int other) => value.Item1.Dispose();
                static int Throws() => throw new InvalidOperationException();
                async Task Run(RespireClient client, int choice)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { Take(choice switch { {{selections}} }, {{(throwingArgument ? "Throws()" : "0")}}); }
                    catch (InvalidOperationException) { }
                }
            }
            """);
    }

    [Test]
    [Arguments("Take(_ = (result, 0));", false)]
    [Arguments("Take(_ = (result, 0), Throws());", true)]
    [Arguments("Take(flag switch { true => result, _ => result });", false)]
    [Arguments("Take(flag switch { true => result, _ => existing }, Throws());", true)]
    [Arguments("Take(flag switch { true => existing, _ => result }, Throws());", true)]
    [Arguments("Take(flag switch { true => result, _ => result }, 0);", false)]
    public async Task ConsumedExpressionsCompleteBeforeTransfer(string operation, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static int Throws() => throw new InvalidOperationException();
            static void Take(RespireResult value, int other = 0) => value.Dispose();
            static void Take((RespireResult, int) value, int other = 0) => value.Item1.Dispose();
            async Task Run(RespireClient client, bool flag, RespireResult existing)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { {{operation}} result.Dispose(); }
                catch (InvalidOperationException) { }
            }
        }
        """);

    [Test]
    [Arguments("holder = null;", "new Holder()", true)]
    [Arguments("holder = new Holder();", "null", false)]
    [Arguments("if (holder is null) return;", "null", false)]
    [Arguments("", "new Holder()", true)]
    public async Task CapturedReceiverRetainsOriginalNullState(string setup, string replacement, bool warning)
    {
        await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Holder { public RespireResult Value { set { value.Dispose(); } } }
        class Caller
        {
            async Task Run(RespireClient client, Holder holder)
            {
                {{setup}}
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { holder.Value = (holder = {{replacement}}) != null ? result : result; }
                catch (NullReferenceException) { }
            }
        }
        """);

        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Holder { public int Value; }
            class Caller
            {
                async Task Run(RespireClient client, Holder holder)
                {
                    {{setup}}
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { holder.Value = (holder = {{replacement}}) != null ? 1 : 0; await batch.SendAsync(); }
                    catch (NullReferenceException) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("(new string[1])[0] = \"x\";", false)]
    [Arguments("(new object[1])[0] = \"x\";", false)]
    [Arguments("strings[0] = \"x\";", false)]
    [Arguments("objects[0] = new object();", true)]
    [Arguments("((object[])new string[1])[0] = new object();", true)]
    [Arguments("((object[])new object[1])[0] = \"x\";", false)]
    public async Task ExactArrayElementTypeCannotViolateCovariance(string operation, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, string[] strings, object[] objects)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { {{operation}} result.Dispose(); }
                    catch (ArrayTypeMismatchException) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, string[] strings, object[] objects)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{operation}} await batch.SendAsync(); }
                    catch (ArrayTypeMismatchException) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("_ = (result, 0);", true)]
    [Arguments("_ = (object)result;", true)]
    [Arguments("_ = flag ? result : default;", true)]
    [Arguments("(_, _) = (result, 0);", true)]
    [Arguments("(_, (_, _)) = (0, (result, 0));", true)]
    [Arguments("RespireResult owner; (owner, _) = (result, 0);", false)]
    [Arguments("(RespireResult, int) _; _ = (result, 0);", false)]
    [Arguments("_ = new Owner(result);", false)]
    public async Task WrappedDiscardsDoNotTransferOwnership(string operation, bool warning) => await Disposal.VerifyAsync($$"""
        using System.Threading.Tasks;
        using Respire;
        class Owner { public Owner(RespireResult result) { result.Dispose(); } }
        class Caller
        {
            async Task Run(RespireClient client, bool flag)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                {{operation}}
            }
        }
        """);

    [Test]
    [Arguments("if (holder is null) return;", "Action action = holder.Method;", "NullReferenceException", false)]
    [Arguments("if (holder is null) return;", "Action action = holder.Method;", "OutOfMemoryException", true)]
    [Arguments("if (holder is null) return; holder = null;", "Action action = holder.Method;", "NullReferenceException", true)]
    [Arguments("", "Action action = holder.Method;", "NullReferenceException", true)]
    [Arguments("", "_ = (new int[1])[0];", "IndexOutOfRangeException", false)]
    [Arguments("", "_ = (new int[1])[1];", "IndexOutOfRangeException", true)]
    [Arguments("", "_ = (new int[1])[-1];", "IndexOutOfRangeException", true)]
    [Arguments("", "_ = (new int[1])[index];", "IndexOutOfRangeException", true)]
    [Arguments("", "_ = (new int[1])[0];", "OutOfMemoryException", true)]
    [Arguments("", "_ = (new int[1, 2])[0, 1];", "IndexOutOfRangeException", false)]
    [Arguments("", "_ = (new int[1, 2])[0, 2];", "IndexOutOfRangeException", true)]
    public async Task DelegateAndArrayChecksUseProvenOperands(string setup, string operation, string catchType, bool warning)
    {
        const string declaration = "class Holder { public void Method() { } }";
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{declaration}}
            class Caller
            {
                async Task Run(RespireClient client, Holder holder, int index)
                {
                    {{setup}}
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { {{operation}} result.Dispose(); }
                    catch ({{catchType}}) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{declaration}}
            class Caller
            {
                async Task Run(RespireClient client, Holder holder, int index)
                {
                    {{setup}}
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{operation}} await batch.SendAsync(); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("var pair = (1, 2); int a, b;", "(a, b) = pair;", "Exception", false)]
    [Arguments("var pair = (1, 2); object a, b;", "(a, b) = pair;", "OutOfMemoryException", true)]
    [Arguments("var pair = (1, (2, 3)); int a, b, c;", "(a, (b, c)) = pair;", "Exception", false)]
    [Arguments("var pair = (1, 2); int a; var holder = new Holder();", "(a, holder.Value) = pair;", "InvalidOperationException", true)]
    [Arguments("if (text is null) return;", "_ = text.Length;", "NullReferenceException", false)]
    [Arguments("if (values is null) return;", "_ = values.Length;", "NullReferenceException", false)]
    [Arguments("if (values is null) return;", "_ = values.LongLength;", "NullReferenceException", false)]
    [Arguments("if (text is null) return; text = null;", "_ = text.Length;", "NullReferenceException", true)]
    [Arguments("", "_ = text.Length;", "NullReferenceException", true)]
    [Arguments("", "_ = optional.Value;", "ArgumentException", false)]
    [Arguments("", "_ = optional.Value;", "InvalidOperationException", true)]
    [Arguments("if (optional is null) return;", "_ = optional.Value;", "InvalidOperationException", false)]
    [Arguments("if (optional == null) return;", "_ = optional.Value;", "InvalidOperationException", false)]
    [Arguments("if (!optional.HasValue) return;", "_ = optional.Value;", "InvalidOperationException", false)]
    [Arguments("if (optional.HasValue == false) return;", "_ = optional.Value;", "InvalidOperationException", false)]
    [Arguments("if (optional.HasValue != true) return;", "_ = (int)optional;", "InvalidOperationException", false)]
    [Arguments("if (!optional.HasValue) return; optional = null;", "_ = optional.Value;", "InvalidOperationException", true)]
    [Arguments("if (!GetOptional().HasValue) return;", "_ = optional.Value;", "InvalidOperationException", true)]
    [Arguments("if (optional is null) return; optional = null;", "_ = optional.Value;", "InvalidOperationException", true)]
    [Arguments("if (optional is null) return;", "_ = GetOptional().Value;", "InvalidOperationException", true)]
    [Arguments("", "_ = new int?().Value;", "InvalidOperationException", true)]
    [Arguments("if (optional is null) return;", "optional = new int?(); _ = optional.Value;", "InvalidOperationException", true)]
    [Arguments("", "_ = new int?(1).Value;", "InvalidOperationException", false)]
    [Arguments("", "optional = new int?(1); _ = optional.Value;", "InvalidOperationException", false)]
    [Arguments("", "_ = new int?(GetOptional().Value).Value;", "ArgumentException", true)]
    [Arguments("if (optional is null) return;", "_ = (int)optional;", "InvalidOperationException", false)]
    [Arguments("", "_ = (int)optional;", "InvalidOperationException", true)]
    [Arguments("if (optional is null) return; optional = null;", "_ = (int)optional;", "InvalidOperationException", true)]
    [Arguments("", "_ = (int)new int?();", "InvalidOperationException", true)]
    [Arguments("", "_ = (int)new int?(1);", "InvalidOperationException", false)]
    [Arguments("if (optional is null) return;", "_ = checked((byte)optional);", "InvalidOperationException", false)]
    [Arguments("if (optional is null) return;", "_ = checked((byte)optional);", "OverflowException", true)]
    [Arguments("if (boxed is null) return;", "_ = (int)boxed;", "NullReferenceException", false)]
    [Arguments("", "_ = (int)boxed;", "NullReferenceException", true)]
    [Arguments("if (boxed is null) return; boxed = null;", "_ = (int)boxed;", "NullReferenceException", true)]
    [Arguments("if (boxed is null) return;", "_ = (int)boxed;", "InvalidCastException", true)]
    [Arguments("", "_ = GetOptional().Value;", "ArgumentException", true)]
    [Arguments("", "_ = optional.GetValueOrDefault();", "ArgumentException", false)]
    [Arguments("", "_ = GetOptional().GetValueOrDefault();", "ArgumentException", true)]
    [Arguments("", "_ = optional.GetValueOrDefault(GetOptional().Value);", "ArgumentException", true)]
    public async Task TupleAndFrameworkOperationsUsePreciseFailures(string setup, string operation, string catchType, bool warning)
    {
        const string declaration = "class Holder { public int Value { set { throw new InvalidOperationException(); } } }";
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{declaration}}
            class Caller
            {
                static int? GetOptional() => throw new ArgumentException();
                async Task Run(RespireClient client, string text, int[] values, int? optional, object boxed)
                {
                    {{setup}}
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { {{operation}} result.Dispose(); }
                    catch ({{catchType}}) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{declaration}}
            class Caller
            {
                static int? GetOptional() => throw new ArgumentException();
                async Task Run(RespireClient client, string text, int[] values, int? optional, object boxed)
                {
                    {{setup}}
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{operation}} await batch.SendAsync(); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("", "TypeInitializationException", true)]
    [Arguments("Owner.Initialize();", "TypeInitializationException", false)]
    [Arguments("", "InvalidOperationException", false)]
    public async Task StructInitializationPrecedesConstructorTransfer(string setup, string catchType, bool warning)
    {
        const string declaration = """
            struct Owner
            {
                static Owner() { }
                public static void Initialize() { }
                public Owner(RespireResult result) { result.Dispose(); }
                public Owner(RespireBatch batch) { batch.SendAsync().AsTask().GetAwaiter().GetResult(); }
            }
            """;
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{declaration}}
            class Caller
            {
                async Task Run(RespireClient client)
                {
                    {{setup}}
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { _ = new Owner(result); }
                    catch ({{catchType}}) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using Respire;
            {{declaration}}
            class Caller
            {
                void Run(RespireClient client)
                {
                    {{setup}}
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { _ = new Owner(batch); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("optional.HasValue", false)]
    [Arguments("GetOptional().HasValue", true)]
    [Arguments("custom.HasValue", true)]
    public async Task NullableHasValueOnlyThrowsFromItsReceiver(string expression, bool warning)
    {
        const string declaration = "class Custom { public bool HasValue => throw new InvalidOperationException(); }";
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{declaration}}
            class Caller
            {
                static int? GetOptional() => throw new InvalidOperationException();
                async Task Run(RespireClient client, int? optional, Custom custom)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { _ = {{expression}}; result.Dispose(); }
                    catch (InvalidOperationException) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{declaration}}
            class Caller
            {
                static int? GetOptional() => throw new InvalidOperationException();
                async Task Run(RespireClient client, int? optional, Custom custom)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { _ = {{expression}}; await batch.SendAsync(); }
                    catch (InvalidOperationException) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("(choice, holder.Number, holder.Value) = (false, 0, result);", true)]
    [Arguments("(holder.Number, choice, holder.Number, holder.Value) = (0, false, 0, result);", true)]
    [Arguments("(holder.Number, holder.Value, choice) = (0, result, false);", false)]
    [Arguments("(choice, holder.Number) = (false, 0); result.Dispose();", true)]
    [Arguments("(holder.Number, choice) = (0, false); result.Dispose();", false)]
    public async Task DeconstructionStoresInvalidatePredicatesInOrder(string operation, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Holder
        {
            public int Number { set { throw new InvalidOperationException(); } }
            public RespireResult Value { set { value.Dispose(); } }
        }
        class Caller
        {
            async Task Run(RespireClient client, bool choice, Holder holder)
            {
                if (!choice) return;
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { {{operation}} }
                catch (InvalidOperationException) { if (choice) result.Dispose(); }
            }
        }
        """);

    [Test]
    [Arguments("(values[0], _) = (new object(), 0);", "ArrayTypeMismatchException", true)]
    [Arguments("(_, (values[0], _)) = (0, (new object(), 0));", "ArrayTypeMismatchException", true)]
    [Arguments("(values[1], _) = (new object(), 0);", "IndexOutOfRangeException", true)]
    [Arguments("(values[0], _) = (new object(), 0);", "NullReferenceException", false)]
    [Arguments("(holder.Value, _) = (new object(), 0);", "InvalidOperationException", true)]
    public async Task DeconstructionStoresCanBypassCleanup(string operation, string catchType, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Holder { public object Value { set { throw new InvalidOperationException(); } } }
            class Caller
            {
                async Task Run(RespireClient client, Holder holder)
                {
                    object[] values = new string[1];
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { {{operation}} result.Dispose(); }
                    catch ({{catchType}}) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Holder { public object Value { set { throw new InvalidOperationException(); } } }
            class Caller
            {
                async Task Run(RespireClient client, Holder holder)
                {
                    object[] values = new string[1];
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{operation}} await batch.SendAsync(); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("Microsoft.CSharp.RuntimeBinder.RuntimeBinderException", true)]
    [Arguments("OutOfMemoryException", true)]
    public async Task DynamicConstructorFailurePrecedesTransfer(string catchType, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Owner { public Owner(RespireResult result, int value) { result.Dispose(); } }
        class Caller
        {
            async Task Run(RespireClient client, dynamic argument)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { _ = new Owner(result, argument); }
                catch ({{catchType}}) { }
            }
        }
        """);

    [Test]
    [Arguments("if (values is null) return;", "_ = values[0];", "NullReferenceException", false)]
    [Arguments("if (values is null) return;", "_ = values[0];", "IndexOutOfRangeException", true)]
    [Arguments("if (values is null) return;", "values[0] = new object();", "ArrayTypeMismatchException", true)]
    [Arguments("if (values is null) return;", "values[0] = null;", "ArrayTypeMismatchException", false)]
    [Arguments("if (values is null) return;", "values[0] = (object)null;", "ArrayTypeMismatchException", false)]
    [Arguments("if (values is null) return;", "values[0] = default(object);", "ArrayTypeMismatchException", false)]
    [Arguments("if (values is null) return;", "(values[0], _) = ((object)null, 0);", "ArrayTypeMismatchException", false)]
    [Arguments("", "values[0] = null;", "NullReferenceException", true)]
    [Arguments("if (values is null) return;", "values[0] = null;", "IndexOutOfRangeException", true)]
    [Arguments("if (values is null) return; values = null;", "_ = values[0];", "NullReferenceException", true)]
    [Arguments("", "_ = values[0];", "NullReferenceException", true)]
    public async Task ArrayReceiverUsesNonNullEvidence(string setup, string operation, string catchType, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, object[] values)
                {
                    {{setup}}
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { {{operation}} result.Dispose(); }
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
                async Task Run(RespireClient client, object[] values)
                {
                    {{setup}}
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{operation}} await batch.SendAsync(); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("_ = Holder.Value;", false)]
    [Arguments("Holder.Initialize();", false)]
    [Arguments("try { Holder.Initialize(); } catch (InvalidOperationException) { }", false)]
    [Arguments("try { _ = Holder.Property; } catch (InvalidOperationException) { }", false)]
    [Arguments("try { Holder.Property = 1; } catch (InvalidOperationException) { }", false)]
    [Arguments("try { _ = new Holder(); } catch (InvalidOperationException) { }", false)]
    [Arguments("try { _ = new Holder(); } catch (OutOfMemoryException) { }", true)]
    [Arguments("try { Holder.Initialize(); } catch (TypeInitializationException) { }", true)]
    [Arguments("try { Holder.Initialize(); } catch { }", true)]
    [Arguments("try { if (flag) Holder.Initialize(); } catch (InvalidOperationException) { }", true)]
    [Arguments("_ = Holder.Property;", false)]
    [Arguments("Holder.Property = 1;", false)]
    [Arguments("_ = Holder.Value; try { throw new Exception(); } catch { }", false)]
    [Arguments("_ = new Holder();", false)]
    [Arguments("if (flag) _ = Holder.Value;", true)]
    [Arguments("try { _ = Holder.Value; } catch { }", true)]
    [Arguments("", true)]
    public async Task SuccessfulTypeInitializationIsRemembered(string setup, bool warning)
    {
        const string declaration = """
            class Holder
            {
                static Holder() { }
                public static int Value;
                public static int Property { get; set; }
                public static void Initialize() { }
                public static void Take(RespireResult result) => result.Dispose();
                public static void Take(RespireBatch batch) => batch.SendAsync().AsTask().GetAwaiter().GetResult();
            }
            """;
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{declaration}}
            class Caller
            {
                async Task Run(RespireClient client, bool flag)
                {
                    {{setup}}
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { Holder.Take(result); }
                    catch (TypeInitializationException) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using Respire;
            {{declaration}}
            class Caller
            {
                void Run(RespireClient client, bool flag)
                {
                    {{setup}}
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { Holder.Take(batch); }
                    catch (TypeInitializationException) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("Action owner = () => result.Dispose();", "OutOfMemoryException", true)]
    [Arguments("Action owner = delegate { result.Dispose(); };", "OutOfMemoryException", true)]
    [Arguments("Action owner = new Action(() => result.Dispose());", "OutOfMemoryException", true)]
    [Arguments("Action owner = () => result.Dispose();", "InvalidOperationException", false)]
    [Arguments("Action owner = result.Dispose;", "OutOfMemoryException", true)]
    [Arguments("Action owner = new Action(result.Dispose);", "OutOfMemoryException", true)]
    [Arguments("Action owner = (Action)result.Dispose;", "OutOfMemoryException", true)]
    [Arguments("Action owner = result.Dispose;", "InvalidOperationException", false)]
    [Arguments("Action owner = result.Dispose;", "OutOfMemoryException", false, "result.Dispose();")]
    [Arguments("Take(() => result.Dispose(), Throws());", "InvalidOperationException", true)]
    [Arguments("Take(() => result.Dispose(), 0);", "InvalidOperationException", false)]
    [Arguments("Take(() => result.Dispose(), Throws());", "InvalidOperationException", false, "result.Dispose();")]
    [Arguments("Take(new Action(() => result.Dispose()), Throws());", "InvalidOperationException", true)]
    [Arguments("Take(result.Dispose, Throws());", "InvalidOperationException", true)]
    [Arguments("var owner = new { Callback = (Action)(() => result.Dispose()), Other = Throws() };", "InvalidOperationException", true)]
    [Arguments("Take(() => result.Dispose(), flag ? 0 : 1);", "InvalidOperationException", false)]
    [Arguments("Take(() => result.Dispose(), flag ? Throws() : 1);", "InvalidOperationException", true)]
    public async Task CapturedOwnerWaitsForDelegateAllocation(string capture, string catchType, bool warning, string cleanup = "") => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static void Take(Action dispose, int other) { }
            static int Throws() => throw new InvalidOperationException();
            async Task Run(RespireClient client, bool flag)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { {{capture}} }
                catch ({{catchType}}) { {{cleanup}} }
            }
        }
        """);

    [Test]
    [Arguments("if (holder is not null) return;", "holder ??= result;", "OutOfMemoryException", "", true)]
    [Arguments("if (holder is not null) return;", "holder ??= result;", "OutOfMemoryException", "result.Dispose();", false)]
    [Arguments("if (holder is not null) return;", "holder ??= result;", "InvalidOperationException", "", false)]
    [Arguments("holder = new object();", "holder ??= result;", "OutOfMemoryException", "", true)]
    [Arguments("", "target.Value ??= result;", "OutOfMemoryException", "", true)]
    [Arguments("", "buffer[index] ??= result;", "OutOfMemoryException", "", true)]
    [Arguments("", "buffer[index] ??= result;", "IndexOutOfRangeException", "", true)]
    [Arguments("", "holder = result;", "OutOfMemoryException", "", true)]
    [Arguments("", "holder = result;", "OutOfMemoryException", "result.Dispose();", false)]
    [Arguments("", "holder = result;", "InvalidOperationException", "", false)]
    public async Task AssignmentWaitsForConversion(string setup, string assignment, string catchType, string cleanup, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Holder { public object Value { get; set; } }
        class Caller
        {
            async Task Run(RespireClient client, Holder target, object[] buffer, int index, object holder)
            {
                {{setup}}
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { {{assignment}} }
                catch ({{catchType}}) { {{cleanup}} }
            }
        }
        """);

    [Test]
    [Arguments("holder.Field", "if (holder is null) return;", "NullReferenceException", false)]
    [Arguments("holder.Field", "if (holder is null) return; holder = null;", "NullReferenceException", true)]
    [Arguments("holder.Field", "", "NullReferenceException", true)]
    [Arguments("value / 2", "", "DivideByZeroException", false)]
    [Arguments("value / 'a'", "", "DivideByZeroException", false)]
    [Arguments("value % -2", "", "DivideByZeroException", false)]
    [Arguments("value / divisor", "", "DivideByZeroException", true)]
    [Arguments("value / -1", "", "OverflowException", true)]
    [Arguments("fraction / 2m", "", "DivideByZeroException", false)]
    [Arguments("fraction % 0m", "", "DivideByZeroException", true)]
    [Arguments("nullable / (int?)2", "", "DivideByZeroException", false)]
    public async Task KnownOperandsExcludeImpossibleFailures(string operation, string setup, string catchType, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Holder { public int Field; }
            class Caller
            {
                async Task Run(RespireClient client, Holder holder, int value, int divisor, decimal fraction, int? nullable)
                {
                    {{setup}}
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { _ = {{operation}}; result.Dispose(); }
                    catch ({{catchType}}) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Holder { public int Field; }
            class Caller
            {
                async Task Run(RespireClient client, Holder holder, int value, int divisor, decimal fraction, int? nullable)
                {
                    {{setup}}
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { _ = {{operation}}; await batch.SendAsync(); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("while (true) { }", false)]
    [Arguments("loop: goto loop;", false)]
    [Arguments("while (true) { await Task.Yield(); }", false)]
    [Arguments("while (true) { }", true)]
    public async Task ReachableOwnerWithoutReleaseWarnsInInfiniteLoop(string loop, bool dispose) => await Disposal.VerifyAsync($$"""
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            async Task Run(RespireClient client)
            {
                var {{(dispose ? "result" : "{|RESP001:result|}")}} = await client.ExecuteAsync("PING");
                {{(dispose ? "result.Dispose();" : "")}}
                {{loop}}
            }
        }
        """);

    [Test]
    [Arguments("throw null;", "InvalidOperationException", false)]
    [Arguments("throw null;", "NullReferenceException", true)]
    [Arguments("throw new ArgumentException();", "InvalidOperationException", false)]
    [Arguments("throw new ArgumentException();", "OutOfMemoryException", true)]
    [Arguments("Throws();", "InvalidOperationException", true)]
    public async Task CatchOriginRequiresApplicableException(string operation, string catchType, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                static void Throws() => throw new InvalidOperationException();
                async Task Run(RespireClient client)
                {
                    try { {{operation}} }
                    catch ({{catchType}})
                    {
                        var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using Respire;
            class Caller
            {
                static void Throws() => throw new InvalidOperationException();
                void Run(RespireClient client)
                {
                    try { {{operation}} }
                    catch ({{catchType}})
                    {
                        var batch = client.CreateBatch();
                        var pending = batch.GetStringAsync("key");
                        Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                    }
                }
            }
            """);
    }

    [Test]
    public async Task YieldingOwnerTransfersBeforeIteratorDisposal() => await Disposal.VerifyAsync("""
        using System.Collections.Generic;
        using Respire;
        class Caller
        {
            async IAsyncEnumerable<RespireResult> Run(RespireClient client)
            {
                var result = await client.ExecuteAsync("PING");
                yield return result;
            }
        }
        """);

    [Test]
    [Arguments("(string)boxed", "InvalidCastException", true)]
    [Arguments("(string)boxed", "InvalidOperationException", false)]
    [Arguments("(int)boxed", "InvalidCastException", true)]
    [Arguments("(int)boxed", "NullReferenceException", true)]
    [Arguments("(int)boxed", "InvalidOperationException", false)]
    [Arguments("(int?)boxed", "NullReferenceException", false)]
    [Arguments("(int?)boxed", "InvalidCastException", true)]
    [Arguments("(int)nullable", "InvalidOperationException", true)]
    [Arguments("(int)nullable", "InvalidCastException", false)]
    [Arguments("checked((byte)number)", "OverflowException", true)]
    [Arguments("checked((byte)number)", "InvalidOperationException", false)]
    [Arguments("checked((byte)nullable)", "InvalidOperationException", true)]
    [Arguments("checked((byte)nullable)", "OverflowException", true)]
    [Arguments("checked((byte?)nullable)", "InvalidOperationException", false)]
    [Arguments("checked((byte?)nullable)", "OverflowException", true)]
    [Arguments("(int)fraction", "OverflowException", true)]
    [Arguments("(int)fraction", "InvalidOperationException", false)]
    [Arguments("(double)fraction", "OverflowException", false)]
    [Arguments("checked((long)nullable)", "OverflowException", false)]
    [Arguments("checked((long)nullable)", "InvalidOperationException", true)]
    [Arguments("(string)GetObject()", "InvalidOperationException", true)]
    public async Task BuiltInConversionsUseSpecificExceptions(string conversion, string catchType, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                static object GetObject() => throw new InvalidOperationException();
                async Task Run(RespireClient client, object boxed, int? nullable, int number, decimal fraction)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { _ = {{conversion}}; result.Dispose(); }
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
                static object GetObject() => throw new InvalidOperationException();
                async Task Run(RespireClient client, object boxed, int? nullable, int number, decimal fraction)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { _ = {{conversion}}; await batch.SendAsync(); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    public async Task DeconstructionEvaluatesIndexBeforeRhsAtRuntime()
    {
        var flag = false;
        var rhsValue = true;
        int[] array = [0];
        var value = 0;
        try { (array[Throws()], value) = ((flag = rhsValue) ? 0 : 0, 1); }
        catch (InvalidOperationException) { }
        await Assert.That(flag).IsFalse();
        await Assert.That(value).IsEqualTo(0);

        static int Throws() => throw new InvalidOperationException();
    }

    [Test]
    [Arguments("var owner = (result, Throws());", "Exception", true)]
    [Arguments("object owner = result;", "OutOfMemoryException", true)]
    [Arguments("Owner owner = result;", "InvalidOperationException", true)]
    [Arguments("var owner = (true ? result : default, Throws());", "Exception", true)]
    [Arguments("IDisposable owner = result;", "OutOfMemoryException", true)]
    [Arguments("object owner = result;", "InvalidOperationException", false)]
    [Arguments("var owner = (result, 0);", "InvalidOperationException", false)]
    [Arguments("RespireResult owner = result;", "OutOfMemoryException", false)]
    public async Task LocalInitializerCompletesBeforeTransfer(string initializer, string catchType, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Owner
        {
            public static implicit operator Owner(RespireResult result) => throw new InvalidOperationException();
        }
        class Caller
        {
            static int Throws() => throw new Exception();
            async Task Run(RespireClient client)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { {{initializer}} }
                catch ({{catchType}}) { }
            }
        }
        """);

    [Test]
    [Arguments("flag", true)]
    [Arguments("true", false)]
    public async Task DeconstructionTargetExceptionsPrecedeRhsWrites(string cleanupCondition, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Holder { public RespireResult Value { set { value.Dispose(); } } }
        class Caller
        {
            static int Throws() => throw new Exception();
            async Task Run(RespireClient client, bool flag, int[] array, Holder holder)
            {
                if (flag) return;
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { (array[Throws()], holder.Value) = ((flag = true) ? 0 : 0, result); }
                catch { if ({{cleanupCondition}}) result.Dispose(); }
            }
        }
        """);

    [Test]
    [Arguments("Holder.Run();")]
    [Arguments("Holder.Value = 1;")]
    [Arguments("_ = Holder.Value;")]
    public async Task StaticMemberBodyCanBypassCleanup(string operation)
    {
        const string declaration = """
            class Holder
            {
                static Holder() { }
                public static void Run() => throw new InvalidOperationException();
                public static int Value
                {
                    get => throw new InvalidOperationException();
                    set => throw new InvalidOperationException();
                }
            }
            """;
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{declaration}}
            class Caller
            {
                async Task Run(RespireClient client)
                {
                    var {|RESP001:result|} = await client.ExecuteAsync("PING");
                    try { {{operation}} result.Dispose(); }
                    catch (InvalidOperationException) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{declaration}}
            class Caller
            {
                async Task Run(RespireClient client)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { {{operation}} await batch.SendAsync(); }
                    catch (InvalidOperationException) { }
                    Console.WriteLine({|RESP002:pending.Result|});
                }
            }
            """);
    }

    [Test]
    [Arguments("TypeInitializationException", true)]
    [Arguments("InvalidOperationException", false)]
    public async Task StaticInitializationPrecedesBatchEscape(string catchType, bool warning) => await Pending.VerifyAsync($$"""
        using System;
        using Respire;
        class Holder
        {
            static Holder() { throw new InvalidOperationException(); }
            public static void Take(RespireBatch batch) => batch.SendAsync().AsTask().GetAwaiter().GetResult();
        }
        class Caller
        {
            void Run(RespireClient client)
            {
                var batch = client.CreateBatch();
                var pending = batch.GetStringAsync("key");
                try { Holder.Take(batch); }
                catch ({{catchType}}) { }
                Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
            }
        }
        """);

    [Test]
    [Arguments("TypeInitializationException", true)]
    [Arguments("InvalidOperationException", false)]
    [Arguments("Exception", true)]
    public async Task StaticFieldInitializationUsesWrappedException(string catchType, bool warning)
    {
        const string declaration = "class Holder { static Holder() { throw new InvalidOperationException(); } public static int Value; }";
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{declaration}}
            class Caller
            {
                async Task Run(RespireClient client)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { _ = Holder.Value; result.Dispose(); }
                    catch ({{catchType}}) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{declaration}}
            class Caller
            {
                async Task Run(RespireClient client)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { _ = Holder.Value; await batch.SendAsync(); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("Holder.Take(result);", "TypeInitializationException", true)]
    [Arguments("Holder.Take(result);", "InvalidOperationException", false)]
    [Arguments("Holder.Value = result;", "TypeInitializationException", true)]
    [Arguments("Holder.Field = result;", "InvalidOperationException", false)]
    [Arguments("_ = new Holder(result);", "TypeInitializationException", true)]
    [Arguments("_ = new Holder(result);", "InvalidOperationException", false)]
    public async Task StaticInitializationPrecedesTransfer(string transfer, string catchType, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Holder
        {
            static Holder() { throw new InvalidOperationException(); }
            public Holder(RespireResult result) { result.Dispose(); }
            public static void Take(RespireResult result) => result.Dispose();
            public static RespireResult Value { set { value.Dispose(); } }
            public static RespireResult Field;
        }
        class Caller
        {
            async Task Run(RespireClient client)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { {{transfer}} }
                catch ({{catchType}}) { }
            }
        }
        """);

    [Test]
    [Arguments("(holder.Value, _) = (result, Throws());", "Exception", true)]
    [Arguments("(holder.Value, _) = (result, 0);", "InvalidOperationException", false)]
    [Arguments("(holder.Value, _) = (result, 0);", "NullReferenceException", true)]
    [Arguments("(holder.Number, holder.Value) = (0, result);", "InvalidOperationException", true)]
    [Arguments("(holder.Value, holder.Number) = (result, 0);", "InvalidOperationException", false)]
    public async Task DeconstructionEvaluatesRhsBeforeTransfer(string transfer, string catchType, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Holder
        {
            public RespireResult Value { set { value.Dispose(); } }
            public int Number { set { throw new InvalidOperationException(); } }
        }
        class Caller
        {
            static int Throws() => throw new InvalidOperationException();
            async Task Run(RespireClient client, Holder holder)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { {{transfer}} }
                catch ({{catchType}}) { }
            }
        }
        """);

    [Test]
    [Arguments("object", "OutOfMemoryException", true)]
    [Arguments("IDisposable", "OutOfMemoryException", true)]
    [Arguments("object", "InvalidOperationException", false)]
    [Arguments("RespireResult", "OutOfMemoryException", false)]
    public async Task DirectReturnWaitsForImplicitConversion(string returnType, string catchType, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            async Task<{{returnType}}> Run(RespireClient client)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { return result; }
                catch ({{catchType}}) { return default; }
            }
        }
        """);

    [Test]
    [Arguments("holder[result, 0] = Throws();", "Exception", true)]
    [Arguments("holder[result, Throws()] = 0;", "Exception", true)]
    [Arguments("holder[result, 0] = 0;", "NullReferenceException", true)]
    [Arguments("holder[result, 0] = 0;", "InvalidOperationException", false)]
    [Arguments("_ = holder[result, Throws()];", "Exception", true)]
    [Arguments("_ = holder[result, 0];", "InvalidOperationException", false)]
    [Arguments("holder[result, 0] += Throws();", "InvalidOperationException", false)]
    public async Task IndexerTransferWaitsForAccessor(string transfer, string catchType, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Holder
        {
            public int this[RespireResult result, int ignored]
            {
                get { result.Dispose(); return 0; }
                set { result.Dispose(); }
            }
        }
        class Caller
        {
            static int Throws() => throw new InvalidOperationException();
            async Task Run(RespireClient client, Holder holder)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { {{transfer}} }
                catch ({{catchType}}) { }
            }
        }
        """);

    [Test]
    [Arguments("sealed class Token { }", "new Token()", "InvalidOperationException", false)]
    [Arguments("sealed class Token { }", "new Token()", "OutOfMemoryException", true)]
    [Arguments("", "new object()", "InvalidOperationException", false)]
    [Arguments("class Token { public Token() { throw new InvalidOperationException(); } }", "new Token()", "InvalidOperationException", true)]
    [Arguments("class Token { int field = Throw(); static int Throw() => throw new InvalidOperationException(); }", "new Token()", "InvalidOperationException", true)]
    [Arguments("class Token { public int Value { get; } = Throw(); static int Throw() => throw new InvalidOperationException(); }", "new Token()", "InvalidOperationException", true)]
    [Arguments("class Base { public Base() { throw new InvalidOperationException(); } } class Token : Base { }", "new Token()", "InvalidOperationException", true)]
    [Arguments("class Token { static Token() { throw new Exception(); } }", "new Token()", "TypeInitializationException", true)]
    public async Task TrivialReferenceConstructionOnlyAddsAllocationFailure(string declaration, string expression, string catchType, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{declaration}}
            class Caller
            {
                async Task Run(RespireClient client)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { _ = {{expression}}; result.Dispose(); }
                    catch ({{catchType}}) { }
                }
            }
            """);
        await Pending.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            {{declaration}}
            class Caller
            {
                async Task Run(RespireClient client)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { _ = {{expression}}; await batch.SendAsync(); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("sbyte", "checked { value /= -1; }", "OverflowException", true)]
    [Arguments("short", "checked { value /= -1; }", "OverflowException", true)]
    [Arguments("sbyte?", "checked { value /= -1; }", "OverflowException", true)]
    [Arguments("short?", "checked { value /= -1; }", "OverflowException", true)]
    [Arguments("byte", "checked { value /= 2; }", "OverflowException", false)]
    [Arguments("ushort", "checked { value /= 2; }", "OverflowException", false)]
    [Arguments("sbyte", "unchecked { value /= -1; }", "OverflowException", false)]
    [Arguments("sbyte", "checked { value %= -1; }", "OverflowException", false)]
    [Arguments("sbyte", "checked { value /= -1; }", "InvalidOperationException", false)]
    [Arguments("int", "checked { value /= divisor; }", "OverflowException", false, "ushort")]
    [Arguments("short", "checked { value /= divisor; }", "OverflowException", false, "byte")]
    [Arguments("short", "checked { value /= divisor; }", "DivideByZeroException", true, "byte")]
    [Arguments("sbyte", "checked { value /= divisor; }", "OverflowException", true, "sbyte")]
    [Arguments("short?", "checked { value /= divisor; }", "OverflowException", false, "byte?")]
    [Arguments("sbyte", "checked { value /= 2; }", "OverflowException", false)]
    [Arguments("sbyte", "checked { value /= -2; }", "OverflowException", false)]
    public async Task CompoundDivisionIncludesCheckedResultConversion(string type, string expression, string catchType, bool warning, string divisorType = "int")
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                async Task Run(RespireClient client, {{type}} value, {{divisorType}} divisor)
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
                async Task Run(RespireClient client, {{type}} value, {{divisorType}} divisor)
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
    [Arguments("_ = checked(value + 1);", "InvalidOperationException", false)]
    [Arguments("_ = checked(value + 1);", "OverflowException", true)]
    [Arguments("_ = checked(-value);", "InvalidOperationException", false)]
    [Arguments("_ = checked(-value);", "OverflowException", true)]
    [Arguments("checked { value++; }", "InvalidOperationException", false)]
    [Arguments("checked { value++; }", "OverflowException", true)]
    [Arguments("checked { value *= 2; }", "OverflowException", true)]
    [Arguments("_ = value / divisor;", "InvalidOperationException", false)]
    [Arguments("_ = value / divisor;", "DivideByZeroException", true)]
    [Arguments("_ = value / divisor;", "OverflowException", true)]
    [Arguments("_ = (uint)value / (uint)divisor;", "OverflowException", false)]
    [Arguments("_ = value % divisor;", "DivideByZeroException", true)]
    [Arguments("_ = value + Throws();", "InvalidOperationException", true)]
    [Arguments("_ = -value;", "OverflowException", false, "decimal")]
    [Arguments("_ = checked(-value);", "OverflowException", false, "decimal")]
    [Arguments("_ = unchecked(-value);", "OverflowException", false, "decimal")]
    [Arguments("_ = -value;", "OverflowException", false, "decimal?")]
    [Arguments("_ = value * value;", "OverflowException", true, "decimal")]
    public async Task ArithmeticUsesSpecificExceptionTypes(string expression, string catchType, bool warning, string type = "int")
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                static int Throws() => throw new InvalidOperationException();
                async Task Run(RespireClient client, {{type}} value, {{type}} divisor)
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
                static int Throws() => throw new InvalidOperationException();
                async Task Run(RespireClient client, {{type}} value, {{type}} divisor)
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
    [Arguments("new int[1]", "InvalidOperationException", false)]
    [Arguments("new int[1]", "OverflowException", false)]
    [Arguments("new int[buffer.Length]", "OverflowException", false)]
    [Arguments("new int[buffer.Length - 1]", "OverflowException", true)]
    [Arguments("new int[buffer.LongLength]", "OverflowException", false)]
    [Arguments("new int[text.Length]", "OverflowException", false)]
    [Arguments("new int[text.Length]", "InvalidOperationException", false)]
    [Arguments("new int[buffer.Length]", "NullReferenceException", true)]
    [Arguments("new int[buffer.Length]", "OutOfMemoryException", true)]
    [Arguments("new int[1]", "OutOfMemoryException", true)]
    [Arguments("new int[length]", "OverflowException", true)]
    [Arguments("new int[length]", "InvalidOperationException", false)]
    [Arguments("new int[1, 2]", "OverflowException", false)]
    [Arguments("new int[1, length]", "OverflowException", true)]
    [Arguments("new int[Length()]", "InvalidOperationException", true)]
    [Arguments("new[] { Length() }", "InvalidOperationException", true)]
    public async Task ArrayCreationUsesAllocationAndLengthExceptions(string expression, string catchType, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                static int Length() => throw new InvalidOperationException();
                async Task Run(RespireClient client, int length, int[] buffer, string text)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { _ = {{expression}}; result.Dispose(); }
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
                static int Length() => throw new InvalidOperationException();
                async Task Run(RespireClient client, int length, int[] buffer, string text)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    try { _ = {{expression}}; await batch.SendAsync(); }
                    catch ({{catchType}}) { }
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("int", "left == right", "left == right", "", false)]
    [Arguments("int", "left != right", "!(left == right)", "", false)]
    [Arguments("int", "left <= right", "!(left > right)", "", false)]
    [Arguments("bool", "left == right", "left == right", "", false)]
    [Arguments("bool", "left & right", "left & right", "", false)]
    [Arguments("bool", "left | right", "left | right", "", false)]
    [Arguments("bool", "left ^ right", "left ^ right", "", false)]
    [Arguments("bool", "left ^ right", "left ^ right", "left = !left;", true)]
    [Arguments("bool", "left & right", "left & right", "right = !right;", true)]
    [Arguments("bool", "left ^ true", "!left", "", false)]
    [Arguments("bool", "left & true", "left", "", false)]
    [Arguments("bool", "false | left", "left", "", false)]
    [Arguments("string", "left == right", "left == right", "", false)]
    [Arguments("DayOfWeek", "left == right", "left == right", "", false)]
    [Arguments("double", "left < right", "left < right", "", false)]
    [Arguments("double", "!(left > right)", "left <= right", "", true)]
    [Arguments("int", "left == right", "left == right", "left++;", true)]
    [Arguments("int", "left == right", "left == right", "right++;", true)]
    [Arguments("int", "left == right", "left == right", "Reset(ref right);", true)]
    [Arguments("int", "left == right", "left == right", "Action mutate = () => right++; mutate();", true)]
    [Arguments("dynamic", "left == right", "left == right", "", true)]
    public async Task VariableComparisonsRequireBothOperandsStable(string type, string selection, string cleanup, string mutation, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                static void Reset(ref int value) => value++;
                async Task Run(RespireClient client, RespireResult existing, {{type}} left, {{type}} right)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = {{selection}} ? await client.ExecuteAsync("PING") : existing;
                    {{mutation}}
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
                static void Reset(ref int value) => value++;
                async Task Run(RespireClient client, {{type}} left, {{type}} right)
                {
                    var first = client.CreateBatch();
                    var second = client.CreateBatch();
                    var pending = {{selection}} ? first.GetStringAsync("a") : second.GetStringAsync("b");
                    {{mutation}}
                    if ({{cleanup}}) await first.SendAsync(); else await second.SendAsync();
                    Console.WriteLine({{(warning ? "{|RESP002:pending.Result|}" : "pending.Result")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("OutOfMemoryException", true)]
    [Arguments("InvalidOperationException", false)]
    public async Task ReturnConversionPrecedesOwnershipTransfer(string catchType, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            async Task<object> Run(RespireClient client)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { return (object)result; }
                catch ({{catchType}}) { return null; }
            }
        }
        """);

    [Test]
    [Arguments("(result, Throw())", true, false)]
    [Arguments("(result, 0)", false, false)]
    [Arguments("(choice ? result : existing, 0)", false, true)]
    [Arguments("choice ? (result, 0) : (result, 1)", false, false)]
    [Arguments("((RespireResult)result, Throw())", false, true)]
    public async Task TupleReturnWaitsForAllElements(string returned, bool cleanupInCatch, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static int Throw() => throw new InvalidOperationException();
            async Task<(RespireResult, int)> Run(RespireClient client, RespireResult existing, bool choice)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { return {{returned}}; }
                catch (InvalidOperationException)
                {
                    {{(cleanupInCatch ? "result.Dispose();" : "")}}
                    return default;
                }
            }
        }
        """);

    [Test]
    public async Task TupleReturnWithThrowingSecondElementRetainsWarning() => await Disposal.VerifyAsync("""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static int Throws() => throw new InvalidOperationException();
            async Task<(RespireResult, int)> Run(RespireClient client)
            {
                var {|RESP001:result|} = await client.ExecuteAsync("PING");
                try { return (result, Throws()); }
                catch { return default; }
            }
        }
        """);

    [Test]
    [Arguments("holder.Value = (RespireResult)result;", "NullReferenceException", true)]
    [Arguments("holder.Field = choice ? result : existing;", "NullReferenceException", true)]
    [Arguments("holder.Field = choice ? result : existing;", "InvalidOperationException", true)]
    [Arguments("holder.Field = choice ? result : result;", "InvalidOperationException", false)]
    [Arguments("holder.Value = (RespireResult)result;", "InvalidOperationException", false)]
    [Arguments("buffer[0] = (RespireResult)result;", "IndexOutOfRangeException", true)]
    public async Task WrappedAssignmentWaitsForTargetChecks(string transfer, string catchType, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Holder
        {
            public RespireResult Field;
            public RespireResult Value { set { value.Dispose(); } }
        }
        class Caller
        {
            async Task Run(RespireClient client, Holder holder, RespireResult existing, RespireResult[] buffer, bool choice)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { {{transfer}} }
                catch ({{catchType}}) { }
            }
        }
        """);

    [Test]
    [Arguments("_ = new Owner(result);", "OutOfMemoryException", false, true)]
    [Arguments("_ = new Owner(result);", "Exception", false, true)]
    [Arguments("_ = new Owner(result);", "OutOfMemoryException", true, false)]
    [Arguments("_ = new Owner(result);", "InvalidOperationException", false, false)]
    [Arguments("_ = new Owner(result) { Value = 1 };", "OutOfMemoryException", false, true)]
    [Arguments("Owner owner = new(result) { Value = 1 };", "OutOfMemoryException", false, true)]
    [Arguments("_ = new ValueOwner(result);", "OutOfMemoryException", false, false)]
    public async Task ConstructorAllocationPrecedesOwnershipTransfer(string transfer, string catchType, bool cleanupInCatch, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Owner
        {
            public Owner(RespireResult result) { result.Dispose(); }
            public int Value { set { } }
        }
        struct ValueOwner { public ValueOwner(RespireResult result) { result.Dispose(); } }
        class Caller
        {
            async Task Run(RespireClient client)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { {{transfer}} }
                catch ({{catchType}}) { {{(cleanupInCatch ? "result.Dispose();" : "")}} }
            }
        }
        """);

    [Test]
    [Arguments("Take((result, 0), Throw());", true)]
    [Arguments("Take((result, 0), 0);", false)]
    [Arguments("Take((((result)), 0), 0);", false)]
    [Arguments("Take((((result)), 0), Throw());", true)]
    [Arguments("Take((choice ? result : existing, 0), Throw());", true)]
    [Arguments("Take((choice ? result : existing, 0), 0);", true)]
    [Arguments("Take((choice ? result : result, 0), 0);", false)]
    [Arguments("TakeObject((object)result, Throw());", true)]
    [Arguments("TakeObject((object)result, 0);", false)]
    public async Task WrappedTransferWaitsForContainingCall(string transfer, bool warning) => await Disposal.VerifyAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static int Throw() => throw new InvalidOperationException();
            static void Take((RespireResult Result, int Number) value, int ignored) => value.Result.Dispose();
            static void TakeObject(object value, int ignored) => ((RespireResult)value).Dispose();
            async Task Run(RespireClient client, RespireResult existing, bool choice)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                try { {{transfer}} }
                catch (InvalidOperationException) { }
            }
        }
        """);

    [Test]
    [Arguments("Throw()", true)]
    [Arguments("0", false)]
    public async Task FunctionPointerTransferWaitsForArguments(string argument, bool warning) => await Disposal.VerifyUnsafeAsync($$"""
        using System;
        using System.Threading.Tasks;
        using Respire;
        class Caller
        {
            static int Throw() => throw new InvalidOperationException();
            static void Take(RespireResult result, int ignored) => result.Dispose();
            async Task Run(RespireClient client)
            {
                var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                unsafe
                {
                    delegate*<RespireResult, int, void> callback = &Take;
                    try { callback(result, {{argument}}); }
                    catch (InvalidOperationException) { }
                }
            }
        }
        """);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FunctionPointerCallCanBypassCleanup(bool cleanupInCatch)
    {
        await Disposal.VerifyUnsafeAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                static void Throw() => throw new InvalidOperationException();
                async Task Run(RespireClient client)
                {
                    var {{(cleanupInCatch ? "result" : "{|RESP001:result|}")}} = await client.ExecuteAsync("PING");
                    unsafe
                    {
                        delegate*<void> callback = &Throw;
                        try { callback(); result.Dispose(); }
                        catch (InvalidOperationException) { {{(cleanupInCatch ? "result.Dispose();" : "")}} }
                    }
                }
            }
            """);
        await Pending.VerifyUnsafeAsync($$"""
            using System;
            using Respire;
            class Caller
            {
                static void Throw() => throw new InvalidOperationException();
                unsafe void Run(RespireClient client)
                {
                    var batch = client.CreateBatch();
                    var pending = batch.GetStringAsync("key");
                    delegate*<void> callback = &Throw;
                    try { callback(); batch.SendAsync().AsTask().GetAwaiter().GetResult(); }
                    catch (InvalidOperationException)
                    {
                        {{(cleanupInCatch ? "batch.SendAsync().AsTask().GetAwaiter().GetResult();" : "")}}
                    }
                    Console.WriteLine({{(cleanupInCatch ? "pending.Result" : "{|RESP002:pending.Result|}")}});
                }
            }
            """);
    }

    [Test]
    [Arguments("var holder = new Holder();", "", false)]
    [Arguments("var holder = input; if (holder is null) return;", "", false)]
    [Arguments("var holder = input; if (holder is not Holder) return;", "", false)]
    [Arguments("var holder = input; if (holder == null) return;", "", false)]
    [Arguments("var holder = new Holder();", "holder = null;", true)]
    [Arguments("var holder = input; if (holder is null) return;", "holder = null;", true)]
    [Arguments("var holder = new Holder();", "Reset(ref holder);", true)]
    [Arguments("var holder = new Holder();", "Action reset = () => holder = null; reset();", true)]
    [Arguments("var holder = new Holder();", "", false, "holder.Take(result);")]
    [Arguments("var holder = input; if (holder is null) return;", "", false, "holder.Value = result;")]
    [Arguments("var holder = new Holder();", "", false, "holder.Take(result);", "holder = null;")]
    [Arguments("var holder = new Holder();", "holder = new Holder();", false)]
    [Arguments("var holder = new Holder();", "if (input is null) holder = null;", true)]
    public async Task NonNullReceiverAllowsOwnershipTransfer(string setup, string mutation, bool warning, string transfer = "holder.Field = result;", string after = "")
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Holder
            {
                public RespireResult Field;
                public RespireResult Value { set { value.Dispose(); } }
                public void Take(RespireResult result) => result.Dispose();
            }
            class Caller
            {
                static void Reset(ref Holder holder) => holder = null;
                async Task Run(RespireClient client, Holder input)
                {
                    {{setup}}
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    {{mutation}}
                    try { {{transfer}} }
                    catch (NullReferenceException) { }
                    {{after}}
                }
            }
            """);
    }

    [Test]
    [Arguments("_ = values[0];", "InvalidOperationException", false)]
    [Arguments("_ = values[0];", "ArrayTypeMismatchException", false)]
    [Arguments("_ = values[0];", "NullReferenceException", true)]
    [Arguments("_ = values[0];", "IndexOutOfRangeException", true)]
    [Arguments("_ = values[0];", "Exception", true)]
    [Arguments("values[0] = new object();", "ArrayTypeMismatchException", true)]
    [Arguments("_ = values[Index()];", "InvalidOperationException", true)]
    public async Task ArrayAccessUsesSpecificExceptions(string expression, string catchType, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Caller
            {
                static int Index() => throw new InvalidOperationException();
                async Task Run(RespireClient client, object[] values)
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
                static int Index() => throw new InvalidOperationException();
                async Task Run(RespireClient client, object[] values)
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
    [Arguments("holder.Value = result;", "NullReferenceException", true)]
    [Arguments("holder.Field = result;", "NullReferenceException", true)]
    [Arguments("buffer[index] = result;", "IndexOutOfRangeException", true)]
    [Arguments("target.Value = result;", "Exception", true)]
    [Arguments("target[index] = result;", "Exception", true)]
    [Arguments("this.Value = result;", "InvalidOperationException", false)]
    [Arguments("holder.Value = result;", "InvalidOperationException", false)]
    public async Task AssignmentTransferWaitsForTargetChecks(string assignment, string catchType, bool warning)
    {
        await Disposal.VerifyAsync($$"""
            using System;
            using System.Threading.Tasks;
            using Respire;
            class Holder { public RespireResult Value { set { value.Dispose(); } } public RespireResult Field; }
            class Caller
            {
                RespireResult Value { set { value.Dispose(); } }
                async Task Run(RespireClient client, Holder holder, RespireResult[] buffer, int index, dynamic target)
                {
                    var {{(warning ? "{|RESP001:result|}" : "result")}} = await client.ExecuteAsync("PING");
                    try { {{assignment}} }
                    catch ({{catchType}}) { }
                }
            }
            """);
    }

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
    [Arguments("_ = new Owner(result, 0) { Property = Throws() };", false, false, "InvalidOperationException")]
    [Arguments("Owner owner = new(result, 0) { Property = Throws() };", false, false, "InvalidOperationException")]
    [Arguments("_ = new Owner(result, Throws()) { Property = 0 };", false)]
    public async Task OwnershipTransferWaitsForArguments(string transfer, bool cleanupInCatch, bool warning = true, string catchType = "Exception")
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
                    catch ({{catchType}}) { {{(cleanupInCatch ? "result.Dispose();" : "")}} }
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
