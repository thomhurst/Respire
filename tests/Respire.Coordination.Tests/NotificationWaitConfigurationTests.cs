using System.Reflection;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Coordination.Tests;

public class NotificationWaitConfigurationTests
{
    [Test]
    [MatrixDataSource]
    public async Task UnsupportedConfigurationIsRejectedBeforeSendingWaitCommands(
        [Matrix("fair", "fenced", "hash")] string operation,
        [Matrix(false, true)] bool prefixedView,
        [Matrix("none", "client", "client-and-cache")] string wrapper,
        [Matrix("noloop", "replica", "replica-preferred", "uncovered", "bypass")] string configuration)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                var value when value.StartsWith("CLIENT ", StringComparison.Ordinal) => FakeRespServer.OkReply,
                _ => "-ERR unexpected coordination command\r\n"u8.ToArray(),
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp3,
            Endpoints = [new("127.0.0.1", server.Port)],
            ReplicaEndpoints = [new("127.0.0.1", server.Port)],
            Connections = 1,
            ClientSideCache = new()
            {
                NoLoop = configuration == "noloop",
                KeyPrefixes = configuration == "uncovered" ? ["other:"] : [],
            },
        });
        var commandsBeforeWait = server.CommandsSeen;
        IRespireClient waitClient = prefixedView ? client.WithKeyPrefix("tenant:") : client;
        waitClient = configuration switch
        {
            "replica" => waitClient.WithReadFrom(RespireReadFrom.Replica),
            "replica-preferred" => waitClient.WithReadFrom(RespireReadFrom.ReplicaPreferred),
            "bypass" => waitClient.WithoutClientCache(),
            _ => waitClient,
        };
        waitClient = Wrap(waitClient, wrapper);

        await Assert.That(() => AcquireAsync(waitClient, operation)).ThrowsExactly<RespireConfigurationException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(commandsBeforeWait);
    }

    [Test]
    [MatrixDataSource]
    public async Task UnverifiableDecoratorsAreRejectedEvenWithSupportedInnerConfiguration(
        [Matrix("fair", "fenced", "hash")] string operation,
        [Matrix(false, true)] bool prefixedView,
        [Matrix("client", "client-and-cache")] string wrapper)
    {
        // A cache wrapper cannot attest to the outer client's routing or tracked-read behavior.
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespireProtocol.Resp3,
            Endpoints = [new("unused.invalid", 6379)],
            ClientSideCache = new(),
        });
        var waitClient = Wrap(prefixedView ? client.WithKeyPrefix("tenant:") : client, wrapper);
        var error = await Assert.That(() => AcquireAsync(waitClient, operation))
            .ThrowsExactly<RespireConfigurationException>();
        await Assert.That(error!.Message).Contains("cannot be verified");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FairLockRequiresQueueCoverageAsWellAsLeaseCoverage(bool prefixedView)
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespireProtocol.Resp3,
            Endpoints = [new("unused.invalid", 6379)],
            ClientSideCache = new() { KeyPrefixes = [prefixedView ? "tenant:lease" : "lease"] },
        });
        var error = await Assert.That(() => AcquireAsync(
            prefixedView ? client.WithKeyPrefix("tenant:") : client, "fair"))
            .ThrowsExactly<RespireConfigurationException>();
        await Assert.That(error!.Message).Contains("tracking coverage");
    }

    private static async Task AcquireAsync(IRespireClient client, string operation)
    {
        var coordination = new RespireCoordination(client);
        if (operation == "fair")
        {
            await using var lease = await coordination.CreateFairLock("lease", "counter", "queue")
                .AcquireAsync(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        }
        else if (operation == "hash")
        {
            await using var lease = await coordination.AcquireLeaseAsync(
                "lease", "worker", TimeSpan.FromMinutes(1));
        }
        else
        {
            await using var lease = await coordination.AcquireFencedLockAsync(
                "lease", "counter", TimeSpan.FromMinutes(1));
        }
    }

    private static IRespireClient Wrap(IRespireClient client, string wrapper)
    {
        if (wrapper == "none") return client;
        var proxy = DispatchProxy.Create<IRespireClient, ForwardingClient>();
        var forwarding = (ForwardingClient)proxy;
        forwarding.Inner = client;
        if (wrapper == "client-and-cache")
        {
            var cache = DispatchProxy.Create<IRespireClientSideCache, ForwardingCache>();
            ((ForwardingCache)cache).Inner = client.ClientSideCache!;
            forwarding.Cache = cache;
        }
        return proxy;
    }

    public class ForwardingClient : DispatchProxy, IRespireNameResolver
    {
        public IRespireClient Inner { get; set; } = null!;
        public IRespireClientSideCache? Cache { get; set; }

        public RespireKey ResolveKey(RespireKey key) => ((IRespireNameResolver)Inner).ResolveKey(key);

        public RespireChannel ResolveChannel(RespireChannel channel) => ((IRespireNameResolver)Inner).ResolveChannel(channel);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.Name == "get_ClientSideCache" && Cache is not null
                ? Cache : targetMethod.Invoke(Inner, args);
    }

    public class ForwardingCache : DispatchProxy
    {
        public IRespireClientSideCache Inner { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.Invoke(Inner, args);
    }
}
