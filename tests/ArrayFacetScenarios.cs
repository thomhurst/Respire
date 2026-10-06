using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.TestSupport;

internal static class ArrayFacetScenarios
{
    internal sealed record Payload(int Number, string Name);

    internal static async Task EmptyRegularExpressionIsRejectedBeforeKeyLookup(IRespireClient client, bool present)
    {
        if (present) await client.Arrays.SetAsync("regex", 0, "", "value");
        RespireArrayPredicate[] predicates = [new(RespireArrayPredicateKind.Regex, "")];
        await Assert.That(async () => await client.Arrays.GrepAsync("regex", RespireArrayBound.First, RespireArrayBound.Last, predicates))
            .Throws<RespireServerException>().WithMessage("ERR regular expression is empty");
        await Assert.That(async () => await client.Arrays.GrepEntriesAsync("regex", RespireArrayBound.Last, RespireArrayBound.First, predicates))
            .Throws<RespireServerException>().WithMessage("ERR regular expression is empty");
        await Assert.That(async () => await client.Arrays.GrepEntriesAsync<string>("regex", RespireArrayBound.First, RespireArrayBound.Last, predicates))
            .Throws<RespireServerException>().WithMessage("ERR regular expression is empty");
        await Assert.That(await client.Arrays.GrepAsync("regex", RespireArrayBound.First, RespireArrayBound.Last,
            new RespireArrayPredicate(RespireArrayPredicateKind.Exact, "")))
            .IsEquivalentTo(present ? new[] { 0UL } : [], CollectionOrdering.Matching);
        await Assert.That(await client.Arrays.GrepAsync("regex", RespireArrayBound.First, RespireArrayBound.Last,
            new RespireArrayPredicate(RespireArrayPredicateKind.Contains, "")))
            .IsEquivalentTo(present ? new[] { 0UL, 1UL } : [], CollectionOrdering.Matching);
        await Assert.That(await client.Arrays.GrepAsync("regex", RespireArrayBound.First, RespireArrayBound.Last,
            new RespireArrayPredicate(RespireArrayPredicateKind.Glob, "")))
            .IsEquivalentTo(present ? new[] { 0UL } : [], CollectionOrdering.Matching);
        await Assert.That(await client.Arrays.CountAsync("regex")).IsEqualTo(present ? 2UL : 0UL);
    }

    internal static async Task SerializationPredicatesAndValidation(IRespireClient client)
    {
        var arrays = client.Arrays;
        RespireValue[] values = ["first", "second"];
        await Assert.That(await arrays.SetAsync("value-array", 0, values)).IsEqualTo(2UL);
        await Assert.That(await arrays.SetAsync("value-array-token", 0, values, CancellationToken.None)).IsEqualTo(2UL);
        await Assert.That(await arrays.RangeAsync("value-array", 0, 1)).IsEquivalentTo(new string?[] { "first", "second" }, CollectionOrdering.Matching);
        var payload = new Payload(42, "sample");
        await Assert.That(await arrays.SetAsync("json", 0, payload)).IsEqualTo(1UL);
        await Assert.That(await arrays.GetAsync<Payload>("json", 0)).IsEqualTo(payload);
        await Assert.That(await arrays.ScanPageAsync<Payload>("json", 0, 0)).IsEquivalentTo(
            new[] { new RespireArrayEntry<Payload>(0, payload) }, CollectionOrdering.Matching);
        await Assert.That(await arrays.GrepEntriesAsync<Payload>("json", RespireArrayBound.First, RespireArrayBound.Last,
            new RespireArrayPredicate(RespireArrayPredicateKind.Contains, "sample"))).IsEquivalentTo(
            new[] { new RespireArrayEntry<Payload>(0, payload) }, CollectionOrdering.Matching);
        await arrays.SetAsync("json-null", 0, (RespireValue)"null");
        var nullPayload = await arrays.TryGetAsync<Payload>("json-null", 0);
        await Assert.That(nullPayload.Found).IsTrue();
        await Assert.That(nullPayload.Value).IsNull();
        await Assert.That((await arrays.ScanPageAsync<Payload>("json-null", 0, 0))[0].Value).IsNull();
        byte[] binary = [0, 255, 128, 13, 10];
        await arrays.SetAsync("binary", 0, binary);
        await Assert.That(await arrays.GetAsync<byte[]>("binary", 0)).IsEquivalentTo(binary, CollectionOrdering.Matching);
        await arrays.SetAsync("search", 0, "Alpha", "alphabet", "beta", "1.5", "2", "-3");
        await Assert.That(await arrays.GrepAsync("search", RespireArrayBound.First, RespireArrayBound.Last,
            new RespireArrayPredicate(RespireArrayPredicateKind.Exact, "Alpha"),
            new RespireArrayPredicate(RespireArrayPredicateKind.Exact, "beta")))
            .IsEquivalentTo(new[] { 0UL, 2UL }, CollectionOrdering.Matching);
        var options = new RespireArrayGrepOptions { MatchAll = true, IgnoreCase = true };
        await Assert.That(await arrays.GrepAsync("search", RespireArrayBound.First, RespireArrayBound.Last,
            [new(RespireArrayPredicateKind.Contains, "ALPHA"), new(RespireArrayPredicateKind.Glob, "*BET")], options))
            .IsEquivalentTo(new[] { 1UL }, CollectionOrdering.Matching);
        await Assert.That(await arrays.GrepAsync("search", RespireArrayBound.First, RespireArrayBound.Last,
            [new(RespireArrayPredicateKind.Regex, "^Alpha")], new RespireArrayGrepOptions { IgnoreCase = true }))
            .IsEquivalentTo(new[] { 0UL, 1UL }, CollectionOrdering.Matching);
        await arrays.SetAsync("glob", 0, "abc", "a*?", "B4", "", "abb", "aaax");
        (string Pattern, ulong[] Expected)[] globCases =
        [
            ("[a-c]*", [0, 1, 4, 5]), ("[^a]*", [2]), ("a\\*\\?", [1]),
            ("*a*b*", [0, 4]), ("B[5-1]", [2]), ("?*?", [0, 1, 2, 4, 5]),
        ];
        foreach (var (pattern, expected) in globCases)
            await Assert.That(await arrays.GrepAsync("glob", RespireArrayBound.First, RespireArrayBound.Last,
                new RespireArrayPredicate(RespireArrayPredicateKind.Glob, pattern)))
                .IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That((await arrays.AggregateAsync("search", 3, 5, RespireArrayOperation.And)).Integer).IsEqualTo(0L);
        await Assert.That((await arrays.AggregateAsync("search", 3, 5, RespireArrayOperation.Or)).Integer).IsEqualTo(-1L);
        await Assert.That((await arrays.AggregateAsync("search", 3, 5, RespireArrayOperation.Xor)).Integer).IsEqualTo(-2L);
        await Assert.That((await arrays.AggregateAsync("search", 0, 5, RespireArrayOperation.Match, "Alpha")).Integer).IsEqualTo(1L);
        await Assert.That((await arrays.AggregateAsync("search", 0, 2, RespireArrayOperation.Sum)).IsNull).IsTrue();
        await Assert.That(await arrays.RangeAsync("missing", 3, 1)).IsEquivalentTo(new string?[] { null, null, null }, CollectionOrdering.Matching);
        await arrays.SetAsync("validation", 0, "original");
        await Assert.That(async () => await arrays.SetManyAsync("validation",
            new RespireArrayItem(0, "changed"), new RespireArrayItem(ulong.MaxValue, "invalid"))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(await arrays.GetStringAsync("validation", 0)).IsEqualTo("original");
        await Assert.That(async () => await arrays.SetAsync("validation", ulong.MaxValue - 1, "one", "two")).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await arrays.ScanPageAsync("validation", 0, 9, limit: 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await arrays.SetAsync("validation", 0, ReadOnlySpan<RespireValue>.Empty)).Throws<ArgumentException>();
        await client.Strings.SetAsync("wrong-type", "string");
        await Assert.That(async () => await arrays.CountAsync("wrong-type")).Throws<RespireServerException>();
        var info = await arrays.InfoAsync("search", full: true);
        await Assert.That(info.AverageDenseSize.HasValue).IsTrue();
        await Assert.That(info.AverageDenseFill.HasValue).IsTrue();
        await Assert.That(info.AverageSparseSize.HasValue).IsTrue();
        await Assert.That(await client.Keys.TypeAsync("search")).IsEqualTo(RespireKeyType.Array);
        await arrays.SetAsync("ranges", 0, "zero", "one", "two", "three", "four");
        await Assert.That(await arrays.DeleteRangeAsync("ranges", new RespireArrayRange(1, 2), new RespireArrayRange(3, 2))).IsEqualTo(3UL);
        await Assert.That(await arrays.CountAsync("ranges")).IsEqualTo(2UL);
    }

    internal static async Task DeferredCommands(IRespireClient client, bool transactional)
    {
        using var batch = transactional ? null : client.CreateBatch();
        await using var transaction = transactional ? client.CreateTransaction() : null;
        var queue = (IRespireCommandQueue?)transaction ?? batch!;
        var arrays = queue.Arrays;
        var set = arrays.Set("queued", 2, "2", "3");
        var manySet = arrays.SetMany("queued", new RespireArrayItem(0, "0"));
        var get = arrays.GetString("queued", 2);
        var typedGet = arrays.Get<int?>("queued", 1);
        var found = arrays.TryGet<int>("queued", 0);
        var many = arrays.GetMany<int?>("queued", 0, 1, 3);
        var range = arrays.Range<int?>("queued", 3, 0);
        var count = arrays.Count("queued");
        var length = arrays.Length("queued");
        var scan = arrays.ScanPage<int>("queued", 0, 3, limit: 2);
        RespireArrayPredicate[] predicates = [new(RespireArrayPredicateKind.Exact, "2")];
        var grep = arrays.Grep("queued", RespireArrayBound.First, RespireArrayBound.Last, predicates);
        var grepValues = arrays.GrepEntries<int>("queued", RespireArrayBound.First, RespireArrayBound.Last, predicates);
        var aggregate = arrays.Aggregate("queued", 3, 0, RespireArrayOperation.Sum);
        var info = arrays.Info("queued");
        var seek = arrays.Seek("queued", 4);
        var next = arrays.NextIndex("queued");
        var insert = arrays.Insert("queued", "4", "5");
        var ring = arrays.Ring("queued", 6, "6");
        var last = arrays.LastItems<int?>("queued", 3);
        var delete = arrays.Delete("queued", 5);
        var deleteRange = arrays.DeleteRange("queued", new RespireArrayRange(3, 2));
        RespireArrayItem[] input = [new(1, "original")];
        var copied = arrays.SetMany("copy", input);
        input[0] = new(9, "changed");
        var copiedRead = arrays.GetString("copy", 1);
        var payload = new Payload(17, "queued");
        var typedSet = arrays.Set("json", 0, payload);
        var typedRead = arrays.Get<Payload>("json", 0);
        RespireValue[] values = ["first", "second"];
        var arraySet = arrays.Set("value-array", 0, values);
        var arrayRead = arrays.Range("value-array", 0, 1);
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        await Assert.That(set.Result).IsEqualTo(2UL);
        await Assert.That(manySet.Result).IsEqualTo(1UL);
        await Assert.That(get.Result).IsEqualTo("2");
        await Assert.That(typedGet.Result).IsNull();
        await Assert.That(found.Result).IsEqualTo(new RespireGet<int>(true, 0));
        await Assert.That(many.Result).IsEquivalentTo(new int?[] { 0, null, 3 }, CollectionOrdering.Matching);
        await Assert.That(range.Result).IsEquivalentTo(new int?[] { 3, 2, null, 0 }, CollectionOrdering.Matching);
        await Assert.That(count.Result).IsEqualTo(3UL);
        await Assert.That(length.Result).IsEqualTo(4UL);
        await Assert.That(scan.Result).IsEquivalentTo(new[] { new RespireArrayEntry<int>(0, 0), new RespireArrayEntry<int>(2, 2) }, CollectionOrdering.Matching);
        await Assert.That(grep.Result).IsEquivalentTo(new[] { 2UL }, CollectionOrdering.Matching);
        await Assert.That(grepValues.Result).IsEquivalentTo(new[] { new RespireArrayEntry<int>(2, 2) }, CollectionOrdering.Matching);
        await Assert.That(aggregate.Result.NumericText).IsEqualTo("5");
        await Assert.That(info.Result.Count).IsEqualTo(3UL);
        await Assert.That(seek.Result).IsTrue();
        await Assert.That(next.Result).IsEqualTo(4UL);
        await Assert.That(insert.Result).IsEqualTo(5UL);
        await Assert.That(ring.Result).IsEqualTo(0UL);
        await Assert.That(last.Result).IsEquivalentTo(new int?[] { 4, 5, 6 }, CollectionOrdering.Matching);
        await Assert.That(delete.Result).IsEqualTo(1UL);
        await Assert.That(deleteRange.Result).IsEqualTo(2UL);
        await Assert.That(copied.Result).IsEqualTo(1UL);
        await Assert.That(copiedRead.Result).IsEqualTo("original");
        await Assert.That(typedSet.Result).IsEqualTo(1UL);
        await Assert.That(typedRead.Result).IsEqualTo(payload);
        await Assert.That(arraySet.Result).IsEqualTo(2UL);
        await Assert.That(arrayRead.Result).IsEquivalentTo(new string?[] { "first", "second" }, CollectionOrdering.Matching);
        await Assert.That(await client.Arrays.CountAsync("queued")).IsEqualTo(2UL);
    }

    internal static async Task SparseSlotsAndUnsignedIndexesPreservePresence(IRespireClient client)
    {
        var arrays = client.WithKeyPrefix("prefix:").Arrays;
        await Assert.That(await arrays.SetAsync("sparse", 2, "0", "7")).IsEqualTo(2UL);
        await Assert.That(await arrays.CountAsync("sparse")).IsEqualTo(2UL);
        await Assert.That(await arrays.LengthAsync("sparse")).IsEqualTo(4UL);
        await Assert.That((await arrays.TryGetAsync<int>("sparse", 1)).Found).IsFalse();
        await Assert.That(await arrays.TryGetAsync<int>("sparse", 2)).IsEqualTo(new RespireGet<int>(true, 0));
        await Assert.That(await arrays.RangeAsync<int?>("sparse", 3, 0)).IsEquivalentTo(new int?[] { 7, 0, null, null }, CollectionOrdering.Matching);
        await Assert.That(await arrays.GetManyAsync("sparse", 2, 1, 3)).IsEquivalentTo(new string?[] { "0", null, "7" }, CollectionOrdering.Matching);
        var high = (ulong)long.MaxValue + 10;
        await Assert.That(await arrays.SetManyAsync("sparse", new RespireArrayItem(high, "large"), new RespireArrayItem(2, "changed"))).IsEqualTo(1UL);
        await Assert.That(await arrays.GetStringAsync("sparse", high)).IsEqualTo("large");
        await Assert.That(await arrays.LengthAsync("sparse")).IsEqualTo(high + 1);
        await Assert.That(await client.Arrays.GetStringAsync("prefix:sparse", high)).IsEqualTo("large");
        await Assert.That(await arrays.DeleteAsync("sparse", high, high)).IsEqualTo(1UL);
        await Assert.That(await arrays.DeleteRangeAsync("sparse", new RespireArrayRange(3, 0))).IsEqualTo(2UL);
        await Assert.That(await arrays.LengthAsync("sparse")).IsEqualTo(0UL);
    }

    internal static async Task ScansPageByIndexInBothDirections(IRespireClient client)
    {
        var last = ulong.MaxValue - 1;
        await client.Arrays.SetManyAsync("scan", new RespireArrayItem(0, "first"), new RespireArrayItem(4, "middle"), new RespireArrayItem(last, "last"));
        await Assert.That(await client.Arrays.LengthAsync("scan")).IsEqualTo(ulong.MaxValue);
        List<RespireArrayEntry<string>> items = [];
        await foreach (var item in client.Arrays.ScanAsync("scan", last, 0, pageSize: 1)) items.Add(item);
        await Assert.That(items.Select(item => item.Index).ToArray()).IsEquivalentTo(new[] { last, 4UL, 0UL }, CollectionOrdering.Matching);
        await Assert.That(items.Select(item => item.Value).ToArray()).IsEquivalentTo(new string?[] { "last", "middle", "first" }, CollectionOrdering.Matching);
        var forward = await client.Arrays.ScanPageAsync("scan", 0, last, limit: 2);
        await Assert.That(forward.Select(item => item.Index).ToArray()).IsEquivalentTo(new[] { 0UL, 4UL }, CollectionOrdering.Matching);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () =>
        {
            await foreach (var _ in client.Arrays.ScanAsync("scan", 0, last, cancellationToken: cancellation.Token)) { }
        }).Throws<OperationCanceledException>();
    }

    internal static async Task CursorRingAndLastItemsRespectHolesAndResize(IRespireClient client)
    {
        await client.Arrays.SetAsync("sparse-last", 100, "last");
        await Assert.That(await client.Arrays.LastItemsAsync("sparse-last", 3)).IsEquivalentTo(new string?[] { "last" }, CollectionOrdering.Matching);
        await Assert.That(await client.Arrays.LastItemsAsync("sparse-last", 3, reverse: true)).IsEquivalentTo(new string?[] { "last" }, CollectionOrdering.Matching);
        await Assert.That(await client.Arrays.LastItemsAsync("sparse-last", 0)).IsEmpty();
        await Assert.That(await client.Arrays.LastItemsAsync("sparse-last", -1)).IsEmpty();
        await Assert.That(await client.Arrays.NextIndexAsync("missing")).IsEqualTo(0UL);
        await Assert.That(await client.Arrays.SeekAsync("missing", 5)).IsFalse();
        await Assert.That(await client.Arrays.InsertAsync("ring", "a", "b", "c")).IsEqualTo(2UL);
        await Assert.That(await client.Arrays.RingAsync("ring", 3, "d", "e")).IsEqualTo(1UL);
        await Assert.That(await client.Arrays.LastItemsAsync("ring", 3)).IsEquivalentTo(new string?[] { "c", "d", "e" }, CollectionOrdering.Matching);
        await Assert.That(await client.Arrays.RingAsync("ring", 5, "f")).IsEqualTo(3UL);
        await Assert.That(await client.Arrays.LastItemsAsync("ring", 4, reverse: true)).IsEquivalentTo(new string?[] { "f", "e", "d", "c" }, CollectionOrdering.Matching);
        await client.Arrays.DeleteAsync("ring", 2);
        await Assert.That(await client.Arrays.LastItemsAsync("ring", 3)).IsEquivalentTo(new string?[] { "d", null, "f" }, CollectionOrdering.Matching);
        await Assert.That(await client.Arrays.RingAsync("ring", 2, "g")).IsEqualTo(1UL);
        await Assert.That(await client.Arrays.LastItemsAsync("ring", 2)).IsEquivalentTo(new string?[] { "f", "g" }, CollectionOrdering.Matching);
        await client.Arrays.SeekAsync("ring", ulong.MaxValue);
        await Assert.That(await client.Arrays.NextIndexAsync("ring")).IsNull();
        await Assert.That(async () => await client.Arrays.InsertAsync("ring", "overflow")).Throws<RespireServerException>();
    }

    internal static async Task SearchAggregateAndInfoKeepReplyShapes(IRespireClient client)
    {
        await client.Arrays.SetAsync("numbers", 0, "1.5", "2", "text", "Text");
        var sum = await client.Arrays.AggregateAsync("numbers", 0, 3, RespireArrayOperation.Sum);
        await Assert.That(sum.NumericText).IsEqualTo("3.5");
        await Assert.That(sum.Integer).IsNull();
        await Assert.That((await client.Arrays.AggregateAsync("numbers", 0, 3, RespireArrayOperation.Used)).Integer).IsEqualTo(4L);
        await Assert.That((await client.Arrays.AggregateAsync("numbers", 9, 10, RespireArrayOperation.Min)).IsNull).IsTrue();
        await client.Arrays.SetAsync("zero", 0, "0");
        var zero = await client.Arrays.AggregateAsync("zero", 0, 0, RespireArrayOperation.Sum);
        await Assert.That(zero.IsNull).IsFalse();
        await Assert.That(zero.NumericText).IsEqualTo("0");
        RespireArrayPredicate[] predicates = [new(RespireArrayPredicateKind.Exact, "TEXT")];
        var options = new RespireArrayGrepOptions { IgnoreCase = true, Limit = 1 };
        await Assert.That(await client.Arrays.GrepAsync("numbers", RespireArrayBound.First, RespireArrayBound.Last, predicates, options)).IsEquivalentTo(new[] { 2UL }, CollectionOrdering.Matching);
        var entries = await client.Arrays.GrepEntriesAsync("numbers", RespireArrayBound.Last, RespireArrayBound.First, predicates, options);
        await Assert.That(entries).IsEquivalentTo(new[] { new RespireArrayEntry<string>(3, "Text") }, CollectionOrdering.Matching);
        var info = await client.Arrays.InfoAsync("numbers", full: true);
        await Assert.That(info.Count).IsEqualTo(4UL);
        await Assert.That(info.Length).IsEqualTo(4UL);
        await Assert.That(info.DenseSlices.HasValue).IsTrue();
        await Assert.That(async () => await client.Arrays.InfoAsync("missing")).Throws<RespireServerException>();
    }
}
