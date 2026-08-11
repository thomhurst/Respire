using System.Text.Json;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

/// <summary>
/// Covers the typed value surface against a real Redis: <c>TryGetAsync</c> presence reporting and
/// the serializing write/read pairs on the hash, list, set, and sorted-set facets.
/// </summary>
[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class TypedValueIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    public async Task TryGetAsync_SeparatesMissingKeyFromStoredDefault()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        // The gap TryGetAsync closes: GetAsync<int> answers 0 for both cases.
        (await client.GetAsync<int>("typed:absent")).Should().Be(0);

        var missing = await client.TryGetAsync<int>("typed:absent");
        missing.Found.Should().BeFalse();
        missing.Value.Should().Be(0);
        missing.GetValueOrDefault(-1).Should().Be(-1);

        await client.SetAsync("typed:zero", 0);
        var storedDefault = await client.TryGetAsync<int>("typed:zero");
        storedDefault.Found.Should().BeTrue();
        storedDefault.Value.Should().Be(0);
        storedDefault.GetValueOrDefault(-1).Should().Be(0);

        await client.SetAsync("typed:seven", 7);
        var (found, value) = await client.Strings.TryGetAsync<int>("typed:seven");
        found.Should().BeTrue();
        value.Should().Be(7);
    }

    [Test]
    public async Task TryGetAsync_ReadsSerializedPayloads()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var payload = new TypedPayload(7, "seven");

        await client.SetAsync("typed:payload", payload);

        var result = await client.TryGetAsync<TypedPayload>("typed:payload");
        result.Found.Should().BeTrue();
        result.Value.Should().Be(payload);

        ReadOnlyMemory<byte> memory = new byte[] { 0, 1, 254, 255 };
        await client.SetAsync("typed:memory", memory);
        (await client.GetAsync<ReadOnlyMemory<byte>>("typed:memory")).ToArray()
            .Should().Equal(memory.ToArray());
    }

    [Test]
    public async Task HashTypedSetAndGet_RoundTrip()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var payload = new TypedPayload(42, "answer");

        (await client.Hashes.SetAsync("typed:hash", "payload", payload)).Should().BeTrue();
        (await client.Hashes.GetAsync<TypedPayload>("typed:hash", "payload")).Should().Be(payload);

        // The typed overload writes raw bytes for strings and numbers, exactly like the
        // RespireValue overload it sits beside.
        await client.Hashes.SetAsync("typed:hash", "text", "plain");
        (await client.Hashes.GetStringAsync("typed:hash", "text")).Should().Be("plain");
        await client.Hashes.SetAsync("typed:hash", "count", 0);
        (await client.Hashes.GetStringAsync("typed:hash", "count")).Should().Be("0");

        var bytes = new byte[] { 0, 1, 254, 255 };
        await client.Hashes.SetAsync("typed:hash", "bytes", bytes);
        (await client.Hashes.GetBytesAsync("typed:hash", "bytes")).Should().Equal(bytes);

        ReadOnlyMemory<byte> memory = bytes;
        await client.Hashes.SetAsync("typed:hash", "memory", memory);
        (await client.Hashes.GetBytesAsync("typed:hash", "memory")).Should().Equal(bytes);
        await client.Hashes.SetAsync<RespireValue>("typed:hash", "raw", "text");
        (await client.Hashes.GetStringAsync("typed:hash", "raw")).Should().Be("text");

        string? nullText = null;
        Func<Task> setNullText = async () =>
            await client.Hashes.SetAsync("typed:hash", "null-text", nullText);
        await setNullText.Should().ThrowAsync<ArgumentException>();
        byte[]? nullBytes = null;
        Func<Task> setNullBytes = async () =>
            await client.Hashes.SetAsync("typed:hash", "null-bytes", nullBytes);
        await setNullBytes.Should().ThrowAsync<ArgumentException>();
        await client.Hashes.SetAsync("typed:hash", "nan", double.NaN);
        (await client.Hashes.GetStringAsync("typed:hash", "nan")).Should().Be("NaN");
        await client.Hashes.SetAsync("typed:hash", "char", 'A');
        (await client.Hashes.GetStringAsync("typed:hash", "char")).Should().Be("A");

        var storedDefault = await client.Hashes.TryGetAsync<int>("typed:hash", "count");
        storedDefault.Found.Should().BeTrue();
        storedDefault.Value.Should().Be(0);

        var missingField = await client.Hashes.TryGetAsync<int>("typed:hash", "absent");
        missingField.Found.Should().BeFalse();
        missingField.Value.Should().Be(0);
    }

    [Test]
    public async Task ListTypedPop_RoundTrip()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var payload = new TypedPayload(3, "three");

        await client.Lists.RightPushAsync("typed:list", 41, 42);
        (await client.Lists.LeftPopAsync<int>("typed:list")).Should().Be(41);
        (await client.Lists.RightPopAsync<int>("typed:list", TimeSpan.FromSeconds(5))).Should().Be(42);
        (await client.Lists.LeftPopAsync<int>("typed:list")).Should().Be(0);

        await client.Lists.RightPushAsync("typed:list:json", JsonSerializer.Serialize(payload));
        (await client.Lists.LeftPopAsync<TypedPayload>("typed:list:json")).Should().Be(payload);

        await client.Lists.RightPushAsync("typed:list:invalid", "not-an-integer");
        Func<Task> popInvalid = async () =>
            await client.Lists.LeftPopAsync<int>("typed:list:invalid", TimeSpan.FromSeconds(1));
        await popInvalid.Should().ThrowAsync<FormatException>();
    }

    [Test]
    public async Task ListCountPop_RoundTrip()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        await client.Lists.RightPushAsync("typed:list:count", 1, 2, 3);

        (await client.Lists.LeftPopManyAsync<int>("typed:list:count", 2)).Should().Equal(1, 2);
        (await client.Lists.RightPopManyAsync<int>("typed:list:count", 2)).Should().Equal(3);
        (await client.Lists.LeftPopManyAsync<int>("typed:list:count", 1)).Should().BeEmpty();
    }

    [Test]
    public async Task SetTypedContains_RoundTrip()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var payload = new TypedPayload(9, "nine");

        await client.Sets.AddAsync("typed:set", 7);
        (await client.Sets.ContainsAsync<int>("typed:set", 7)).Should().BeTrue();
        (await client.Sets.ContainsAsync<int>("typed:set", 8)).Should().BeFalse();

        await client.Sets.AddAsync("typed:set:bool", true);
        (await client.Sets.ContainsAsync("typed:set:bool", true)).Should().BeTrue();

        string? nullMember = null;
        Func<Task> addNullMember = async () => await client.Sets.AddAsync("typed:set:null", nullMember);
        await addNullMember.Should().ThrowAsync<ArgumentException>();
        Func<Task> containsNullMember = async () =>
            await client.Sets.ContainsAsync("typed:set:null", nullMember);
        await containsNullMember.Should().ThrowAsync<ArgumentException>();

        ReadOnlyMemory<byte> memoryMember = new byte[] { 0, 1, 254, 255 };
        await client.Sets.AddAsync("typed:set:memory", memoryMember);
        (await client.Sets.ContainsAsync("typed:set:memory", memoryMember)).Should().BeTrue();

        await client.Sets.AddAsync("typed:set:nan", double.NaN);
        (await client.Sets.ContainsAsync("typed:set:nan", double.NaN)).Should().BeTrue();

        await client.Sets.AddAsync("typed:set:char", 'A');
        (await client.Sets.ContainsAsync("typed:set:char", 'A')).Should().BeTrue();

        await client.Sets.AddAsync("typed:set:json", JsonSerializer.Serialize(payload));
        (await client.Sets.ContainsAsync("typed:set:json", payload)).Should().BeTrue();
    }

    [Test]
    public async Task SortedSetTypedAdd_RoundTrip()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var payload = new TypedPayload(5, "five");

        (await client.SortedSets.AddAsync("typed:zset", 7, 1.5)).Should().BeTrue();
        (await client.SortedSets.ScoreAsync("typed:zset", 7)).Should().Be(1.5);

        (await client.SortedSets.AddAsync("typed:zset:bool", true, 1.5)).Should().BeTrue();
        (await client.SortedSets.ScoreAsync("typed:zset:bool", true)).Should().Be(1.5);
        (await client.SortedSets.RankAsync("typed:zset:bool", true)).Should().Be(0);
        (await client.SortedSets.IncrementAsync("typed:zset:bool", true, 0.5)).Should().Be(2.0);
        (await client.SortedSets.RemoveAsync("typed:zset:bool", true)).Should().Be(1);

        string? nullMember = null;
        Func<Task> addNullMember = async () =>
            await client.SortedSets.AddAsync("typed:zset:null", nullMember, 1.5);
        await addNullMember.Should().ThrowAsync<ArgumentException>();
        Func<Task> scoreNullMember = async () =>
            await client.SortedSets.ScoreAsync("typed:zset:null", nullMember);
        await scoreNullMember.Should().ThrowAsync<ArgumentException>();

        ReadOnlyMemory<byte> memoryMember = new byte[] { 0, 1, 254, 255 };
        (await client.SortedSets.AddAsync("typed:zset:memory", memoryMember, 1.5)).Should().BeTrue();
        (await client.SortedSets.ScoreAsync("typed:zset:memory", memoryMember)).Should().Be(1.5);

        (await client.SortedSets.AddAsync("typed:zset:nan", double.NaN, 1.5)).Should().BeTrue();
        (await client.SortedSets.ScoreAsync("typed:zset:nan", double.NaN)).Should().Be(1.5);

        (await client.SortedSets.AddAsync("typed:zset:char", 'A', 1.5)).Should().BeTrue();
        (await client.SortedSets.ScoreAsync("typed:zset:char", 'A')).Should().Be(1.5);

        (await client.SortedSets.AddAsync("typed:zset", payload, 2.5)).Should().BeTrue();
        (await client.SortedSets.ScoreAsync("typed:zset", JsonSerializer.Serialize(payload))).Should().Be(2.5);
        (await client.SortedSets.RangeAsync("typed:zset")).Should().Equal("7", JsonSerializer.Serialize(payload));
    }

    private sealed record TypedPayload(int Number, string Name);
}
