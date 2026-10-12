using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class TopologyCommandRetryTests
{
    [Test]
    [Arguments(RespireCommandRetryCategory.ReadOnly, false)]
    [Arguments(RespireCommandRetryCategory.WriteLastWins, false)]
    [Arguments(RespireCommandRetryCategory.ReadOnly, true)]
    [Arguments(RespireCommandRetryCategory.WriteLastWins, true)]
    public async Task SentinelBatchRetryRediscoversAndReplaysCompleteUnit(RespireCommandRetryCategory maximum, bool circuits)
    {
        var demoted = 0;
        await using var replacement = Primary(command => command == "GET key" ? Value : null);
        await using var former = Primary(command =>
        {
            if (command == "ROLE") return RoleReply(Volatile.Read(ref demoted) == 0 ? "master" : "slave");
            if (command == "GET key") return "$3\r\nold\r\n"u8.ToArray();
            if (command != "SET key value") return null;
            Volatile.Write(ref demoted, 1);
            return "-READONLY You can't write against a read only replica.\r\n"u8.ToArray();
        });
        await using var sentinel = Sentinel(() => Volatile.Read(ref demoted) == 0 ? former.Port : replacement.Port);
        await using var client = await RespireClient.ConnectAsync(SentinelOptions(sentinel) with
        {
            CircuitBreaker = circuits ? new() : null,
            RetryPolicy = new() { InitialBackoff = TimeSpan.Zero, MaxCommandRetryCategory = maximum },
        });
        using var batch = client.CreateBatch();
        var read = batch.GetString("key");
        var write = batch.Set("key", "value");
        var result = await batch.TryExecuteAsync();
        var retries = maximum == RespireCommandRetryCategory.WriteLastWins;
        await Assert.That(result.FailureCount).IsEqualTo(retries ? 0 : 1);
        await Assert.That(read.Result).IsEqualTo(retries ? "value" : "old");
        await Assert.That(write.HasResult).IsEqualTo(retries);
        await Assert.That(former.ReceivedCommands.Count(command => command == "SET key value")).IsEqualTo(1);
        await Assert.That(replacement.ReceivedCommands.Count(command => command == "SET key value")).IsEqualTo(retries ? 1 : 0);
        await Assert.That(replacement.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(retries ? 1 : 0);
    }

    [Test]
    public async Task SentinelBatchConnectionLossReplaysAllReadsOnNewPrimary()
    {
        var failed = 0;
        await using var replacement = Primary(command => command.StartsWith("GET ") ? Value : null);
        FakeRespServer? formerServer = null;
        await using var former = Primary(command =>
        {
            // A demoted primary reports itself as a replica. The startup monitor's validation
            // discovery can read Sentinel before the failure and connect after it; ROLE must
            // reject that stale candidate, as it would for a real failover.
            if (command == "ROLE") return RoleReply(Volatile.Read(ref failed) == 0 ? "master" : "slave");
            if (command == "GET first") return "$3\r\nold\r\n"u8.ToArray();
            if (command != "GET second") return null;
            Volatile.Write(ref failed, 1);
            formerServer!.CloseConnections();
            return null;
        });
        formerServer = former;
        await using var sentinel = Sentinel(() => Volatile.Read(ref failed) == 0 ? former.Port : replacement.Port);
        await using var client = await RespireClient.ConnectAsync(SentinelOptions(sentinel));
        using var batch = client.CreateBatch();
        var first = batch.GetString("first");
        var second = batch.GetString("second");
        await batch.ExecuteAsync();
        await Assert.That(first.Result).IsEqualTo("value");
        await Assert.That(second.Result).IsEqualTo("value");
        await Assert.That(former.ReceivedCommands.Count(command => command == "GET first")).IsEqualTo(1);
        await Assert.That(former.ReceivedCommands.Count(command => command == "GET second")).IsEqualTo(1);
        await Assert.That(replacement.ReceivedCommands.Where(command => command.StartsWith("GET ")).ToArray())
            .IsEquivalentTo(new[] { "GET first", "GET second" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
