using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class DedicatedPoolOptionsTests
{
    [Test]
    public async Task DefaultsPreserveUnlimitedAcquisitionAndFourIdleConnections()
    {
        var options = new RespireOptions { Endpoints = [new("localhost")] }.ValidateAndSnapshot();
        await Assert.That(options.DedicatedPool.MinimumSize).IsEqualTo(0);
        await Assert.That(options.DedicatedPool.MaximumSize).IsNull();
        await Assert.That(options.DedicatedPool.MaximumIdle).IsEqualTo(4);
        await Assert.That(options.DedicatedPool.IdleTimeout).IsNull();
        await Assert.That(options.DedicatedPool.UnblockOnCancel).IsFalse();
        await Assert.That(RespireConnectionOptions.Default.DedicatedPool).IsEqualTo(options.DedicatedPool);
    }

    [Test]
    public async Task SnapshotMapsDedicatedLimitsWithoutChangingMultiplexedConnections()
    {
        var configured = new RespireDedicatedPoolOptions
        {
            MinimumSize = 2, MaximumSize = 3, MaximumIdle = 8, IdleTimeout = TimeSpan.FromMinutes(2), UnblockOnCancel = true,
        };
        var source = new RespireOptions { Endpoints = [new("localhost")], Connections = 7, DedicatedPool = configured };
        var snapshot = source.ValidateAndSnapshot();
        var replacement = source with { DedicatedPool = configured with { MaximumSize = 12 } };
        var transport = snapshot.ToConnectionOptions();
        await Assert.That(snapshot.Connections).IsEqualTo(7);
        await Assert.That(snapshot.DedicatedPool).IsEqualTo(configured);
        await Assert.That(transport.DedicatedPool).IsEqualTo(configured);
        await Assert.That(replacement.DedicatedPool.MaximumSize).IsEqualTo(12);
        await Assert.That(transport.DedicatedPool.MaximumSize).IsEqualTo(3);
    }

    [Test]
    [Arguments(-1, null, 4)]
    [Arguments(0, 0, 4)]
    [Arguments(0, -1, 4)]
    [Arguments(2, 1, 4)]
    [Arguments(2, null, 1)]
    [Arguments(0, null, -1)]
    public async Task InvalidSizeCombinationsAreRejected(int minimum, int? maximum, int maximumIdle)
    {
        var error = await Assert.That(() => new RespireOptions
        {
            Endpoints = [new("localhost")],
            DedicatedPool = new() { MinimumSize = minimum, MaximumSize = maximum, MaximumIdle = maximumIdle },
        }.ValidateAndSnapshot()).ThrowsExactly<RespireConfigurationException>();
        await Assert.That(error!.Message).Contains("DedicatedPool");
    }

    [Test]
    [Arguments(-1.0)]
    [Arguments(0.0)]
    [Arguments(0.5)]
    [Arguments(86400001.0)]
    public async Task InvalidIdleTimeoutIsRejected(double milliseconds)
    {
        var error = await Assert.That(() => new RespireOptions
        {
            Endpoints = [new("localhost")],
            DedicatedPool = new() { IdleTimeout = TimeSpan.FromMilliseconds(milliseconds) },
        }.ValidateAndSnapshot()).ThrowsExactly<RespireConfigurationException>();
        await Assert.That(error!.Message).Contains("DedicatedPool");
    }

    [Test]
    [Arguments(1.0)]
    [Arguments(86400000.0)]
    public async Task IdleTimeoutBoundaryIsAccepted(double milliseconds)
    {
        var options = new RespireOptions
        {
            Endpoints = [new("localhost")],
            DedicatedPool = new() { IdleTimeout = TimeSpan.FromMilliseconds(milliseconds) },
        }.ValidateAndSnapshot();
        await Assert.That(options.DedicatedPool.IdleTimeout).IsEqualTo(TimeSpan.FromMilliseconds(milliseconds));
    }

    [Test]
    public async Task NullDedicatedPoolIsRejected()
    {
        await Assert.That(() => new RespireOptions
        {
            Endpoints = [new("localhost")], DedicatedPool = null!,
        }.ValidateAndSnapshot()).ThrowsExactly<RespireConfigurationException>();
    }
}
