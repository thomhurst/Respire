using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class DeferredFacetParityTests
{
    private static readonly HashSet<MethodInfo> ImmediateOnlyMethods =
    [
        // Cursor scans issue multiple commands while streaming; they are not one queued result.
        typeof(IKeyCommands).GetMethod(nameof(IKeyCommands.ScanAsync),
            [typeof(string), typeof(RespireKeyType?), typeof(int), typeof(CancellationToken)])!,
        typeof(IHashCommands).GetMethod(nameof(IHashCommands.ScanAsync),
            [typeof(RespireKey), typeof(string), typeof(int), typeof(CancellationToken)])!,
        typeof(ISetCommands).GetMethod(nameof(ISetCommands.ScanAsync),
            [typeof(RespireKey), typeof(string), typeof(int), typeof(CancellationToken)])!,
        typeof(ISortedSetCommands).GetMethod(nameof(ISortedSetCommands.ScanAsync),
            [typeof(RespireKey), typeof(string), typeof(int), typeof(CancellationToken)])!,
        // Multi-key scalar list pops are always blocking and cannot be queued.
        typeof(IListCommands).GetMethod(nameof(IListCommands.PopAsync),
            [typeof(ReadOnlySpan<RespireKey>), typeof(TimeSpan), typeof(ListSide), typeof(CancellationToken)])!,
        // Both typed and string multi-key scalar sorted-set pops always block on a dedicated lease.
        .. typeof(ISortedSetCommands).GetMethods().Where(method => method.Name == nameof(ISortedSetCommands.PopAsync)
            && method.GetParameters().Any(parameter => parameter.ParameterType == typeof(TimeSpan))),
        // Leased replies require explicit pooled-buffer ownership outside deferred completion.
        typeof(IStringCommands).GetMethod(nameof(IStringCommands.GetLeaseAsync),
            [typeof(RespireKey), typeof(CancellationToken)])!,
    ];

    [Test]
    [Arguments("Strings")]
    [Arguments("Keys")]
    [Arguments("Hashes")]
    [Arguments("Lists")]
    [Arguments("Sets")]
    [Arguments("SortedSets")]
    [Arguments("Bitmaps")]
    [Arguments("HyperLogLog")]
    [Arguments("Geo")]
    public async Task EveryQueueableClientOverloadHasMatchingDeferredShape(string facet)
    {
        var immediate = typeof(IRespireClient).GetProperty(facet)!.PropertyType;
        var deferred = typeof(IRespireCommandQueue).GetProperty(facet)!.PropertyType;
        var expected = immediate.GetMethods()
            .Where(method => !ImmediateOnlyMethods.Contains(method))
            .Select(method => Signature(method, immediate: true));
        var actual = deferred.GetMethods().Select(method => Signature(method, immediate: false));

        await Assert.That(expected.Except(actual).ToArray()).IsEmpty();
    }

    private static string Signature(MethodInfo method, bool immediate)
    {
        // Compare generic parameters by position; this shape audit does not compare their constraints.
        var name = immediate ? method.Name[..^"Async".Length] : method.Name;
        var parameters = method.GetParameters()
            .Where(parameter => !immediate || (parameter.ParameterType != typeof(CancellationToken)
                && !(parameter.Name == "waitFor" && parameter.ParameterType == typeof(TimeSpan?))))
            .Select(parameter => TypeName(parameter.ParameterType));
        var result = method.ReturnType.GetGenericArguments().Single();
        return $"{name}`{method.GetGenericArguments().Length}({string.Join(", ", parameters)}): {TypeName(result)}";
    }

    private static string TypeName(Type type)
    {
        if (type.IsGenericParameter)
        {
            return $"T{type.GenericParameterPosition}";
        }
        if (type.IsArray)
        {
            return $"{TypeName(type.GetElementType()!)}[]";
        }
        return type.IsGenericType
            ? $"{type.GetGenericTypeDefinition().Name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>"
            : type.Name;
    }
}
