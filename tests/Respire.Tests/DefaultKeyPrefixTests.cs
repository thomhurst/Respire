using System.Text;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class DefaultKeyPrefixTests
{
    /// <summary>Verifies both connection-string forms decode once and preserve undecodable escapes as literal text.</summary>
    [Test]
    [Arguments("redis://localhost?keyPrefix=tenant%3A", "tenant:")]
    [Arguments("localhost,keyPrefix=tenant%3A", "tenant:")]
    [Arguments("redis://localhost?KEYPREFIX=%20%2C%3D%26%2B%25%E2%98%83%00", " ,=&+%\u2603\0")]
    [Arguments("localhost,KEYPREFIX=%20%2C%3D%26%2B%25%E2%98%83%00", " ,=&+%\u2603\0")]
    [Arguments("redis://localhost?keyPrefix=%252C", "%2C")]
    [Arguments("localhost,keyPrefix=%252C", "%2C")]
    [Arguments("redis://localhost?keyPrefix=%FF", "%FF")]
    [Arguments("localhost,keyPrefix=%FF", "%FF")]
    [Arguments("redis://localhost?keyPrefix=%E2%28%A1", "%E2(%A1")]
    [Arguments("localhost,keyPrefix=%E2%28%A1", "%E2(%A1")]
    [Arguments("redis://localhost?keyPrefix=%GG", "%GG")]
    [Arguments("localhost,keyPrefix=%GG", "%GG")]
    [Arguments("redis://localhost?keyPrefix=first&keyPrefix=last", "last")]
    [Arguments("localhost,keyPrefix=first,keyPrefix=last", "last")]
    [Arguments("redis://localhost?keyPrefix=first&keyPrefix=", "")]
    [Arguments("localhost,keyPrefix=first,keyPrefix=", "")]
    [Arguments("redis://localhost?keyPrefix", "")]
    [Arguments("localhost,keyPrefix=", "")]
    [Arguments("redis://localhost?keyPrefix=a+b", "a+b")]
    [Arguments("localhost,keyPrefix= a+b ", "a+b")]
    public async Task ConnectionStringsApplyDecodedTextPrefix(string connectionString, string expected)
    {
        await using var client = RespireClient.Create(RespireOptions.Parse(connectionString));
        await Assert.That(client.ResolveKey("key").ToBytes().SequenceEqual(Encoding.UTF8.GetBytes(expected + "key"))).IsTrue();
    }

    /// <summary>Verifies a recognized prefix option does not suppress unknown-option validation.</summary>
    [Test]
    [Arguments("redis://localhost?keyPrefix=tenant&unknownPrefix=x")]
    [Arguments("localhost,keyPrefix=tenant,unknownPrefix=x")]
    public async Task PrefixDoesNotPermitUnknownOptions(string connectionString)
    {
        var error = Assert.Throws<ArgumentException>(() => RespireOptions.Parse(connectionString));
        await Assert.That(error.ParamName).IsEqualTo("connectionString");
        await Assert.That(error.Message).Contains("unknownPrefix");
    }

    /// <summary>Verifies eager and lazy roots snapshot binary storage and retain disposal ownership across derived views.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RootCreationSnapshotsBinaryPrefixAndOwnsDisposal(bool eager)
    {
        await using var server = new FakeRespServer(BinaryPrefixTests.Bulk("value"u8.ToArray()));
        byte[] bytes = [255, 0, (byte)':'];
        var options = Options(server.Port) with { KeyPrefix = bytes };
        var snapshot = options.ValidateAndSnapshot();
        await using var client = eager ? await RespireClient.ConnectAsync(options) : RespireClient.Create(options);
        bytes[0] = 254;
        await Assert.That(snapshot.KeyPrefix.ToBytes().SequenceEqual(new byte[] { 255, 0, (byte)':' })).IsTrue();
        await Assert.That(options.KeyPrefix.ToBytes()[0]).IsEqualTo((byte)254);
        var view = (RespireClient)client.WithKeyPrefix("nested:").WithKeyPrefix((RespireKey)new byte[] { 253 });
        await Assert.That(await view.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(server.ReceivedArguments[0][1].SequenceEqual(new byte[] { 255, 0, (byte)':' }
            .Concat("nested:"u8.ToArray()).Concat(new byte[] { 253 }).Concat("key"u8.ToArray()))).IsTrue();
        await view.DisposeAsync();
        await Assert.That(client.Core.Disposed).IsFalse();
        await client.DisposeAsync();
        await Assert.That(client.Core.Disposed).IsTrue();
        await Assert.That(async () => await view.GetStringAsync("key")).Throws<ObjectDisposedException>();
    }

    /// <summary>Verifies every empty prefix representation leaves root keys unchanged while allowing nested namespaces.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task EmptyDefaultsKeepRootUnprefixed(int representation)
    {
        RespireKey prefix = representation switch
        {
            1 => "",
            2 => Array.Empty<byte>(),
            3 => ReadOnlyMemory<byte>.Empty,
            _ => default,
        };
        await using var client = RespireClient.Create(Options(1) with { KeyPrefix = prefix });
        await Assert.That(client.KeyPrefix).IsNull();
        await Assert.That(client.ResolveKey("key")).IsEqualTo((RespireKey)"key");
        await Assert.That(client.WithKeyPrefix("tenant:").ResolveKey("key")).IsEqualTo((RespireKey)"tenant:key");
    }

    /// <summary>Verifies text prefixes combine surrogate halves and survive routing and cache-policy views.</summary>
    [Test]
    public async Task RootTextPrefixKeepsSurrogateCompositionAndDerivedPolicies()
    {
        await using var root = RespireClient.Create(Options(1) with
        {
            KeyPrefix = "\uD800", ReplicaEndpoints = [new("127.0.0.1", 2)], ClientSideCache = new(),
        });
        var view = (RespireClient)root.WithKeyPrefix("\uDC00:").WithReadFrom(RespireReadFrom.Replica).WithoutClientCache();
        await Assert.That(view.ResolveKey("key").ToBytes().SequenceEqual(Encoding.UTF8.GetBytes("\uD800\uDC00:key"))).IsTrue();
        await Assert.That(view.GetBatchReadFromPolicy()).IsEqualTo(RespireReadFrom.Replica);
        await Assert.That(view.ReadCache).IsNull();
        await Assert.That(view.PrimaryReadView.KeyPrefixBytes.SequenceEqual(view.KeyPrefixBytes)).IsTrue();
        await Assert.That(view.ForDeferredBatch().KeyPrefixBytes.SequenceEqual(view.KeyPrefixBytes)).IsTrue();
        await view.DisposeAsync();
        await Assert.That(root.Core.Disposed).IsFalse();
    }

    /// <summary>Verifies each connection candidate retains its configured namespace.</summary>
    [Test]
    public async Task IndependentCandidatesRetainTheirOwnPrefix()
    {
        await using var rejected = new FakeRespServer("-WRONGPASS rejected\r\n"u8.ToArray());
        await using var accepted = new FakeRespServer(FakeRespServer.OkReply, BinaryPrefixTests.Bulk("value"u8.ToArray()));
        var first = Options(rejected.Port) with { Password = "secret", KeyPrefix = "wrong:" };
        var second = Options(accepted.Port) with { Password = "secret", KeyPrefix = "chosen:" };
        await using var client = await RespireClient.ConnectAnyAsync([first, second]);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(accepted.ReceivedArguments.Single(args => args[0].AsSpan().SequenceEqual("GET"u8))[1]
            .AsSpan().SequenceEqual("chosen:key"u8)).IsTrue();
        await Assert.That(rejected.ReceivedCommands.All(command => command.StartsWith("AUTH ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(rejected.ReceivedCommands.Count).IsEqualTo(1);
    }

    /// <summary>Verifies Cluster routing uses the physical prefixed key when its slot differs from the logical key.</summary>
    [Test]
    public async Task ConfiguredPrefixSelectsPhysicalClusterSlot()
    {
        await using var lower = new FakeRespServer(BinaryPrefixTests.Bulk("value"u8.ToArray()));
        await using var upper = new FakeRespServer(BinaryPrefixTests.Bulk("wrong"u8.ToArray()));
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{lower.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{upper.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        byte[] prefix = [255, 0, .. "{b}:"u8];
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port) with
        {
            UseCluster = true, KeyPrefix = prefix,
        });
        await Assert.That(((RespireKey)"{a}:key").ClusterSlot >= 8192).IsTrue();
        await Assert.That(client.ResolveKey("{a}:key").ClusterSlot < 8192).IsTrue();
        await Assert.That(await client.GetStringAsync("{a}:key")).IsEqualTo("value");
        await Assert.That(lower.ReceivedArguments.Single(args => args[0].AsSpan().SequenceEqual("GET"u8))[1]
            .SequenceEqual(prefix.Concat("{a}:key"u8.ToArray()))).IsTrue();
        await Assert.That(upper.ReceivedCommands.Any(command => command.StartsWith("GET ", StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>Verifies batches and watched transactions send physical keys with the configured root prefix.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeferredAndWatchedCommandsUseRootPrefix(bool watched)
    {
        byte[][] replies = watched
            ? [FakeRespServer.OkReply, FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n$5\r\nvalue\r\n"u8.ToArray()]
            : [BinaryPrefixTests.Bulk("value"u8.ToArray())];
        await using var server = new FakeRespServer(2, replies);
        byte[] prefix = [255, 0, (byte)':'];
        await using var client = await RespireClient.ConnectAsync(Options(server.Port) with { KeyPrefix = prefix });
        if (watched)
        {
            await using var transaction = await client.CreateTransactionAsync(["key"]);
            var pending = transaction.GetString("key");
            await Assert.That(await transaction.CommitAsync()).IsTrue();
            await Assert.That(pending.Result).IsEqualTo("value");
            await Assert.That(server.ReceivedArguments.Single(args => args[0].AsSpan().SequenceEqual("WATCH"u8))[1]
                .SequenceEqual(prefix.Concat("key"u8.ToArray()))).IsTrue();
        }
        else
        {
            using var batch = client.CreateBatch();
            var pending = batch.GetString("key");
            await batch.ExecuteAsync();
            await Assert.That(pending.Result).IsEqualTo("value");
        }
        await Assert.That(server.ReceivedArguments.Single(args => args[0].AsSpan().SequenceEqual("GET"u8))[1]
            .SequenceEqual(prefix.Concat("key"u8.ToArray()))).IsTrue();
    }

    /// <summary>Verifies scans escape prefix bytes in patterns and return only logical keys inside the namespace.</summary>
    [Test]
    public async Task RootScansEscapeAndStripPhysicalNamespace()
    {
        byte[] prefix = [255, (byte)'*', 0];
        byte[] response = [.. "*2\r\n$1\r\n0\r\n*2\r\n"u8,
            .. BinaryPrefixTests.Bulk([.. prefix, .. "visible"u8]),
            .. BinaryPrefixTests.Bulk([254, (byte)'*', 0, .. "secret"u8])];
        await using var server = new FakeRespServer(response);
        await using var client = await RespireClient.ConnectAsync(Options(server.Port) with { KeyPrefix = prefix });
        var keys = new List<string>();
        await foreach (var key in client.Keys.ScanAsync()) keys.Add(key);
        await Assert.That(keys).IsEquivalentTo(["visible"]);
        await Assert.That(server.ReceivedArguments[0][3].SequenceEqual(new byte[] { 255, (byte)'\\', (byte)'*', 0, (byte)'*' })).IsTrue();
    }

    /// <summary>Verifies cache invalidation uses physical keys while ordinary pub/sub channels remain literal.</summary>
    [Test]
    public async Task RootCacheUsesPhysicalKeysAndPubSubKeepsLiteralChannel()
    {
        await using var server = new FakeRespServer(
            "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(), FakeRespServer.OkReply,
            FakeRespServer.OkReply, BinaryPrefixTests.Bulk("old"u8.ToArray()), FakeRespServer.OkReply,
            BinaryPrefixTests.Bulk("new"u8.ToArray()), ":1\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server.Port) with
        {
            KeyPrefix = new byte[] { 255, 0 }, ClientSideCache = new(),
        });
        var physical = client.ResolveKey("key");
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = client.ClientSideCache!.SubscribeInvalidations(physical, _ => invalidated.TrySetResult());
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("old");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("old");
        await server.SendRawAsync([.. ">2\r\n+invalidate\r\n*1\r\n"u8, .. BinaryPrefixTests.Bulk(physical.ToBytes())]);
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
        await client.PublishAsync("literal-channel", "payload"u8.ToArray());
        await Assert.That(server.ReceivedArguments.Count(args => args[0].AsSpan().SequenceEqual("GET"u8)
            && args[1].SequenceEqual(physical.ToBytes()))).IsEqualTo(2);
        await Assert.That(server.ReceivedArguments.Single(args => args[0].AsSpan().SequenceEqual("PUBLISH"u8))[1]
            .AsSpan().SequenceEqual("literal-channel"u8)).IsTrue();
    }

    /// <summary>Creates deterministic fixture options without automatic maintenance negotiation.</summary>
    private static RespireOptions Options(int port) => new()
    {
        Endpoints = [new("127.0.0.1", port)], Protocol = RespProtocol.Resp2, Connections = 1,
    };
}
