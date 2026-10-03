using System.Net;
using System.Net.Sockets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class FakeRespServerTests
{
    [Test]
    [NotInParallel]
    public async Task ListenerAllocationDoesNotReuseRetiredEndpoint()
    {
        // Model the OS immediately recycling a stopped fixture's ephemeral port.
        // Serialize the explicit rebind so unrelated listeners cannot claim that port.
        await using var previous = new FakeRespServer();
        var retiredPort = previous.Port;
        await previous.DisposeAsync();
        var allocations = 0;
        using var listener = FakeRespServer.StartListener(() =>
            new TcpListener(IPAddress.Loopback, ++allocations == 1 ? retiredPort : 0));

        await Assert.That(((IPEndPoint)listener.LocalEndpoint).Port).IsNotEqualTo(retiredPort);
        await Assert.That(allocations).IsGreaterThanOrEqualTo(2);

        // The replacement still accepts a connection on its fresh endpoint.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        var accepted = listener.AcceptSocketAsync(timeout.Token);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
        using var connection = await accepted;
        await Assert.That(connection.Connected).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task RejectedListenerIsReleasedWhenAllocationFails()
    {
        await using var previous = new FakeRespServer();
        var retiredPort = previous.Port;
        await previous.DisposeAsync();
        var allocations = 0;
        var failure = new IOException("Controlled listener allocation failure.");

        await Assert.That(() => FakeRespServer.StartListener(() => ++allocations == 1
            ? new TcpListener(IPAddress.Loopback, retiredPort) : throw failure))
            .ThrowsExactly<IOException>();

        // The rejected listener must not retain its socket after the second allocation fails.
        using var probe = new TcpListener(IPAddress.Loopback, retiredPort);
        probe.Start();
        await Assert.That(((IPEndPoint)probe.LocalEndpoint).Port).IsEqualTo(retiredPort);
    }
}
