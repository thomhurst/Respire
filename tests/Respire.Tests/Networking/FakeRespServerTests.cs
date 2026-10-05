using System.Net;
using System.Net.Sockets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

// These tests run alongside the rest of the suite. Other sockets in this process can claim a
// just-released ephemeral port, so a retired port is only a precondition: an attempt whose
// rebind loses that race is retried with a fresh retired port. Release is observed on the
// rejected listener itself instead of by rebinding its port, which another socket could take.
public class FakeRespServerTests
{
    private const int RetiredPortAttempts = 10;

    [Test]
    public async Task ListenerAllocationDoesNotReuseRetiredEndpoint()
    {
        // Model the OS immediately recycling a stopped fixture's ephemeral port.
        for (var attempt = 1; ; attempt++)
        {
            var retiredPort = await RetirePortAsync();
            var created = new List<TcpListener>();
            TcpListener listener;
            try
            {
                listener = FakeRespServer.StartListener(() =>
                {
                    var next = new TcpListener(IPAddress.Loopback, created.Count == 0 ? retiredPort : 0);
                    created.Add(next);
                    return next;
                });
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse
                && created.Count == 1 && attempt < RetiredPortAttempts)
            {
                // Another socket claimed the retired port before the rebind; nothing was tested.
                DisposeAll(created);
                continue;
            }

            try
            {
                await Assert.That(((IPEndPoint)listener.LocalEndpoint).Port).IsNotEqualTo(retiredPort);
                await Assert.That(created.Count).IsGreaterThanOrEqualTo(2);

                // Successful allocation must release every rejected listener as well.
                foreach (var rejected in created.Where(item => !ReferenceEquals(item, listener)))
                    await Assert.That(IsListening(rejected)).IsFalse();
                await Assert.That(IsListening(listener)).IsTrue();

                // The replacement still accepts a connection on its fresh endpoint.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var client = new TcpClient();
                var accepted = listener.AcceptSocketAsync(timeout.Token);
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
                using var connection = await accepted;
                await Assert.That(connection.Connected).IsTrue();
            }
            finally
            {
                listener.Stop();
                DisposeAll(created);
            }

            return;
        }
    }

    [Test]
    public async Task RejectedListenerIsReleasedWhenAllocationFails()
    {
        for (var attempt = 1; ; attempt++)
        {
            var retiredPort = await RetirePortAsync();
            var created = new List<TcpListener>();
            var failure = new IOException("Controlled listener allocation failure.");
            try
            {
                FakeRespServer.StartListener(() =>
                {
                    if (created.Count != 0) throw failure;
                    var rejected = new TcpListener(IPAddress.Loopback, retiredPort);
                    created.Add(rejected);
                    return rejected;
                });
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse
                && attempt < RetiredPortAttempts)
            {
                // Another socket claimed the retired port before the rebind; nothing was tested.
                DisposeAll(created);
                continue;
            }
            catch (IOException error) when (ReferenceEquals(error, failure))
            {
                // The second allocation failed as arranged; check the rejected listener below.
            }

            try
            {
                await Assert.That(created.Count).IsEqualTo(1);
                // The rejected listener must not retain its socket after the second allocation fails.
                await Assert.That(IsListening(created[0])).IsFalse();
            }
            finally
            {
                DisposeAll(created);
            }

            return;
        }
    }

    private static async Task<int> RetirePortAsync()
    {
        await using var previous = new FakeRespServer();
        var retiredPort = previous.Port;
        await previous.DisposeAsync();
        return retiredPort;
    }

    // Stop closes the listening socket; Server then hands out a fresh, unbound socket.
    private static bool IsListening(TcpListener listener) => listener.Server.IsBound;

    private static void DisposeAll(List<TcpListener> listeners)
    {
        foreach (var listener in listeners) listener.Dispose();
    }
}
