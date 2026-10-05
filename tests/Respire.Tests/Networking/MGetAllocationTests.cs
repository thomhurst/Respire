using System.Runtime.CompilerServices;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class MGetAllocationTests
{
    /// <summary>Compares fully cached MGET allocations with the required result array and a detectable positive control.</summary>
    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CachedMGetAllocatesOnlyResultArray(bool cluster)
    {
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command.StartsWith("HELLO", StringComparison.Ordinal)
                ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray()
                : command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray() : FakeRespServer.OkReply,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = cluster,
            Endpoints = [new("127.0.0.1", server.Port)], ClientSideCache = new(),
        });
        RespireKey[] keys = ["{mget}:a", "{mget}:b"];
        foreach (var key in keys)
        {
            var token = client.Core.ClientCache!.BeginRead(in key);
            var value = RespValue.BulkString("42"u8.ToArray());
            client.Core.ClientCache.CompleteRead(in token, in value, allowInsert: true);
        }
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(2);
        Measure(client, keys, baseline: false, positiveControl: false);
        Measure(client, keys, baseline: true, positiveControl: false);
        Measure(client, keys, baseline: true, positiveControl: true);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => (
            Actual: Measure(client, keys, false, false),
            Baseline: Measure(client, keys, true, false),
            Control: Measure(client, keys, true, true)));
        await Assert.That(measured.Actual).IsEqualTo(measured.Baseline);
        await Assert.That(measured.Control).IsGreaterThan(measured.Baseline);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("MGET", StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>Measures warmed reads or control allocations inside the caller's concurrent-GC exclusion.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(RespireClient client, RespireKey[] keys, bool baseline, bool positiveControl)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var result = baseline ? new int[keys.Length] : client.CachedGetManyAsync(
                keys, default, static (RespireClient _, in RespValue value) => 42).GetAwaiter().GetResult();
            GC.KeepAlive(result);
            if (positiveControl) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
