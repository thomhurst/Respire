using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

/// <summary>
/// Covers <see cref="RespireGetResult{T}"/> and the compile-time overload resolution of the facets that
/// pair a raw <see cref="RespireValue"/> method with a serializing generic one. The resolution
/// tests bind at compile time — the expression tree only reports which member the compiler chose.
/// </summary>
public class TypedValueApiTests
{
    [Test]
    public async Task BitmapSet_RequiresConcreteImplementations()
    {
        var clientSet = typeof(IRespireBitmapCommands).GetMethod(nameof(IRespireBitmapCommands.SetAsync));
        var batchSet = typeof(IRespireBatchBitmapCommands).GetMethod(nameof(IRespireBatchBitmapCommands.Set));

        await Assert.That(clientSet).IsNotNull();
        await Assert.That(clientSet!.IsAbstract).IsTrue();
        await Assert.That(batchSet).IsNotNull();
        await Assert.That(batchSet!.IsAbstract).IsTrue();
    }

    [Test]
    public async Task SubscriptionOptionsOverloads_HaveConcreteClientImplementations()
    {
        var methods = typeof(IRespireClient)
            .GetMethods()
            .Where(method => method.GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(RespireSubscriptionOptions)))
            .ToArray();

        await Assert.That(methods).IsNotEmpty();
        var mapping = typeof(RespireClient).GetInterfaceMap(typeof(IRespireClient));
        foreach (var method in methods)
        {
            var index = Array.IndexOf(mapping.InterfaceMethods, method);
            await Assert.That(index).IsGreaterThanOrEqualTo(0);
            var implementation = mapping.TargetMethods[index];
            await Assert.That(implementation.DeclaringType).IsEqualTo(typeof(RespireClient));
            await Assert.That(implementation.IsAbstract).IsFalse();
        }
    }

    [Test]
    public async Task CombinedGets_RequireConcreteImplementations()
    {
        (Type Type, string Name)[] methods =
        [
            (typeof(IRespireStringCommands), nameof(IRespireStringCommands.GetAndDeleteAsync)),
            (typeof(IRespireStringCommands), nameof(IRespireStringCommands.GetAndExpireAsync)),
            (typeof(IRespireHashCommands), nameof(IRespireHashCommands.GetAndRemoveAsync)),
            (typeof(IRespireHashCommands), nameof(IRespireHashCommands.GetAndExpireAsync)),
            (typeof(IRespireBatchStringCommands), nameof(IRespireBatchStringCommands.GetAndDelete)),
            (typeof(IRespireBatchStringCommands), nameof(IRespireBatchStringCommands.GetAndExpire)),
            (typeof(IRespireBatchHashCommands), nameof(IRespireBatchHashCommands.GetAndRemove)),
            (typeof(IRespireBatchHashCommands), nameof(IRespireBatchHashCommands.GetAndExpire)),
        ];

        foreach (var (type, name) in methods)
        {
            var untyped = type.GetMethods()
                .Where(method => method.Name == name && !method.IsGenericMethod)
                .ToArray();
            await Assert.That(untyped).IsNotEmpty();
            await Assert.That(untyped.All(method => method.IsAbstract)).IsTrue();
        }
    }

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
        RespireGetResult<int> missing = default;

        await Assert.That(missing.Found).IsFalse();
        await Assert.That(missing.Value).IsEqualTo(0);
        await Assert.That(missing.GetValueOrDefault(-1)).IsEqualTo(-1);
        await Assert.That(missing.TryGetValue(out var value)).IsFalse();
        await Assert.That(value).IsEqualTo(0);
    }

    [Test]
    public async Task FoundRespireGet_IsDistinctFromMissingEvenWhenValueIsDefault()
    {
        var stored = new RespireGetResult<int>(Found: true, Value: 0);
        var (found, value) = stored;

        await Assert.That(found).IsTrue();
        await Assert.That(value).IsEqualTo(0);
        await Assert.That(stored.GetValueOrDefault(-1)).IsEqualTo(0);
        await Assert.That(stored.TryGetValue(out var extracted)).IsTrue();
        await Assert.That(extracted).IsEqualTo(0);
        await Assert.That(stored.Equals(default(RespireGetResult<int>))).IsFalse();
    }

    [Test]
    public async Task TryGetValue_WritesDefaultWhenFoundIsFalse()
    {
        var inconsistent = new RespireGetResult<int>(Found: false, Value: 42);

        await Assert.That(inconsistent.TryGetValue(out var value)).IsFalse();
        await Assert.That(value).IsEqualTo(0);
    }

    [Test]
    public async Task TryGetValue_AnnotatesReferenceAsNullableRegardlessOfFound()
    {
        var parameter = typeof(RespireGetResult<string>).GetMethod(nameof(RespireGetResult<string>.TryGetValue))!
            .GetParameters()[0];
        var nullability = new NullabilityInfoContext().Create(parameter);

        await Assert.That(nullability.WriteState).IsEqualTo(NullabilityState.Nullable);
        await Assert.That(parameter.GetCustomAttribute<MaybeNullWhenAttribute>()).IsNull();
    }

    [Test]
    public async Task NullableResultMembers_AnnotateReferenceValuesAsNullable()
    {
        var type = typeof(RespireGetResult<string>);
        var nullability = new NullabilityInfoContext();
        var value = type.GetProperty(nameof(RespireGetResult<string>.Value))!;
        var deconstruct = type.GetMethod(nameof(RespireGetResult<string>.Deconstruct))!;
        var getValueOrDefault = type.GetMethod(nameof(RespireGetResult<string>.GetValueOrDefault))!;

        await Assert.That(nullability.Create(value).ReadState).IsEqualTo(NullabilityState.Nullable);
        await Assert.That(nullability.Create(deconstruct.GetParameters()[1]).WriteState)
            .IsEqualTo(NullabilityState.Nullable);
        await Assert.That(nullability.Create(getValueOrDefault.ReturnParameter).ReadState)
            .IsEqualTo(NullabilityState.Nullable);
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
        var foundNull = new RespireGetResult<string?>(Found: true, Value: null);
        RespireGetResult<string?> missing = default;

        await Assert.That(foundNull.Found).IsTrue();
        await Assert.That(missing.Found).IsFalse();
        await Assert.That(foundNull.Equals(missing)).IsFalse();
        await Assert.That(foundNull.TryGetValue(out var value)).IsTrue();
        await Assert.That(value).IsNull();
    }

    [Test]
    public async Task HashSetAsync_WithRespireValue_BindsToRawOverload()
    {
        var bound = BoundMethod(
            (IRespireHashCommands hash) => hash.SetAsync(
                "key", "field", (RespireValue)"text", RespireSetWhen.Always, CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsFalse();
    }

    [Test]
    public async Task HashSetAsync_WithString_BindsToSerializingOverload()
    {
        // An exact match beats the implicit string -> RespireValue conversion, so the typed
        // overload wins. Both write the same bytes: string serialization is pass-through.
        var bound = BoundMethod(
            (IRespireHashCommands hash) => hash.SetAsync(
                "key", "field", "text", RespireSetWhen.Always, CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsTrue();
        await Assert.That(bound.GetGenericArguments()[0]).IsEqualTo(typeof(string));
    }

    [Test]
    public async Task HashSetAsync_LegacyCancellationOverloadsRemainBindable()
    {
        var raw = BoundMethod(
            (IRespireHashCommands hash) => hash.SetAsync(
                "key", "field", (RespireValue)"text", CancellationToken.None));
        var typed = BoundMethod(
            (IRespireHashCommands hash) => hash.SetAsync(
                "key", "field", "text", CancellationToken.None));

        await Assert.That(raw.GetParameters()[3].ParameterType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(typed.GetParameters()[3].ParameterType).IsEqualTo(typeof(CancellationToken));
    }

    [Test]
    public async Task BatchHashSet_LegacyThreeArgumentOverloadRemainsBindable()
    {
        var bound = BoundMethod(
            (IRespireBatchHashCommands hash) => hash.Set("key", "field", (RespireValue)"text"));

        await Assert.That(bound.GetParameters().Length).IsEqualTo(3);
    }

    [Test]
    public async Task SetContainsAsync_WithRespireValue_BindsToRawOverload()
    {
        var bound = BoundMethod(
            (IRespireSetCommands set) => set.ContainsAsync("key", (RespireValue)"member", CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsFalse();
    }

    [Test]
    public async Task SetContainsAsync_WithString_BindsToSerializingOverload()
    {
        var bound = BoundMethod(
            (IRespireSetCommands set) => set.ContainsAsync("key", "member", CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsTrue();
    }

    [Test]
    public async Task SortedSetAddAsync_WithRespireValue_BindsToRawOverload()
    {
        var bound = BoundMethod(
            (IRespireSortedSetCommands sorted) => sorted.AddAsync("key", (RespireValue)"member", 1.5, CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsFalse();
    }

    [Test]
    public async Task SortedSetAddAsync_WithString_BindsToSerializingOverload()
    {
        var bound = BoundMethod(
            (IRespireSortedSetCommands sorted) => sorted.AddAsync("key", "member", 1.5, CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsTrue();
    }

    [Test]
    public async Task StringSetAsync_KeepsTheSameSplitTheTypedFacetsCopy()
    {
        var raw = BoundMethod(
            (IRespireStringCommands strings) => strings.SetAsync(
                "key", (RespireValue)"text", RespireExpiry.None, RespireSetWhen.Always, CancellationToken.None));
        var typed = BoundMethod(
            (IRespireStringCommands strings) => strings.SetAsync(
                "key", "text", RespireExpiry.None, RespireSetWhen.Always, CancellationToken.None));

        await Assert.That(raw.IsGenericMethod).IsFalse();
        await Assert.That(typed.IsGenericMethod).IsTrue();
    }

    [Test]
    public async Task ListPopAsync_WithoutTypeArgument_StaysOnTheStringOverload()
    {
        // The typed pops cannot infer T, so an untyped call is never captured by them.
        var bound = BoundMethod(
            (IRespireListCommands list) => list.LeftPopAsync("key", null, CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsFalse();
        await Assert.That(bound.ReturnType).IsEqualTo(typeof(ValueTask<string?>));
    }

    [Test]
    public async Task ListPopAsync_WithTypeArgument_SelectsTheTypedOverload()
    {
        var bound = BoundMethod(
            (IRespireListCommands list) => list.LeftPopAsync<int>("key", null, CancellationToken.None));

        await Assert.That(bound.IsGenericMethod).IsTrue();
        await Assert.That(bound.GetGenericArguments()[0]).IsEqualTo(typeof(int));
    }

    [Test]
    public async Task ListPopAsync_WithDefaultLiteral_SelectsTheImmediateOverload()
    {
        var left = BoundMethod(
            (IRespireListCommands list) => list.LeftPopAsync("key", default));
        var right = BoundMethod(
            (IRespireListCommands list) => list.RightPopAsync("key", default));

        await Assert.That(left.GetParameters()[1].ParameterType).IsEqualTo(typeof(CancellationToken));
        await Assert.That(right.GetParameters()[1].ParameterType).IsEqualTo(typeof(CancellationToken));
    }

    private static MethodInfo BoundMethod<TFacet, TResult>(Expression<Func<TFacet, TResult>> call)
        => ((MethodCallExpression)call.Body).Method;
}
