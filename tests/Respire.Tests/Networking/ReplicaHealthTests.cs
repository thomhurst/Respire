using System.Diagnostics;
using System.Text;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReplicaHealthTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    [Test]
    public async Task RoleCheckIsFreshThenStaleThenRequired()
    {
        var health = new ReplicaHealth<object>();
        var unknown = new object();
        var fresh = new object();
        var stale = new object();
        var expired = new object();

        await Assert.That(health.Record(fresh, Stopwatch.GetTimestamp(), Parse(LinkedReplica))).IsTrue();
        await Assert.That(health.Record(stale, Ago(1.5), Parse(LinkedReplica))).IsTrue();
        await Assert.That(health.Record(expired, Ago(2.5), Parse(LinkedReplica))).IsTrue();

        await Assert.That(health.Check(unknown, Interval)).IsEqualTo(ReplicaValidation.Required);
        await Assert.That(health.Check(fresh, Interval)).IsEqualTo(ReplicaValidation.Fresh);
        // Within two intervals one caller revalidates while the others keep using the connection.
        await Assert.That(health.Check(stale, Interval)).IsEqualTo(ReplicaValidation.Stale);
        await Assert.That(health.Check(expired, Interval)).IsEqualTo(ReplicaValidation.Required);
        // A zero interval revalidates on every read.
        await Assert.That(health.Check(fresh, TimeSpan.Zero)).IsEqualTo(ReplicaValidation.Required);
    }

    [Test]
    public async Task NonReplicaRoleForgetsAValidatedConnection()
    {
        var health = new ReplicaHealth<object>();
        var connection = new object();
        await Assert.That(health.Record(connection, Stopwatch.GetTimestamp(), Parse(LinkedReplica))).IsTrue();

        // The node was promoted: its next ROLE reports master.
        await Assert.That(health.Record(connection, Stopwatch.GetTimestamp(), Parse("*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n")))
            .IsFalse();
        await Assert.That(health.Check(connection, Interval)).IsEqualTo(ReplicaValidation.Required);
    }

    [Test]
    public async Task ReplicationLinkStateFollowsTheLatestRoleCheck()
    {
        var health = new ReplicaHealth<object>();
        var connection = new object();

        await health.RecordAsync(connection, "*5\r\n$7\r\nreplica\r\n$1\r\nh\r\n:1\r\n$4\r\nsync\r\n:0\r\n");
        await Assert.That(health.IsReplicationLinkDown).IsTrue();
        await health.RecordAsync(connection, LinkedReplica);
        await Assert.That(health.IsReplicationLinkDown).IsFalse();
        // Old servers may omit the link state; treat that as not linked.
        await health.RecordAsync(connection, "*1\r\n$5\r\nslave\r\n");
        await Assert.That(health.IsReplicationLinkDown).IsTrue();
    }

    [Test]
    public async Task FailureCooldownEndsAfterTheIntervalOrASuccessfulRoleCheck()
    {
        var health = new ReplicaHealth<object>();
        await Assert.That(health.IsCoolingDown(Interval)).IsFalse();

        health.MarkFailed();
        await Assert.That(health.IsCoolingDown(Interval)).IsTrue();
        await Assert.That(health.IsCoolingDown(TimeSpan.Zero)).IsFalse();

        await health.RecordAsync(new object(), LinkedReplica);
        await Assert.That(health.IsCoolingDown(Interval)).IsFalse();
    }

    private const string LinkedReplica = "*5\r\n$5\r\nslave\r\n$1\r\nh\r\n:1\r\n$9\r\nconnected\r\n:0\r\n";

    private static long Ago(double intervals)
        => Stopwatch.GetTimestamp() - (long)(Interval.TotalSeconds * intervals * Stopwatch.Frequency);

    private static RespValue Parse(string wire)
    {
        var position = 0;
        if (RespParser.TryParseValue(Encoding.UTF8.GetBytes(wire), ref position, out var value) != RespParseStatus.Done)
            throw new InvalidOperationException("Invalid test frame.");
        return value;
    }
}

internal static class ReplicaHealthTestExtensions
{
    internal static async Task RecordAsync(this ReplicaHealth<object> health, object connection, string role)
    {
        var position = 0;
        if (RespParser.TryParseValue(Encoding.UTF8.GetBytes(role), ref position, out var value) != RespParseStatus.Done)
            throw new InvalidOperationException("Invalid test frame.");
        await Assert.That(health.Record(connection, Stopwatch.GetTimestamp(), in value)).IsTrue();
    }
}
