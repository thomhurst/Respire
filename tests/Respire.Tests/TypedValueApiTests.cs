using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

/// <summary>
/// Covers <see cref="RespireGet{T}"/> and the compile-time overload resolution of the facets that
/// pair a raw <see cref="RespireValue"/> method with a serializing generic one. The resolution
/// tests bind at compile time — the expression tree only reports which member the compiler chose.
/// </summary>
public class TypedValueApiTests
{
    [Test]
    public async Task TryGetAsync_IsAbstractOnIRespireClient()
    {
        var method = typeof(IRespireClient)
            .GetMethods()
            .Single(candidate => candidate.Name == nameof(IRespireClient.TryGetAsync));

        await Assert.That(method.IsAbstract).IsTrue();
    }

    [Test]
    public async Task DefaultRespireGet_IsNotFoundAndCarriesDefaultValue()
    {
        RespireGet<int> missing = default;

        await Assert.That(missing.Found).IsFalse();
        await Assert.That(missing.Value).IsEqualTo(0);
        await Assert.That(missing.GetValueOrDefault(-1)).IsEqualTo(-1);
        await Assert.That(missing.TryGetValue(out var value)).IsFalse();
        await Assert.That(value).IsEqualTo(0);
    }

    [Test]
    public async Task FoundRespireGet_IsDistinctFromMissingEvenWhenValueIsDefault()
    {
        var stored = new RespireGet<int>(Found: true, Value: 0);
        var (found, value) = stored;

        await Assert.That(found).IsTrue();
        await Assert.That(value).IsEqualTo(0);
        await Assert.That(stored.GetValueOrDefault(-1)).IsEqualTo(0);
        await Assert.That(stored.TryGetValue(out var extracted)).IsTrue();
        await Assert.That(extracted).IsEqualTo(0);
        await Assert.That(stored.Equals(default(RespireGet<int>))).IsFalse();
    }

    [Test]
    public async Task TryGetValue_WritesDefaultWhenFoundIsFalse()
    {
        var inconsistent = new RespireGet<int>(Found: false, Value: 42);

        await Assert.That(inconsistent.TryGetValue(out var value)).IsFalse();
        await Assert.That(value).IsEqualTo(0);
    }

    [Test]
    public async Task TryGetValue_AnnotatesMissingReferenceAsMaybeNull()
    {
        var parameter = typeof(RespireGet<>).GetMethod(nameof(RespireGet<int>.TryGetValue))!
            .GetParameters()[0];
        var attribute = parameter.GetCustomAttribute<MaybeNullWhenAttribute>();

        await Assert.That(attribute).IsNotNull();
        await Assert.That(attribute!.ReturnValue).IsFalse();
    }

    [Test]
    public async Task PendingTryGetResult_AnnotatesMissingReferenceAsMaybeNull()
    {
        var parameter = typeof(RespirePending<>).GetMethod(nameof(RespirePending<int>.TryGetResult))!
            .GetParameters()[0];
        var attribute = parameter.GetCustomAttribute<MaybeNullWhenAttribute>();

        await Assert.That(attribute).IsNotNull();
        await Assert.That(attribute!.ReturnValue).IsFalse();
    }

    [Test]
    public async Task RespireGet_ForReferenceTypes_KeepsFoundNullDistinctFromMissing()
    {
        var foundNull = new RespireGet<string?>(Found: true, Value: null);
        RespireGet<string?> missing = default;

        await Assert.That(foundNull.Found).IsTrue();
        await Assert.That(missing.Found).IsFalse();
        await Assert.That(foundNull.Equals(missing)).IsFalse();
    }

    [Test]
    public async Task HashSetAsync_WithRespireValue_BindsToRawOverload()
    {
        var bound = BoundMethod(
            (IHashCommands hash) => hash.SetAsync("key", "field", (RespireValue)"text", CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsFalse();
    }

    [Test]
    public async Task HashSetAsync_WithString_BindsToSerializingOverload()
    {
        // An exact match beats the implicit string -> RespireValue conversion, so the typed
        // overload wins. Both write the same bytes: string serialization is pass-through.
        var bound = BoundMethod(
            (IHashCommands hash) => hash.SetAsync("key", "field", "text", CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsTrue();
        await Assert.That(bound.GetGenericArguments()[0]).IsEqualTo(typeof(string));
    }

    [Test]
    public async Task SetContainsAsync_WithRespireValue_BindsToRawOverload()
    {
        var bound = BoundMethod(
            (ISetCommands set) => set.ContainsAsync("key", (RespireValue)"member", CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsFalse();
    }

    [Test]
    public async Task SetContainsAsync_WithString_BindsToSerializingOverload()
    {
        var bound = BoundMethod(
            (ISetCommands set) => set.ContainsAsync("key", "member", CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsTrue();
    }

    [Test]
    public async Task SortedSetAddAsync_WithRespireValue_BindsToRawOverload()
    {
        var bound = BoundMethod(
            (ISortedSetCommands sorted) => sorted.AddAsync("key", (RespireValue)"member", 1.5, CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsFalse();
    }

    [Test]
    public async Task SortedSetAddAsync_WithString_BindsToSerializingOverload()
    {
        var bound = BoundMethod(
            (ISortedSetCommands sorted) => sorted.AddAsync("key", "member", 1.5, CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsTrue();
    }

    [Test]
    public async Task StringSetAsync_KeepsTheSameSplitTheTypedFacetsCopy()
    {
        var raw = BoundMethod(
            (IStringCommands strings) => strings.SetAsync(
                "key", (RespireValue)"text", RespireExpiry.None, SetWhen.Always, CancellationToken.None));
        var typed = BoundMethod(
            (IStringCommands strings) => strings.SetAsync(
                "key", "text", RespireExpiry.None, SetWhen.Always, CancellationToken.None));

        await Assert.That(raw.IsGenericMethod).IsFalse();
        await Assert.That(typed.IsGenericMethod).IsTrue();
    }

    [Test]
    public async Task ListPopAsync_WithoutTypeArgument_StaysOnTheStringOverload()
    {
        // The typed pops cannot infer T, so an untyped call is never captured by them.
        var bound = BoundMethod(
            (IListCommands list) => list.LeftPopAsync("key", null, CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsFalse();
        await Assert.That(bound.ReturnType).IsEqualTo(typeof(ValueTask<string?>));
    }

    [Test]
    public async Task ListPopAsync_WithTypeArgument_SelectsTheTypedOverload()
    {
        var bound = BoundMethod(
            (IListCommands list) => list.LeftPopAsync<int>("key", null, CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsTrue();
        await Assert.That(bound.GetGenericArguments()[0]).IsEqualTo(typeof(int));
    }

    private static MethodInfo BoundMethod<TFacet, TResult>(Expression<Func<TFacet, TResult>> call)
        => ((MethodCallExpression)call.Body).Method;
}
