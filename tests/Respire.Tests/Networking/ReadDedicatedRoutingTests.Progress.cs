using System.Reflection;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class ReadDedicatedRoutingTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NearestTerminatesWhenPoolAndSelectedEndpointDiffer(bool failRental)
    {
        await using var primary = Node("primary", false);
        var expected = new IOException("Injected dedicated alias failure.");
        var rentals = 0;
        var options = Options(primary, [], RespireReadFrom.Primary) with
        {
            Endpoints = [new("localhost", primary.Port)],
            TestingStreamFactory = OpenStreamAsync,
        };
        await using var client = await RespireClient.ConnectAsync(options);
        var core = client.Core;
        var original = core.DedicatedPool;
        // Model selection and acquisition observing different endpoint identities while
        // retaining one publication. Both aliases reach the same real fake-server socket.
        await using var pool = new DedicatedConnectionPool("127.0.0.1", primary.Port,
            options.ToConnectionOptions(), null)
        {
            MovingOwner = core.Multiplexer,
            MovingPublication = core.Multiplexer.MovingPublication,
        };
        var field = typeof(ClientCore).GetField("_dedicatedPool", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(core, pool);
        long now = 0;
        core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>(
            (_, _) => ValueTask.FromResult(1L), () => Interlocked.Add(ref now, 2_000));
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await Assert.That(pool.Endpoint).IsNotEqualTo(core.Multiplexer.ActiveConnectionEndpoint);
            if (failRental)
            {
                var error = await Assert.That(async () =>
                    await core.ReadRouter.RentDedicatedConnectionAsync(RespireReadFrom.Nearest, caller.Token))
                    .Throws<RespireConnectionException>();
                await Assert.That(error!.ToString()).Contains(expected.Message);
            }
            else
            {
                var lease = await core.ReadRouter.RentDedicatedConnectionAsync(RespireReadFrom.Nearest, caller.Token);
                lease.Pool.Return(lease.Connection);
                await Assert.That(lease.IsReplica).IsFalse();
            }
            await Assert.That(caller.IsCancellationRequested).IsFalse();
            await Assert.That(rentals).IsEqualTo(1);
        }
        finally { field.SetValue(core, original); }

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            if (host == "127.0.0.1")
            {
                Interlocked.Increment(ref rentals);
                if (failRental) throw expected;
            }
            return await OpenSocketAsync("127.0.0.1", port, token);
        }
    }
}
