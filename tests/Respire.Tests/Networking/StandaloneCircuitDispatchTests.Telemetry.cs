namespace Respire.Tests.Networking;

public partial class StandaloneCircuitDispatchTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CircuitRejectionsCountEachBatchEntryAndOneTransactionPermit(bool halfOpen)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var measurements = new CircuitTelemetryTests.Measurements();
        await using var server = QueueServer();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            CircuitBreaker = Options(server).CircuitBreaker! with { HalfOpenProbeCount = 1 },
        });
        var (_, clock) = await Prepare(client, server);
        Open(client, server);
        if (halfOpen) clock.Advance(TimeSpan.FromMinutes(1));
        var admission = halfOpen ? client.Core.Circuits!.Acquire(new("127.0.0.1", server.Port), default) : default;
        try
        {
            await Assert.That(await Failure(async () => await client.GetStringAsync("secret-key")))
                .IsTypeOf<RespireCircuitOpenException>();
            using var batch = client.CreateBatch();
            var first = batch.Strings.GetString("first-secret");
            var second = batch.Strings.GetString("second-secret");
            var third = batch.Strings.GetString("third-secret");
            await Assert.That(await Failure(async () => await batch.ExecuteAsync())).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(first.Error).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(second.Error).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(third.Error).IsTypeOf<RespireCircuitOpenException>();
            await using var transaction = client.CreateTransaction();
            var queuedFirst = transaction.Strings.GetString("transaction-secret");
            var queuedSecond = transaction.Strings.GetString("transaction-secret-2");
            await Assert.That(await Failure(async () => await transaction.CommitAsync())).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(queuedFirst.Error).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(queuedSecond.Error).IsTypeOf<RespireCircuitOpenException>();
            var rejected = measurements.Events.Where(e => e.Name == CircuitTelemetryTests.Rejections).ToArray();
            await Assert.That(rejected.Length).IsEqualTo(5);
            await Assert.That(rejected.All(e => Equals(e.Tags["server.port"], server.Port))).IsTrue();
        }
        finally { admission.Dispose(); }
    }
}
