using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<ClusterTransactionTestContainer>(Shared = SharedType.PerTestSession)]
public class ClusterImmediateRawIntegrationTests(ClusterTransactionTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SameSlotBinaryCommandsWorkAndCrossSlotCommandsLeaveDataUntouched(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(fixture.Host, fixture.Port)], UseCluster = true, Connections = 1,
            Protocol = (RespProtocol)protocol,
        });
        var suffix = System.Text.Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N"));
        byte[] first = [255, .. "{raw}:first:"u8, .. suffix];
        byte[] second = [128, .. "{raw}:second:"u8, .. suffix];
        var other = $"{{other}}:{Guid.NewGuid():N}";
        try
        {
            using var stored = await client.ExecuteAsync(RespireCommands.String.MSET, first, "one", second, "two");
            stored.AsString().Should().Be("OK");
            using var read = await client.ExecuteAsync(RespireCommands.String.MGET, first, second);
            read[0].AsString().Should().Be("one");
            read[1].AsString().Should().Be("two");
            using var script = await client.ExecuteAsync(RespireCommands.Scripting.EVAL, "return {KEYS[1],KEYS[2],ARGV[1]}",
                2, first, second, "{not-a-key}");
            script[0].AsBytes().Should().Equal(first);
            script[1].AsBytes().Should().Equal(second);
            script[2].AsString().Should().Be("{not-a-key}");
            Func<Task> crossSlot = async () =>
            {
                using var ignored = await client.ExecuteAsync(RespireCommands.String.MSET, first, "changed", other, "bad");
            };
            (await crossSlot.Should().ThrowAsync<RespireServerException>()).Which.Code.Should().Be("CROSSSLOT");
            Func<Task> discarded = async () => await client.ExecuteFireAndForgetAsync(RespireCommands.String.MSET,
                first, "changed", other, "bad");
            (await discarded.Should().ThrowAsync<RespireServerException>()).Which.Code.Should().Be("CROSSSLOT");
            Func<Task> blocking = async () =>
            {
                using var ignored = await client.ExecuteAsync(RespireCommands.List.BLPOP, first, other, 0.01);
            };
            (await blocking.Should().ThrowAsync<RespireServerException>()).Which.Code.Should().Be("CROSSSLOT");
            Func<Task> streams = async () =>
            {
                using var ignored = await client.ExecuteAsync(RespireCommands.Stream.XREAD,
                    "COUNT", 1, "STREAMS", first, other, "0", "0");
            };
            (await streams.Should().ThrowAsync<RespireServerException>()).Which.Code.Should().Be("CROSSSLOT");
            (await client.GetStringAsync(first)).Should().Be("one");
            (await client.Keys.ExistsAsync(other)).Should().BeFalse();
        }
        finally
        {
            await client.Keys.DeleteAsync(first, second);
            await client.Keys.DeleteAsync(other);
        }
    }
}
