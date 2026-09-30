using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<ClusterTransactionTestContainer>(Shared = SharedType.PerTestSession)]
public class ClusterStreamReadIntegrationTests(ClusterTransactionTestContainer fixture)
{
    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task SameSlotReadsAndCrossSlotRejection(int protocol, bool blocking)
    {
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(fixture.Host, fixture.Port)], UseCluster = true, Protocol = (RespProtocol)protocol,
        });
        var view = client.WithKeyPrefix($"stream-read:{Guid.NewGuid():N}:");
        await view.Streams.AddAsync("{a}:one", new StreamAddOptions { Id = "1-0" }, ("value", "first"));
        await view.Streams.AddAsync("{a}:two", new StreamAddOptions { Id = "2-0" }, ("value", "second"));
        var result = await view.Streams.ReadAsync([("{a}:one", "0"), ("{a}:two", "0")],
            waitFor: blocking ? TimeSpan.FromSeconds(1) : null);
        result.Select(x => x.Key.ToString()).Should().Equal("{a}:one", "{a}:two");
        result[0].Entries.Should().ContainSingle().Which.Id.Should().Be((RespireStreamId)"1-0");
        result[1].Entries.Should().ContainSingle().Which.Id.Should().Be((RespireStreamId)"2-0");
        Func<Task> crossSlot = async () => await view.Streams.ReadAsync([("{a}:one", "0"), ("{b}:two", "0")]);
        await crossSlot.Should().ThrowAsync<RespireServerException>().WithMessage("*CROSSSLOT*");
        await view.Keys.DeleteAsync("{a}:one", "{a}:two");
    }
}
