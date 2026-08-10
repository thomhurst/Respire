using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

/// <summary>
/// Every <c>params ReadOnlySpan&lt;T&gt;</c> command has a sibling overload that takes the same
/// items non-params plus a required <see cref="CancellationToken"/>, because a params parameter
/// must come last and so cannot be followed by a token. These tests pin both halves: that the
/// sibling exists across the whole public surface, and that the resolution matrix binds the way
/// callers expect on every target framework.
/// </summary>
public class MultiItemCancellationOverloadTests
{
    [Test]
    public async Task EveryParamsSpanCommand_HasACancellationTokenSibling()
    {
        var missing = new List<string>();
        var covered = 0;

        foreach (var type in typeof(RespireClient).Assembly.GetExportedTypes())
        {
            // Deferred commands intentionally omit per-command cancellation because ExecuteAsync
            // or CommitAsync owns cancellation for the whole queued operation set.
            if (type == typeof(RespireBatch)
                || type == typeof(RespireTransaction)
                || type.Name.StartsWith("IBatch", StringComparison.Ordinal))
            {
                continue;
            }

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 0 || !IsParamsSpan(parameters[^1]))
                {
                    continue;
                }

                covered++;
                var expected = parameters.Select(static parameter => parameter.ParameterType)
                    .Append(typeof(CancellationToken))
                    .ToArray();

                if (!methods.Any(candidate => Matches(candidate, method.Name, expected)))
                {
                    missing.Add($"{type.Name}.{method.Name}({string.Join(", ", expected.Select(Describe))})");
                }
            }
        }

        await Assert.That(missing).IsEmpty();

        // Guards the guard: RespireExpiry intentionally collapses the relative, absolute, persist,
        // and keep permutations, leaving 42 params-span commands in the combined surface.
        await Assert.That(covered).IsGreaterThanOrEqualTo(42);
    }

    [Test]
    public async Task ParamsForm_BindsToTheParamsOverload_AndPassesNoToken()
    {
        var keys = new RecordingKeyCommands();
        RespireKey[] array = ["a", "b"];

        // Array argument, no token: the params overload in its normal (non-expanded) form.
        await keys.DeleteAsync(array);
        await Assert.That(keys.LastOverload).IsEqualTo(Overload.Params);
        await Assert.That(keys.LastKeyCount).IsEqualTo(2);
        await Assert.That(keys.LastToken.CanBeCanceled).IsFalse();

        // Expanded params form.
        await keys.DeleteAsync("a", "b", "c");
        await Assert.That(keys.LastOverload).IsEqualTo(Overload.Params);
        await Assert.That(keys.LastKeyCount).IsEqualTo(3);
        await Assert.That(keys.LastToken.CanBeCanceled).IsFalse();
    }

    [Test]
    public async Task TokenForm_BindsToTheCancellationOverload_ForArraysAndSpans()
    {
        var keys = new RecordingKeyCommands();
        using var cts = new CancellationTokenSource();
        RespireKey[] array = ["a", "b"];

        await keys.DeleteAsync(array, cts.Token);
        await Assert.That(keys.LastOverload).IsEqualTo(Overload.WithToken);
        await Assert.That(keys.LastKeyCount).IsEqualTo(2);
        await Assert.That(keys.LastToken).IsEqualTo(cts.Token);

        ReadOnlySpan<RespireKey> span = array.AsSpan(0, 1);
        await keys.DeleteAsync(span, cts.Token);
        await Assert.That(keys.LastOverload).IsEqualTo(Overload.WithToken);
        await Assert.That(keys.LastKeyCount).IsEqualTo(1);
        await Assert.That(keys.LastToken).IsEqualTo(cts.Token);
    }

    [Test]
    public async Task ResolutionMatrix_CompilesAcrossEveryFacet()
    {
        // The assertion is the compile itself: <see cref="CompileCoverage"/> exercises the
        // (items, token) form of every multi-item command on both target frameworks.
        var coverage = CompileCoverage;
        await Assert.That(coverage).IsNotNull();
    }

    /// <summary>
    /// Never executed. Each call must bind to the cancellation overload rather than fold the
    /// token into the params collection, on both net8.0 and net10.0.
    /// </summary>
    private static void CompileCoverage(IRespireClient client, CancellationToken token)
    {
        RespireKey[] keys = ["a", "b"];
        RespireValue[] values = ["x", "y"];
        string[] fields = ["f1", "f2"];
        (RespireKey Key, RespireValue Value)[] pairs = [("a", "x")];
        (string Field, RespireValue Value)[] fieldValues = [("f1", "x")];
        SortedSetEntry[] entries = [new SortedSetEntry("m", 1)];
        GeoEntry[] geoEntries = [new GeoEntry(0, 0, "m")];
        BitFieldOperation[] operations = [BitFieldOperation.Get("u8", "0")];
        RespireStreamId[] ids = [new RespireStreamId("1-1")];
        var expiry = TimeSpan.FromMinutes(1);
        var expireAt = DateTimeOffset.UtcNow;

        _ = client.DeleteAsync(keys, token);

        _ = client.Keys.DeleteAsync(keys, token);
        _ = client.Keys.UnlinkAsync(keys, token);
        _ = client.Keys.TouchAsync(keys, token);

        _ = client.Strings.GetManyAsync(keys, token);
        _ = client.Strings.SetManyAsync(pairs, token);
        _ = client.Strings.SetManyAsync(expiry, SetWhen.NotExists, pairs, token);
        _ = client.Strings.SetManyAsync(expireAt, SetWhen.Exists, pairs, token);
        _ = client.Strings.SetManyAsync(RespireExpiry.Keep, SetWhen.Exists, pairs, token);

        _ = client.Hashes.SetAsync("h", fieldValues, token);
        _ = client.Hashes.GetManyAsync("h", fields, token);
        _ = client.Hashes.DeleteAsync("h", fields, token);
        _ = client.Hashes.ExpiryAsync("h", fields, token);
        _ = client.Hashes.ExpireAsync("h", expiry, fields, token);
        _ = client.Hashes.ExpireAsync("h", expiry, HashFieldExpireWhen.Exists, fields, token);
        _ = client.Hashes.ExpireAsync("h", RespireExpiry.At(expireAt), fields, token);
        _ = client.Hashes.ExpireAsync("h", RespireExpiry.At(expireAt), HashFieldExpireWhen.Exists, fields, token);
        _ = client.Hashes.ExpireAsync("h", RespireExpiry.Persist, fields, token);
        _ = client.Hashes.GetDeleteAsync("h", fields, token);
        _ = client.Hashes.GetExpireAsync("h", expiry, fields, token);
        _ = client.Hashes.GetExpireAsync("h", RespireExpiry.At(expireAt), fields, token);
        _ = client.Hashes.GetExpireAsync("h", RespireExpiry.Persist, fields, token);
        _ = client.Hashes.SetExpireAsync("h", expiry, fieldValues, token);
        _ = client.Hashes.SetExpireAsync("h", expiry, SetWhen.Exists, fieldValues, token);
        _ = client.Hashes.SetExpireAsync("h", RespireExpiry.At(expireAt), fieldValues, token);
        _ = client.Hashes.SetExpireAsync("h", RespireExpiry.At(expireAt), SetWhen.Exists, fieldValues, token);

        _ = client.Lists.LeftPushAsync("l", values, token);
        _ = client.Lists.RightPushAsync("l", values, token);

        _ = client.Sets.AddAsync("s", values, token);
        _ = client.Sets.RemoveAsync("s", values, token);
        _ = client.Sets.IntersectAsync(keys, token);
        _ = client.Sets.UnionAsync(keys, token);
        _ = client.Sets.DifferenceAsync(keys, token);
        _ = client.Sets.IntersectStoreAsync("d", keys, token);
        _ = client.Sets.UnionStoreAsync("d", keys, token);
        _ = client.Sets.DifferenceStoreAsync("d", keys, token);

        _ = client.SortedSets.AddAsync("z", entries, token);
        _ = client.SortedSets.RemoveAsync("z", values, token);

        _ = client.HyperLogLog.AddAsync("hll", values, token);
        _ = client.HyperLogLog.CountAsync(keys, token);
        _ = client.HyperLogLog.MergeAsync("hll", keys, token);

        _ = client.Bitmaps.OperateAsync(BitOperation.Or, "d", keys, token);
        _ = client.Bitmaps.FieldAsync("b", operations, token);
        _ = client.Bitmaps.FieldReadOnlyAsync("b", operations, token);

        _ = client.Geo.AddAsync("g", GeoAddCondition.Always, changed: false, geoEntries, token);
        _ = client.Geo.HashAsync("g", values, token);
        _ = client.Geo.PositionAsync("g", values, token);

        _ = client.Streams.AddAsync("st", fieldValues, token);
        _ = client.Streams.DeleteAsync("st", ids, token);
        _ = client.Streams.AcknowledgeAsync("st", "group", ids, token);
    }

    private static bool Matches(MethodInfo candidate, string name, Type[] expected)
    {
        if (candidate.Name != name)
        {
            return false;
        }

        var parameters = candidate.GetParameters();
        if (parameters.Length != expected.Length || IsParamsSpan(parameters[^1]))
        {
            return false;
        }

        return !parameters.Where((parameter, index) => parameter.ParameterType != expected[index]).Any();
    }

    /// <summary>
    /// A <c>params ReadOnlySpan&lt;T&gt;</c> parameter — the only shape that cannot be followed by
    /// a token. <c>params T[]</c> methods are out of scope: callers can already pass an array.
    /// </summary>
    private static bool IsParamsSpan(ParameterInfo parameter)
        => parameter.ParameterType.IsGenericType
            && parameter.ParameterType.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>)
            && parameter.GetCustomAttributes(inherit: false)
                .Any(static attribute => attribute.GetType().Name
                    is "ParamArrayAttribute" or "ParamCollectionAttribute");

    private static string Describe(Type type)
        => type.IsGenericType
            ? $"{type.Name[..type.Name.IndexOf('`')]}<{string.Join(", ", type.GetGenericArguments().Select(Describe))}>"
            : type.Name;

    private enum Overload
    {
        None,
        Params,
        WithToken,
    }

    /// <summary>Records which <c>DeleteAsync</c> overload the compiler picked.</summary>
    private sealed class RecordingKeyCommands : IKeyCommands
    {
        public Overload LastOverload { get; private set; }

        public int LastKeyCount { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public ValueTask<long> DeleteAsync(params ReadOnlySpan<RespireKey> keys)
            => Record(Overload.Params, keys.Length, CancellationToken.None);

        public ValueTask<long> DeleteAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
            => Record(Overload.WithToken, keys.Length, cancellationToken);

        private ValueTask<long> Record(Overload overload, int keyCount, CancellationToken token)
        {
            LastOverload = overload;
            LastKeyCount = keyCount;
            LastToken = token;
            return new ValueTask<long>(keyCount);
        }

        public ValueTask<long> UnlinkAsync(params ReadOnlySpan<RespireKey> keys) => throw new NotSupportedException();

        public ValueTask<long> UnlinkAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<long> TouchAsync(params ReadOnlySpan<RespireKey> keys) => throw new NotSupportedException();

        public ValueTask<long> TouchAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<bool> ExistsAsync(RespireKey key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<bool> ExpireAsync(
            RespireKey key,
            RespireExpiry expiry,
            ExpireWhen when = ExpireWhen.Always,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<RespireTtl> ExpiryAsync(RespireKey key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<string> TypeAsync(RespireKey key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask RenameAsync(RespireKey key, RespireKey newKey, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<string> ScanAsync(
            string? match = null, int pageSize = 250, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
