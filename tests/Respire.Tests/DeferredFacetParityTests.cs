using System.Buffers;
using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class DeferredFacetParityTests
{
    private static readonly HashSet<MethodInfo> ImmediateOnlyMethods =
    [
        // Path Hash traversal streams multiple opaque-cursor pages; queue only individual pages.
        .. typeof(IRespirePathHashCommands).GetMethods().Where(method => method.Name == nameof(IRespirePathHashCommands.ScanAsync)),
        // Array scans page by index while streaming; only individual pages can be queued.
        .. typeof(IRespireArrayCommands).GetMethods().Where(method => method.Name == nameof(IRespireArrayCommands.ScanAsync)),
        // Opening an import session acquires a connection and owns its lifetime; it is not a queued command.
        typeof(IRespireHashCommands).GetMethod(nameof(IRespireHashCommands.CreateImportSessionAsync),
            [typeof(CancellationToken)])!,
        typeof(IRespireHashCommands).GetMethod(nameof(IRespireHashCommands.CreateImportSessionAsync),
            [typeof(RespireKey), typeof(CancellationToken)])!,
        // Cursor scans issue multiple commands while streaming; they are not one queued result.
        typeof(IRespireKeyCommands).GetMethod(nameof(IRespireKeyCommands.ScanAsync),
            [typeof(string), typeof(RespireKeyType?), typeof(int), typeof(CancellationToken)])!,
        // A resumable page also discovers/validates multiple primaries before committing progress.
        typeof(IRespireKeyCommands).GetMethod(nameof(IRespireKeyCommands.ScanClusterPageAsync),
            [typeof(RespireClusterScanCursor), typeof(string), typeof(RespireKeyType?), typeof(int), typeof(CancellationToken)])!,
        typeof(IRespireHashCommands).GetMethod(nameof(IRespireHashCommands.ScanAsync),
            [typeof(RespireKey), typeof(string), typeof(int), typeof(CancellationToken)])!,
        typeof(IRespireHashCommands).GetMethod(nameof(IRespireHashCommands.ScanFieldsAsync),
            [typeof(RespireKey), typeof(string), typeof(int), typeof(CancellationToken)])!,
        typeof(IRespireSetCommands).GetMethod(nameof(IRespireSetCommands.ScanAsync),
            [typeof(RespireKey), typeof(string), typeof(int), typeof(CancellationToken)])!,
        typeof(IRespireSortedSetCommands).GetMethod(nameof(IRespireSortedSetCommands.ScanAsync),
            [typeof(RespireKey), typeof(string), typeof(int), typeof(CancellationToken)])!,
        // Single- and multi-key scalar list pops always block and cannot be queued.
        .. typeof(IRespireListCommands).GetMethods().Where(method => method.Name == nameof(IRespireListCommands.PopAsync)
            && method.GetParameters().Any(parameter => parameter.Name == "waitFor"
                && parameter.ParameterType == typeof(TimeSpan?) && !parameter.IsOptional)),
        // Both typed and string multi-key scalar sorted-set pops always block on a dedicated lease.
        .. typeof(IRespireSortedSetCommands).GetMethods().Where(method => method.Name == nameof(IRespireSortedSetCommands.PopAsync)
            && method.GetParameters().Any(parameter => parameter.Name == "waitFor"
                && parameter.ParameterType == typeof(TimeSpan?) && !parameter.IsOptional)),
        // A streamed reply owns a live network response and cannot be queued for batch completion.
        typeof(IRespireStringCommands).GetMethod(nameof(IRespireStringCommands.GetStreamAsync),
            [typeof(RespireKey), typeof(CancellationToken)])!,
        // Leased replies require explicit pooled-buffer ownership outside deferred completion.
        typeof(IRespireStringCommands).GetMethod(nameof(IRespireStringCommands.GetLeaseAsync),
            [typeof(RespireKey), typeof(CancellationToken)])!,
        .. typeof(IRespireFunctionCommands).GetMethods().Where(method => method.Name is
            nameof(IRespireFunctionCommands.ExecuteLeaseAsync) or nameof(IRespireFunctionCommands.ExecuteLeaseSpanAsync)),
        // Stream payloads are immediate-only: deferred batches snapshot values and cannot hold
        // a multiplexed connection while asynchronously reading an external source.
        typeof(IRespireStringCommands).GetMethod(nameof(IRespireStringCommands.SetAsync),
            [typeof(RespireKey), typeof(Stream), typeof(long), typeof(RespireExpiry), typeof(RespireSetWhen), typeof(CancellationToken)])!,
        typeof(IRespireStringCommands).GetMethod(nameof(IRespireStringCommands.SetAsync),
            [typeof(RespireKey), typeof(ReadOnlySequence<byte>), typeof(RespireExpiry), typeof(RespireSetWhen), typeof(CancellationToken)])!,
    ];

    [Test]
    [Arguments("Strings")]
    [Arguments("Keys")]
    [Arguments("Hashes")]
    [Arguments("Lists")]
    [Arguments("Arrays")]
    [Arguments("PathHashes")]
    [Arguments("Sets")]
    [Arguments("SortedSets")]
    [Arguments("Bitmaps")]
    [Arguments("HyperLogLog")]
    [Arguments("Geo")]
    [Arguments("Functions")]
    public async Task EveryQueueableClientOverloadHasMatchingDeferredShape(string facet)
    {
        var immediate = typeof(IRespireClient).GetProperty(facet)!.PropertyType;
        var deferred = typeof(IRespireCommandQueue).GetProperty(facet)!.PropertyType;
        var actual = deferred.GetMethods().Select(method => Signature(method, immediate: false)).ToHashSet();
        var expected = immediate.GetMethods()
            .Where(method => !ImmediateOnlyMethods.Contains(method))
            .Select(method => DeferredSignature(method, actual));

        await Assert.That(expected.Except(actual).ToArray()).IsEmpty();
    }

    [Test]
    public async Task SupportedStreamOverloadsHaveMatchingDeferredShape()
    {
        // Group administration, pending-entry management, metadata, and consumer loops are
        // outside the current deferred facet. New method names must be classified explicitly.
        string[] clientOnly =
        [
            nameof(IRespireStreamCommands.CreateGroupAsync), nameof(IRespireStreamCommands.DeleteGroupAsync),
            nameof(IRespireStreamCommands.DeleteConsumerAsync), nameof(IRespireStreamCommands.SetGroupPositionAsync),
            nameof(IRespireStreamCommands.CreateConsumerAsync), nameof(IRespireStreamCommands.SetLastIdAsync),
            nameof(IRespireStreamCommands.PendingSummaryAsync), nameof(IRespireStreamCommands.PendingAsync),
            nameof(IRespireStreamCommands.ClaimAsync), nameof(IRespireStreamCommands.ClaimPendingAsync),
            nameof(IRespireStreamCommands.ClaimIdsAsync), nameof(IRespireStreamCommands.ClaimPendingIdsAsync),
            nameof(IRespireStreamCommands.InfoAsync), nameof(IRespireStreamCommands.GroupInfoAsync),
            nameof(IRespireStreamCommands.ConsumerInfoAsync),
            nameof(IRespireStreamCommands.ReadAllAsync),
        ];
        var immediate = typeof(IRespireStreamCommands).GetMethods();
        await Assert.That(clientOnly.Except(immediate.Select(method => method.Name)).ToArray()).IsEmpty();
        var actual = typeof(IRespireBatchStreamCommands).GetMethods()
            .Select(method => Signature(method, immediate: false)).ToHashSet();
        var expected = immediate.Where(method => !clientOnly.Contains(method.Name)
                && !(method.Name == nameof(IRespireStreamCommands.ReadGroupAsync)
                    && method.ReturnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>)))
            .Select(method => DeferredSignature(method, actual)).ToArray();
        await Assert.That(expected.Except(actual).ToArray()).IsEmpty();
        await Assert.That(actual.Except(expected).ToArray()).IsEmpty();
    }

    private static string DeferredSignature(MethodInfo method, HashSet<string> actual)
    {
        var signature = Signature(method, immediate: true);
        if (actual.Contains(signature)) return signature;

        // Cancellation belongs to Execute/Commit for deferred commands. The immediate
        // single-item conveniences can therefore use an existing collection command,
        // passing [item] (or simply item for params). Require the same name, all other
        // parameters, generic arity and result shape; a missing command still fails.
        var parameters = CommandParameters(method, immediate: true).ToArray();
        for (var index = 0; index < parameters.Length; index++)
        {
            var item = parameters[index];
            foreach (var collection in new[] { $"ReadOnlySpan`1<{item}>", $"{item}[]" })
            {
                parameters[index] = collection;
                var candidate = Signature(method, immediate: true, parameters);
                if (actual.Contains(candidate)) return candidate;
            }
            parameters[index] = item;
        }

        // The string-field convenience preserves the existing scalar RespireKey result.
        // Deferred callers select that result explicitly with (RespireKey)field; a
        // bare string selects the existing params-string overload returning an array.
        if (method.DeclaringType == typeof(IRespireHashCommands)
            && method.Name == nameof(IRespireHashCommands.ExpiryAsync)
            && parameters.SequenceEqual([nameof(RespireKey), nameof(String)]))
        {
            parameters[1] = nameof(RespireKey);
            return Signature(method, immediate: true, parameters);
        }

        return signature;
    }

    private static IEnumerable<string> CommandParameters(MethodInfo method, bool immediate)
        => method.GetParameters()
            .Where(parameter => !immediate || (parameter.ParameterType != typeof(CancellationToken)
                && !(parameter.Name == "waitFor" && parameter.ParameterType == typeof(TimeSpan?))))
            .Select(parameter => TypeName(parameter.ParameterType));

    private static string Signature(MethodInfo method, bool immediate, IEnumerable<string>? parameters = null)
    {
        // Compare generic parameters by position; this shape audit does not compare their constraints.
        var name = immediate ? method.Name[..^"Async".Length] : method.Name;
        // Single-stream group pages distinguish themselves from existing consumer loops by name.
        if (immediate && method.Name == nameof(IRespireStreamCommands.ReadGroupOnceAsync)) name = nameof(IRespireBatchStreamCommands.ReadGroup);
        parameters ??= CommandParameters(method, immediate);
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
