using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class StreamMetadataIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ConsumerCreationIsIdempotentAndDoesNotConsumeEntries(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix($"metadata:{Guid.NewGuid():N}:");
        await view.Streams.CreateGroupAsync("events", "workers", RespireStreamId.Beginning);
        (await view.Streams.CreateConsumerAsync("events", "workers", "alice")).Should().BeTrue();
        (await view.Streams.CreateConsumerAsync("events", "workers", "alice")).Should().BeFalse();
        var consumers = await view.Streams.ConsumerInfoAsync("events", "workers");
        consumers.Should().ContainSingle().Which.Name.Should().Be("alice");
        consumers[0].Pending.Should().Be(0);
        (await view.Streams.CountAsync("events")).Should().Be(0);
        Func<Task> missingGroup = async () => await view.Streams.CreateConsumerAsync("events", "missing", "bob");
        await missingGroup.Should().ThrowAsync<RespireServerException>();
        Func<Task> missingStream = async () => await view.Streams.CreateConsumerAsync("missing", "workers", "bob");
        await missingStream.Should().ThrowAsync<RespireServerException>();
        await view.SetAsync("wrong-type", "value");
        Func<Task> wrongType = async () => await view.Streams.CreateConsumerAsync("wrong-type", "workers", "bob");
        await wrongType.Should().ThrowAsync<RespireServerException>();
        await view.Keys.DeleteAsync("events", "wrong-type");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LastIdUpdatesOnlyRequestedMetadataAndPreservesBinaryEntries(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var prefix = $"metadata:{Guid.NewGuid():N}:";
        var view = client.WithKeyPrefix(prefix);
        byte[] payload = [0xff, 0, 0x80];
        await view.Streams.AddAsync("events", new StreamAddOptions { Id = "1-0" }, ("value", (RespireValue)payload));
        await view.Streams.CreateGroupAsync("events", "workers", "0-0");
        await view.Streams.SetLastIdAsync("events", "9-0", entriesAdded: 12, maxDeletedId: "2-0");
        var info = await view.Streams.InfoAsync("events");
        info.LastGeneratedId.Should().Be((RespireStreamId)"9-0");
        info.EntriesAdded.Should().Be(12);
        info.MaxDeletedEntryId.Should().Be((RespireStreamId)"2-0");
        info.Length.Should().Be(1);
        info.FirstEntry!.Value["value"].Should().Equal(payload);
        await view.Streams.SetLastIdAsync("events", "10-0");
        info = await view.Streams.InfoAsync("events");
        info.EntriesAdded.Should().Be(12);
        info.MaxDeletedEntryId.Should().Be((RespireStreamId)"2-0");
        await view.Streams.SetLastIdAsync("events", "11-0", entriesAdded: 20);
        await view.Streams.SetLastIdAsync("events", "12-0", maxDeletedId: "3-0");
        info = await view.Streams.InfoAsync("events");
        info.LastGeneratedId.Should().Be((RespireStreamId)"12-0");
        info.EntriesAdded.Should().Be(20);
        info.MaxDeletedEntryId.Should().Be((RespireStreamId)"3-0");
        (await view.Streams.GroupInfoAsync("events"))[0].LastDeliveredId.Should().Be((RespireStreamId)"0-0");
        (await client.Keys.ExistsAsync(prefix + "events")).Should().BeTrue();
        (await client.Keys.ExistsAsync(prefix + prefix + "events")).Should().BeFalse();
        // An explicit append below the new last-generated id must still be rejected.
        Func<Task> appendOlder = async () => await view.Streams.AddAsync("events", new StreamAddOptions { Id = "11-0" }, ("value", "old"));
        await appendOlder.Should().ThrowAsync<RespireServerException>();
        (await view.Streams.RangeAsync("events"))[0]["value"].Should().Equal(payload);
        await view.Keys.DeleteAsync("events");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task InvalidMetadataAndMissingOrWrongTypeStreamsReturnServerErrors(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix($"metadata:{Guid.NewGuid():N}:");
        await view.Streams.AddAsync("events", new StreamAddOptions { Id = "2-0" }, ("value", "first"));
        Func<Task>[] invalid =
        [
            async () => await view.Streams.SetLastIdAsync("events", "1-0"),
            async () => await view.Streams.SetLastIdAsync("events", "2-0", entriesAdded: 0),
            async () => await view.Streams.SetLastIdAsync("events", "2-0", maxDeletedId: "3-0"),
            async () => await view.Streams.SetLastIdAsync("events", "$"),
            async () => await view.Streams.SetLastIdAsync("events", "3-0", maxDeletedId: "invalid"),
            async () => await view.Streams.SetLastIdAsync("missing", "3-0"),
        ];
        foreach (var operation in invalid) await operation.Should().ThrowAsync<RespireServerException>();
        await view.SetAsync("wrong-type", "value");
        Func<Task> wrongType = async () => await view.Streams.SetLastIdAsync("wrong-type", "3-0");
        await wrongType.Should().ThrowAsync<RespireServerException>();
        var info = await view.Streams.InfoAsync("events");
        info.LastGeneratedId.Should().Be((RespireStreamId)"2-0");
        info.EntriesAdded.Should().Be(1);
        await view.Keys.DeleteAsync("events", "wrong-type");
    }
}
