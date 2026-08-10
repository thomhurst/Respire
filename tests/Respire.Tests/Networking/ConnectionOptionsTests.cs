using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ConnectionOptionsTests
{
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
