using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ConnectionOptionsTests
{
    [Test]
    [Arguments(-2.0)]
    [Arguments(0.0)]
    [Arguments(0.5)]
    public async Task GatheredWriteTimeoutRejectsInvalidBounds(double milliseconds)
    {
        var error = await Assert.That(() => new RespireOptions
        {
            Endpoints = [new("localhost")],
            GatheredWriteStallTimeout = TimeSpan.FromMilliseconds(milliseconds),
        }.ValidateAndSnapshot()).ThrowsExactly<RespireConfigurationException>();
        await Assert.That(error!.Message).Contains(nameof(RespireOptions.GatheredWriteStallTimeout));
        await Assert.That(async () => await RespireConnection.ConnectAsync("localhost", 0,
            new RespireConnectionOptions { GatheredWriteStallTimeout = TimeSpan.FromMilliseconds(milliseconds) }))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(null, null, null)]
    [Arguments(1000, null, 1000)]
    [Arguments(null, 2000, 2000)]
    [Arguments(1000, 2000, 2000)]
    [Arguments(1000, -1, null)]
    public async Task GatheredWriteTimeoutResolvesCompatibilityAndExplicitOverrides(int? read, int? write, int? expected)
    {
        var options = new RespireOptions
        {
            Endpoints = [new("localhost")],
            ConnectionIdleReadTimeout = read is { } r ? TimeSpan.FromMilliseconds(r) : null,
            GatheredWriteStallTimeout = write is { } w ? TimeSpan.FromMilliseconds(w) : null,
        }.ValidateAndSnapshot();
        var transport = options.ToConnectionOptions();
        await Assert.That(transport.ResponseTimeout).IsEqualTo(options.ConnectionIdleReadTimeout);
        await Assert.That(transport.EffectiveGatheredWriteStallTimeout)
            .IsEqualTo(expected is { } e ? TimeSpan.FromMilliseconds(e) : null);
    }

    [Test]
    public async Task KernelSocketBuffers_DefaultToOsAutotuning()
    {
        // 0 means "leave the OS default". Explicitly setting SO_RCVBUF/SO_SNDBUF disables
        // Linux receive-window autotuning and caps throughput on high-latency links, so the
        // default must not pin a size.
        await Assert.That(RespireConnectionOptions.Default.SocketReceiveBufferSize).IsEqualTo(0);
        await Assert.That(RespireConnectionOptions.Default.SocketSendBufferSize).IsEqualTo(0);
    }
}
