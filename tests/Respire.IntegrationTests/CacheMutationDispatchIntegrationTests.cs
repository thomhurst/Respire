using FluentAssertions;
using System.Buffers;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
[Category(TestCategories.ProtocolIndependent)]
public class CacheMutationDispatchIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments("ordinary", 32)]
    [Arguments("ordinary", 512 * 1024)]
    [Arguments("converted", 32)]
    [Arguments("discard", 32)]
    [Arguments("blocking", 32)]
    [Arguments("pinned", 32)]
    [Arguments("ordinary", 32, true)]
    [Arguments("ordinary", 512 * 1024, true)]
    [Arguments("converted", 32, true)]
    [Arguments("discard", 32, true)]
    [Arguments("blocking", 32, true)]
    [Arguments("pinned", 32, true)]
    [Arguments("streaming", 32, true)]
    [Arguments("transaction", 32, true)]
    public async Task RejectsUnadmittedNativeWrites(string path, int payloadLength, bool dedicated = false)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        { Protocol = RespProtocol.Resp3, ClientSideCache = new() });
        RespireKey key = "dispatch-contract:" + Guid.NewGuid().ToString("N");
        DedicatedConnectionPool? pool = null;
        RespireConnection? borrowed = null;
        try
        {
            await client.SetAsync(key, "original");
            (await client.GetStringAsync(key)).Should().Be("original");
            if (dedicated)
            {
                pool = await client.Core.GetDedicatedPoolAsync(CancellationToken.None);
                (pool, borrowed) = await client.Core.RentDedicatedConnectionAsync(pool, CancellationToken.None);
            }
            var connection = borrowed ?? client.Core.Multiplexer.GetConnection();
            var command = new Cmd2(Verbs.Set, key.AsValue(), new byte[payloadLength]);
            // Models a new higher-level path that dispatches directly instead of obtaining
            // the logical client's mutation admission. It must fail before writing any bytes.
            async Task BypassLogicalAdmissionAsync()
            {
                switch (path)
                {
                    case "streaming":
                        var streamed = new StreamedSetCommand(key.AsValue(),
                            new ReadOnlySequence<byte>(new byte[payloadLength]), default, SetWhen.Always);
                        using (await connection.SendCheckedAsync(streamed, commandName: "SET")) { }
                        break;
                    case "transaction":
                        var buffer = new WriteBuffer(128);
                        try
                        {
                            var writer = new RespWriter(buffer);
                            command.Write(ref writer);
                            writer.Complete();
                            using (await connection.SendTransactionAsync(buffer.WrittenMemory, 1)) { }
                        }
                        finally { buffer.Release(); }
                        break;
                    case "converted":
                        await connection.SendConvertedAsync(command, 0,
                            static (int _, in RespValue reply) => reply.AsString(), transferOwnership: false, commandName: "SET");
                        break;
                    case "discard":
                        await connection.SendFireAndForgetAsync(command, commandName: "SET");
                        break;
                    case "blocking":
                        using (await connection.SendWithoutResponseTimeoutAsync(command)) { }
                        break;
                    case "pinned":
                        using (await (await connection.EnqueuePinnedAsync(command, CancellationToken.None, "SET"))) { }
                        break;
                    default:
                        using (await connection.SendAsync(command, commandName: "SET")) { }
                        break;
                }
            }
            await ((Func<Task>)BypassLogicalAdmissionAsync).Should().ThrowAsync<InvalidOperationException>();
            (await client.WithoutClientCache().GetStringAsync(key)).Should().Be("original");
            await client.SetAsync(key, "admitted");
            (await client.GetStringAsync(key)).Should().Be("admitted");
        }
        finally
        {
            if (borrowed is not null) pool!.Return(borrowed);
            await client.Keys.DeleteAsync(key);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DiagnosticNamesCannotAuthorizeUnknownDispatch(bool preEncoded)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        { Protocol = RespProtocol.Resp3, ClientSideCache = new() });
        RespireKey key = "dispatch-name:" + Guid.NewGuid().ToString("N");
        try
        {
            await client.SetAsync(key, "original");
            var connection = client.Core.Multiplexer.GetConnection();
            async Task WriteAsync()
            {
                if (!preEncoded)
                {
                    var command = new DynamicCommand(["SET", key.AsValue(), "unsafe"], routingKeyIndex: 1);
                    using (await connection.SendAsync(command, commandName: "GET")) { }
                    return;
                }
                var buffer = new WriteBuffer(128);
                try
                {
                    var writer = new RespWriter(buffer);
                    new Cmd2(Verbs.Set, key.AsValue(), "unsafe").Write(ref writer);
                    writer.Complete();
                    using (await connection.SendAsync(new RawCommand(buffer.WrittenMemory.ToArray()), commandName: "GET")) { }
                }
                finally { buffer.Release(); }
            }
            await ((Func<Task>)WriteAsync).Should().ThrowAsync<InvalidOperationException>();
            (await client.WithoutClientCache().GetStringAsync(key)).Should().Be("original");
        }
        finally { await client.Keys.DeleteAsync(key); }
    }

    [Test]
    [Arguments("ordinary")]
    [Arguments("validated")]
    [Arguments("first-prefix")]
    [Arguments("second-prefix")]
    [Arguments("bulk-stream")]
    [Arguments("fenced-body")]
    public async Task MutatingPreludesCannotBorrowTheirFinalCommandsAdmission(string path)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        { Protocol = RespProtocol.Resp3, ClientSideCache = new() });
        var suffix = Guid.NewGuid().ToString("N");
        RespireKey prefixKey = "dispatch-prelude:" + suffix, bodyKey = "dispatch-body:" + suffix;
        var cache = client.Core.ClientCache!;
        var body = new Cmd2(Verbs.Set, bodyKey.AsValue(), "body-write");
        var fence = cache.BeforeCommand("SET", in body);
        try
        {
            await client.SetAsync(prefixKey, "original");
            await client.SetAsync(bodyKey, "original");
            var connection = client.Core.Multiplexer.GetConnection();
            var prefix = new Cmd2(Verbs.Set, prefixKey.AsValue(), "unsafe");
            var read = new Cmd1(Verbs.Get, bodyKey.AsValue());
            var ping = new RawCommand(RespCommands.Ping);
            async Task WriteAsync()
            {
                switch (path)
                {
                    case "bulk-stream":
                        await using (await connection.SendPrefixedBulkStreamAsync(prefix, read)) { }
                        break;
                    case "validated":
                        using (await connection.SendValidatedPrefixedAsync(prefix, read)) { }
                        break;
                    case "first-prefix":
                        using (await connection.SendValidatedPrefixedAsync(prefix, ping, read)) { }
                        break;
                    case "second-prefix":
                        using (await connection.SendValidatedPrefixedAsync(ping, prefix, read)) { }
                        break;
                    case "fenced-body":
                        using (await connection.SendPrefixedCheckedAsync(prefix, new MutationCommand<Cmd2>(body, fence))) { }
                        break;
                    default:
                        using (await connection.SendPrefixedCheckedAsync(prefix, read)) { }
                        break;
                }
            }
            await ((Func<Task>)WriteAsync).Should().ThrowAsync<InvalidOperationException>();
            (await client.WithoutClientCache().GetStringAsync(prefixKey)).Should().Be("original");
            (await client.WithoutClientCache().GetStringAsync(bodyKey)).Should().Be("original");
        }
        finally
        {
            cache.CompleteMutation(in fence);
            await client.Keys.DeleteAsync([prefixKey, bodyKey]);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectsForeignOrRetiredMutationAdmissions(bool foreign)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        { Protocol = RespProtocol.Resp3, ClientSideCache = new() });
        RespireKey key = "dispatch-owner:" + Guid.NewGuid().ToString("N");
        var owner = foreign ? new ClientSideCacheCoordinator(new()) : client.Core.ClientCache!;
        var fence = owner.BeginUnknownMutation();
        if (!foreign) owner.CompleteMutation(in fence, succeeded: true);
        try
        {
            await client.SetAsync(key, "original");
            var command = new MutationCommand<Cmd2>(new Cmd2(Verbs.Set, key.AsValue(), "unsafe"), fence);
            Func<Task> write = async () =>
            {
                using var reply = await client.Core.Multiplexer.GetConnection().SendAsync(command, commandName: "SET");
            };
            await write.Should().ThrowAsync<InvalidOperationException>();
            (await client.WithoutClientCache().GetStringAsync(key)).Should().Be("original");
        }
        finally
        {
            owner.CompleteMutation(in fence);
            await client.Keys.DeleteAsync(key);
        }
    }

    [Test]
    public async Task ExplicitReadOnlyDispatchRetainsTheResidentCacheEntry()
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        { Protocol = RespProtocol.Resp3, ClientSideCache = new() });
        RespireKey key = "dispatch-read:" + Guid.NewGuid().ToString("N");
        try
        {
            await client.SetAsync(key, "original");
            (await client.GetStringAsync(key)).Should().Be("original");
            var command = new DynamicCommand(["GET", key.AsValue()], routingKeyIndex: 1,
                cacheMutation: RespireCacheMutation.ReadOnly, hasExplicitCacheMutation: true);
            using var reply = await client.Core.Multiplexer.GetConnection().SendAsync(command, commandName: "diagnostic");
            reply.AsString().Should().Be("original");
            client.ClientSideCache!.Count.Should().Be(1);
            var hits = client.ClientSideCache.GetStatistics().Hits;
            (await client.GetStringAsync(key)).Should().Be("original");
            client.ClientSideCache.GetStatistics().Hits.Should().Be(hits + 1);
        }
        finally { await client.Keys.DeleteAsync(key); }
    }

    [Test]
    [Arguments(RespireClientTrackingMode.OptIn)]
    [Arguments(RespireClientTrackingMode.Broadcast)]
    public async Task ReadOnlyScriptsAndConnectionProtocolRetainTheirDispatchContracts(RespireClientTrackingMode mode)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var prefix = "dispatch-protocol:" + suffix + ":";
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        { Protocol = RespProtocol.Resp3, ClientSideCache = new() { TrackingMode = mode, KeyPrefixes = [prefix] } });
        RespireKey key = prefix + "key";
        try
        {
            await client.SetAsync(key, "original");
            (await client.GetStringAsync(key)).Should().Be("original");
            foreach (var readOnly in new[] { true, false })
            {
                var script = RespireScript.Create("return redis.call('GET', KEYS[1]) -- " + suffix + readOnly,
                    readOnly, cacheReadOnly: true);
                // Exercise both the NOSCRIPT fallback and the subsequent EVALSHA dispatch.
                for (var execution = 0; execution < 2; execution++)
                {
                    using var reply = await client.Scripts.ExecuteAsync(script, [key]);
                    reply.AsString().Should().Be("original");
                    client.ClientSideCache!.Count.Should().Be(1);
                    var hits = client.ClientSideCache.GetStatistics().Hits;
                    (await client.GetStringAsync(key)).Should().Be("original");
                    client.ClientSideCache.GetStatistics().Hits.Should().Be(hits + 1);
                }
            }

            await using (var transaction = await client.CreateTransactionAsync(new RespireKey[] { key }))
            {
                var pending = transaction.Set(key, "committed");
                (await transaction.CommitAsync()).Should().BeTrue();
                pending.Result.Should().BeTrue();
            }
            (await client.GetStringAsync(key)).Should().Be("committed");

            RespireChannel channel = prefix + "channel";
            await using var subscription = await client.SubscribeAsync(channel);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var messages = subscription.GetAsyncEnumerator(deadline.Token);
            var next = messages.MoveNextAsync();
            (await client.PublishAsync(channel, "message")).Should().Be(1);
            (await next).Should().BeTrue();
            messages.Current.Text.Should().Be("message");
        }
        finally { await client.Keys.DeleteAsync(key); }
    }
}
