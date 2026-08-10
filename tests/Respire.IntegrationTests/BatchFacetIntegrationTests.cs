using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

/// <summary>
/// The facet surface shared by <see cref="RespireBatch"/> and <see cref="RespireTransaction"/>,
/// exercised against a real Redis: at least two commands per facet on each host, with every
/// result read back through its <see cref="RespirePending{T}"/> after the flush or commit.
/// </summary>
[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class BatchFacetIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    public async Task Batch_StringAndKeyFacets_RoundTrip()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        var batch = client.CreateBatch();
        var set = batch.Strings.Set("batch:s", "alpha");
        var appended = batch.Strings.Append("batch:s", "-beta");
        var length = batch.Strings.Length("batch:s");
        var value = batch.Strings.GetString("batch:s");
        var many = batch.Strings.GetMany("batch:s", "batch:missing");
        var exists = batch.Keys.Exists("batch:s");
        var type = batch.Keys.Type("batch:s");
        var expired = batch.Keys.Expire("batch:s", RespireExpiry.In(TimeSpan.FromMinutes(5)));
        var expiry = batch.Keys.Expiry("batch:s");
        var persisted = batch.Keys.Expire("batch:s", RespireExpiry.Persist);

        await batch.ExecuteAsync();

        set.Result.Should().BeTrue();
        appended.Result.Should().Be(10);
        length.Result.Should().Be(10);
        value.Result.Should().Be("alpha-beta");
        many.Result.Should().Equal("alpha-beta", null);
        exists.Result.Should().BeTrue();
        type.Result.Should().Be("string");
        expired.Result.Should().BeTrue();
        expiry.Result.HasExpiry.Should().BeTrue();
        persisted.Result.Should().BeTrue();

        // A pending is awaitable as well as readable through .Result.
        (await client.Keys.ExpiryAsync("batch:s")).HasExpiry.Should().BeFalse();
    }

    [Test]
    public async Task Batch_CollectionFacets_RoundTrip()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        var batch = client.CreateBatch();
        var hashSet = batch.Hashes.Set("batch:h", ("name", "Ada"), ("lang", "en"));
        var hashIncrement = batch.Hashes.Increment("batch:h", "visits", 3);
        var hashAll = batch.Hashes.GetAll("batch:h");
        var hashCount = batch.Hashes.Count("batch:h");
        var pushed = batch.Lists.RightPush("batch:l", "a", "b", "c");
        var listRange = batch.Lists.Range("batch:l");
        var popped = batch.Lists.LeftPop("batch:l");
        var listCount = batch.Lists.Count("batch:l");
        var added = batch.Sets.Add("batch:set", "x", "y");
        var contains = batch.Sets.Contains("batch:set", "x");
        var count = batch.Sets.Count("batch:set");
        var ranked = batch.SortedSets.Add(
            "batch:z", new SortedSetEntry("ada", 42), new SortedSetEntry("grace", 58));
        var score = batch.SortedSets.Score("batch:z", "grace");
        var leaderboard = batch.SortedSets.RangeWithScores("batch:z", descending: true);

        await batch.ExecuteAsync();

        hashSet.Result.Should().Be(2);
        hashIncrement.Result.Should().Be(3);
        hashAll.Result.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["name"] = "Ada",
            ["lang"] = "en",
            ["visits"] = "3",
        });
        hashCount.Result.Should().Be(3);
        pushed.Result.Should().Be(3);
        listRange.Result.Should().Equal("a", "b", "c");
        popped.Result.Should().Be("a");
        listCount.Result.Should().Be(2);
        added.Result.Should().Be(2);
        contains.Result.Should().BeTrue();
        count.Result.Should().Be(2);
        ranked.Result.Should().Be(2);
        score.Result.Should().Be(58);
        leaderboard.Result.Should().Equal(
            new SortedSetEntry("grace", 58), new SortedSetEntry("ada", 42));
    }

    [Test]
    public async Task Batch_BitmapHyperLogLogAndGeoFacets_RoundTrip()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        var batch = client.CreateBatch();
        var bitSet = batch.Bitmaps.GetAndSet("batch:bits", 4, true);
        var bitCount = batch.Bitmaps.Count("batch:bits");
        var bitPosition = batch.Bitmaps.Position("batch:bits", true);
        var hllAdded = batch.HyperLogLog.Add("batch:hll", "ada", "grace");
        var hllCount = batch.HyperLogLog.Count("batch:hll");
        var geoAdded = batch.Geo.Add(
            "batch:cities", entries:
            [
                new GeoEntry(-0.1276, 51.5072, "london"),
                new GeoEntry(2.3522, 48.8566, "paris"),
            ]);
        var distance = batch.Geo.Distance("batch:cities", "london", "paris", GeoUnit.Kilometers);
        var positions = batch.Geo.Position("batch:cities", "london", "missing");

        await batch.ExecuteAsync();

        bitSet.Result.Should().BeFalse();
        bitCount.Result.Should().Be(1);
        bitPosition.Result.Should().Be(4);
        hllAdded.Result.Should().BeTrue();
        hllCount.Result.Should().Be(2);
        geoAdded.Result.Should().Be(2);
        distance.Result.Should().BeInRange(340, 350);
        positions.Result.Should().SatisfyRespectively(
            position => position.Should().NotBeNull(),
            position => position.Should().BeNull());
    }

    [Test]
    public async Task Transaction_Facets_RoundTripAtomically()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        var transaction = client.CreateTransaction();
        var stored = transaction.Strings.Set("tx:facet:s", "alpha");
        var incremented = transaction.Strings.Increment("tx:facet:counter", 4);
        var hashSet = transaction.Hashes.Set("tx:facet:h", "name", "Ada");
        var hashValue = transaction.Hashes.GetString("tx:facet:h", "name");
        var pushed = transaction.Lists.LeftPush("tx:facet:l", "one", "two");
        var listCount = transaction.Lists.Count("tx:facet:l");
        var added = transaction.Sets.Add("tx:facet:set", "x", "y", "z");
        var members = transaction.Sets.Members("tx:facet:set");
        var ranked = transaction.SortedSets.Add("tx:facet:z", "ada", 42);
        var rank = transaction.SortedSets.Rank("tx:facet:z", "ada");
        var keyType = transaction.Keys.Type("tx:facet:h");
        var removed = transaction.Keys.Delete("tx:facet:s", "tx:facet:missing");

        var committed = await transaction.CommitAsync();

        committed.Should().BeTrue();
        stored.Result.Should().BeTrue();
        incremented.Result.Should().Be(4);
        hashSet.Result.Should().BeTrue();
        hashValue.Result.Should().Be("Ada");
        pushed.Result.Should().Be(2);
        listCount.Result.Should().Be(2);
        added.Result.Should().Be(3);
        members.Result.Should().BeEquivalentTo("x", "y", "z");
        ranked.Result.Should().BeTrue();
        rank.Result.Should().Be(0);
        keyType.Result.Should().Be("hash");
        removed.Result.Should().Be(1);

        // The DEL inside the transaction applied, and the connection is usable afterwards.
        (await client.ExistsAsync("tx:facet:s")).Should().BeFalse();
        (await client.Lists.RangeAsync("tx:facet:l")).Should().Equal("two", "one");
    }

    [Test]
    public async Task RootShortcuts_MirrorTheClientRoot()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        await client.SetAsync("root:a", "1");
        await client.SetAsync("root:b", "2");

        var batch = client.CreateBatch();
        var typed = batch.Set("root:typed", 7);
        var read = batch.Get<int>("root:typed");
        var bytes = batch.GetBytes("root:a");
        var incremented = batch.Increment("root:counter", 5);
        var decremented = batch.Decrement("root:counter", 2);
        var expired = batch.Expire("root:counter", TimeSpan.FromMinutes(1));
        var present = batch.Exists("root:a");
        // Parity with IRespireClient.DeleteAsync: many keys, not one.
        var deleted = batch.Delete("root:a", "root:b", "root:missing");

        await batch.ExecuteAsync();

        typed.Result.Should().BeTrue();
        read.Result.Should().Be(7);
        bytes.Result.Should().Equal("1"u8.ToArray());
        incremented.Result.Should().Be(5);
        decremented.Result.Should().Be(3);
        expired.Result.Should().BeTrue();
        present.Result.Should().BeTrue();
        deleted.Result.Should().Be(2);
    }

    [Test]
    public async Task BatchAndTransaction_ShareTheSameFacetInterfaces()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        // Helper code can queue into either host through the shared interface.
        static RespirePending<long> QueueAudit(IBatchListCommands lists)
            => lists.RightPush("shared:audit", "entry");

        var batch = client.CreateBatch();
        var batched = QueueAudit(batch.Lists);
        await batch.ExecuteAsync();

        var transaction = client.CreateTransaction();
        var transacted = QueueAudit(transaction.Lists);
        var committed = await transaction.CommitAsync();

        batched.Result.Should().Be(1);
        committed.Should().BeTrue();
        transacted.Result.Should().Be(2);
        (await client.Lists.RangeAsync("shared:audit")).Should().Equal("entry", "entry");
    }
}
