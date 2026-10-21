using Respire.Internal;
using Respire.Tests.Networking;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CrossSlotOriginDeterminesTelemetry(bool local)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer("-CROSSSLOT injected reply\r\n"u8.ToArray());
#pragma warning disable RESP010 // Each telemetry scenario owns and disposes its client so connection and observation state cannot leak between tests.
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, UseCluster = local, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
            IncludeCrossSlotKeyContext = true,
        });
#pragma warning restore RESP010
        using var capture = new Capture();
        async Task Invoke() => await client.Strings.GetManyAsync("{first}:private", "{second}:private");
        if (local) await Assert.That(Invoke).ThrowsExactly<RespireCrossSlotException>();
        else await Assert.That(Invoke).ThrowsExactly<RespireServerException>();
        var tags = capture.Items.Single().Tags;
        await Assert.That(tags["redis.client.errors.category"]).IsEqualTo(local ? "validation" : "server");
        await Assert.That(tags.ContainsKey("db.response.status_code")).IsEqualTo(!local);
        if (!local) await Assert.That(tags["db.response.status_code"]).IsEqualTo("CROSSSLOT");
        await Assert.That(tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(tags.Values.Any(value => value?.ToString()?.Contains("private", StringComparison.Ordinal) == true)).IsFalse();
        if (local)
        {
            await Assert.That(tags.ContainsKey("server.address")).IsFalse();
            await Assert.That(tags.ContainsKey("server.port")).IsFalse();
            await Assert.That(server.ReceivedCommands).IsEmpty();
        }
    }
}
