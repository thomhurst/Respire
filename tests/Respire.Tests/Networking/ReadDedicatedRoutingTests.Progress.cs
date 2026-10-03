using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class ReadDedicatedRoutingTests
{
    [Test]
    public async Task NearestRetainsSelectionFailureAfterAcquisitionFailure()
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        var original = new IOException("Original dedicated acquisition failure.");
        var newer = new IOException("Newer replica selection failure.");
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], RespireReadFrom.Primary) with
        {
            TestingStreamFactory = (host, port, token) => port == replica.Port
                ? ValueTask.FromException<Stream>(newer) : OpenSocketAsync(host, port, token),
        });
        var attempt = new ReadAttempt();
        await Assert.That(attempt.TryAdd(client.Core.Multiplexer.ActiveConnectionEndpoint, original)).IsTrue();
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.That(async () =>
            await client.Core.ReadRouter.SelectAsync(RespireReadFrom.Nearest, caller.Token, attempt))
            .Throws<RespireConnectionException>();
        var causes = error!.InnerException as AggregateException;
        await Assert.That(causes).IsNotNull();
        await Assert.That(causes!.InnerExceptions[0]).IsSameReferenceAs(original);
        await Assert.That(causes.InnerExceptions[1].ToString()).Contains(newer.Message);
        await Assert.That(caller.IsCancellationRequested).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task NearestTerminatesWhenPoolAndSelectedEndpointDiffer(bool failRental, bool hasReplica)
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        var expected = new IOException("Injected dedicated alias failure.");
        var rentals = 0;
        var options = Options(primary, hasReplica ? [replica] : [], RespireReadFrom.Primary) with
        {
            Endpoints = [new("localhost", primary.Port)],
            TestingStreamFactory = OpenStreamAsync,
        };
        await using var client = await RespireClient.ConnectAsync(options);
        var core = client.Core;
        var original = core.TestingDedicatedPoolOverride;
        // Model selection and acquisition observing different endpoint identities while
        // retaining one publication. Both aliases reach the same real fake-server socket.
        await using var pool = new DedicatedConnectionPool("127.0.0.1", primary.Port,
            options.ToConnectionOptions(), null)
        {
            MovingOwner = core.Multiplexer,
            MovingPublication = core.Multiplexer.MovingPublication,
        };
        long now = 0;
        core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>(
            (connection, _) => ValueTask.FromResult(connection.Port == primary.Port ? 1L : 10L), () => Interlocked.Add(ref now, 2_000));
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            core.TestingDedicatedPoolOverride = pool;
            await Assert.That(pool.Endpoint).IsNotEqualTo(core.Multiplexer.ActiveConnectionEndpoint);
            if (failRental && !hasReplica)
            {
                var error = await Assert.That(async () =>
                    await core.ReadRouter.RentDedicatedConnectionAsync(RespireReadFrom.Nearest, caller.Token))
                    .ThrowsExactly<IOException>();
                await Assert.That(error).IsSameReferenceAs(expected);
            }
            else
            {
                var lease = await core.ReadRouter.RentDedicatedConnectionAsync(RespireReadFrom.Nearest, caller.Token);
                lease.Pool.Return(lease.Connection);
                await Assert.That(lease.IsReplica).IsEqualTo(failRental && hasReplica);
                await Assert.That(lease.Connection.Port).IsEqualTo(failRental && hasReplica ? replica.Port : primary.Port);
                if (!failRental) await Assert.That(lease.Pool).IsSameReferenceAs(pool);
            }
            await Assert.That(caller.IsCancellationRequested).IsFalse();
            await Assert.That(rentals).IsEqualTo(1);
        }
        finally { core.TestingDedicatedPoolOverride = original; }

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            if (host == "127.0.0.1" && port == primary.Port)
            {
                Interlocked.Increment(ref rentals);
                if (failRental) throw expected;
            }
            return await OpenSocketAsync("127.0.0.1", port, token);
        }
    }
}
