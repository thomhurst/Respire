using System.Net.Sockets;
using System.Text;
using TUnit.Assertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class GeneratedHashMutationIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test]
    [Arguments(RespProtocol.Resp2, RespireHashExpiryMode.HSetEx)]
    [Arguments(RespProtocol.Resp3, RespireHashExpiryMode.HSetEx)]
    [Arguments(RespProtocol.Resp2, RespireHashExpiryMode.HSetThenExpire)]
    [Arguments(RespProtocol.Resp3, RespireHashExpiryMode.HSetThenExpire)]
    public async Task RealRedisCommandsExpiryAndPartialUpdates(RespProtocol protocol, RespireHashExpiryMode mode)
        => await GeneratedHashMutationControls.RunAsync(fixture.Host, fixture.Port, protocol, mode);

    [Test]
    public async Task AttributeExpiryActuallyRemovesFieldAndNoOpDoesNotRecreateIt()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var key = "short:" + Guid.NewGuid().ToString("N");
        var value = new ShortLivedHashModel("id", "token");
        var tracker = ShortLivedHashModelHashMapper.Track(client, key);
        await tracker.UpdateAsync(value);
        var ttl = await client.Hashes.ExpiryAsync(key, "Token");
        await Assert.That(ttl[0].HasExpiry && ttl[0].TimeToLive!.Value.TotalMilliseconds <= 500).IsTrue();
        await Task.Delay(700);
        await Assert.That(await client.Hashes.GetStringAsync(key, "Token")).IsNull();
        await tracker.UpdateAsync(value);
        await Assert.That(await client.Hashes.GetStringAsync(key, "Token")).IsNull();
        await Assert.That(await client.Hashes.GetStringAsync(key, "Id")).IsEqualTo("id");
    }

    [Test]
    public async Task FailedRemovalKeepsRealRedisPartialWriteAvailableForRetry()
    {
        var username = "hash-tracker-" + Guid.NewGuid().ToString("N");
        var key = "retry:" + username;
        await using var admin = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(fixture.Host, fixture.Port)], AllowAdmin = true,
        });
        await admin.Server.AclSetUserAsync(username, ["on", "nopass", "~" + key, "+@all", "-hdel"]);
        try
        {
            var first = new StoredHashModel("id", "name", 0, "old", 1);
            await StoredHashModelHashMapper.SetAsync(admin, key, first);
            await using var restricted = await RespireClient.ConnectAsync(new RespireOptions
            {
                Endpoints = [new(fixture.Host, fixture.Port)], Username = username, Password = "",
            });
            await using var monitor = await HashMutationMonitor.ConnectAsync(fixture.Host, fixture.Port);
            var tracker = StoredHashModelHashMapper.Track(restricted, key, first);
            var next = first with { Count = 1, Note = null };
            await Assert.That(async () => await tracker.UpdateAsync(next)).Throws<RespireServerException>();
            await Assert.That(await admin.Hashes.GetStringAsync(key, "Count")).IsEqualTo("1");
            await Assert.That(await admin.Hashes.GetStringAsync(key, "Note")).IsEqualTo("old");
            await admin.Server.AclSetUserAsync(username, ["+hdel"]);
            await tracker.UpdateAsync(first);
            await Assert.That(await StoredHashModelHashMapper.GetAsync(admin, key)).IsEqualTo(first);
            await tracker.UpdateAsync(first);
            await tracker.UpdateAsync(next);
            await tracker.UpdateAsync(next);
            await Assert.That(await StoredHashModelHashMapper.GetAsync(admin, key)).IsEqualTo(next);
            var commands = await monitor.ReadMutationsAsync(admin, key);
            await Assert.That(commands.Count(command => command.StartsWith("\"HSET\"", StringComparison.Ordinal))).IsEqualTo(3);
        }
        finally
        {
            await admin.Server.AclDeleteUsersAsync([username]);
        }
    }
}

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<HashExpiryRedis74Container>(Shared = SharedType.PerTestSession)]
public class GeneratedHashMutationRedis74Tests(HashExpiryRedis74Container fixture)
{
    [Test]
    [Arguments(RespProtocol.Resp2)]
    [Arguments(RespProtocol.Resp3)]
    public async Task ExplicitFallbackWorksWithoutHSetEx(RespProtocol protocol)
        => await GeneratedHashMutationControls.RunAsync(fixture.Host, fixture.Port, protocol, RespireHashExpiryMode.HSetThenExpire);

    [Test]
    public async Task DefaultRefusesUnsupportedHSetExWithoutWriting()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var key = "refused:" + Guid.NewGuid().ToString("N");
        var value = new ExpiringHashModel("id", "name", "token", null);
        var tracker = ExpiringHashModelHashMapper.Track(client, key);
        await Assert.That(async () => await tracker.UpdateAsync(value)).Throws<NotSupportedException>();
        await Assert.That(await client.Keys.ExistsAsync(key)).IsFalse();
        // The failed attempt did not advance the baseline: retry remains a real write.
        await Assert.That(async () => await tracker.UpdateAsync(value)).Throws<NotSupportedException>();
    }
}

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<HashExpiryRedis70Container>(Shared = SharedType.PerTestSession)]
public class GeneratedHashMutationUnsupportedTests(HashExpiryRedis70Container fixture)
{
    [Test]
    [Arguments(RespireHashExpiryMode.HSetEx)]
    [Arguments(RespireHashExpiryMode.HSetThenExpire)]
    public async Task Redis7RefusesExpiryBeforeWriting(RespireHashExpiryMode mode)
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var key = "unsupported:" + Guid.NewGuid().ToString("N");
        await Assert.That(async () => await ExpiringHashModelHashMapper.SetWithExpiryAsync(client, key, mode,
            new ExpiringHashModel("id", "name", "token", null))).Throws<NotSupportedException>();
        await Assert.That(await client.Keys.ExistsAsync(key)).IsFalse();
    }
}

public sealed class HashExpiryRedis74Container() : StandaloneRedisTestContainer("redis:7.4-alpine");
public sealed class HashExpiryRedis70Container() : StandaloneRedisTestContainer("redis:7.0.15");

[RespireHash("short:{Id}")]
public partial record ShortLivedHashModel(string Id, [property: RespireFieldTtl(500)] string? Token);

internal static class GeneratedHashMutationControls
{
    internal static async Task RunAsync(string host, int port, RespProtocol protocol, RespireHashExpiryMode mode)
    {
        await using var root = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(host, port)], Protocol = protocol, Connections = 1,
        });
        var client = root.WithKeyPrefix("prefix:");
        var key = "mutation:" + Guid.NewGuid().ToString("N");
        await using var monitor = await HashMutationMonitor.ConnectAsync(host, port);
        var value = new ExpiringHashModel("id", "name", "token", 0);
        var tracker = ExpiringHashModelHashMapper.Track(client, key, expiryMode: mode);
        await tracker.UpdateAsync(value);
        var expiries = await client.Hashes.ExpiryTimeAsync(key, "Token", "Counter");
        await Assert.That((await client.Hashes.ExpiryAsync(key, "Token", "Counter")).All(ttl => ttl.HasExpiry)).IsTrue();
        await tracker.UpdateAsync(value);
        await tracker.UpdateAsync(value with { Name = "new" });
        var after = await client.Hashes.ExpiryTimeAsync(key, "Token", "Counter");
        await Assert.That(after.Select(expiry => expiry.UnixTimeMilliseconds).SequenceEqual(expiries.Select(expiry => expiry.UnixTimeMilliseconds))).IsTrue();
        await tracker.UpdateAsync(value with { Name = "new", Token = null });
        await tracker.UpdateAsync(value with { Name = "new", Token = null });
        await Assert.That(await ExpiringHashModelHashMapper.GetAsync(client, key)).IsEqualTo(value with { Name = "new", Token = null });
        var commands = await monitor.ReadMutationsAsync(root, "prefix:" + key);
        var quotedKey = "\"prefix:" + key + "\"";
        string[] expected = mode == RespireHashExpiryMode.HSetEx
            ? [
                $"\"HSETEX\" {quotedKey} \"PX\" \"10000\" \"FIELDS\" \"1\" \"Token\" \"token\"",
                $"\"HSETEX\" {quotedKey} \"PX\" \"20000\" \"FIELDS\" \"1\" \"Counter\" \"0\"",
                $"\"HSET\" {quotedKey} \"Id\" \"id\" \"Name\" \"name\"",
                $"\"HSET\" {quotedKey} \"Name\" \"new\"", $"\"HDEL\" {quotedKey} \"Token\"",
            ]
            : [
                $"\"HSET\" {quotedKey} \"Token\" \"token\"",
                $"\"HPEXPIRE\" {quotedKey} \"10000\" \"FIELDS\" \"1\" \"Token\"",
                $"\"HSET\" {quotedKey} \"Counter\" \"0\"",
                $"\"HPEXPIRE\" {quotedKey} \"20000\" \"FIELDS\" \"1\" \"Counter\"",
                $"\"HSET\" {quotedKey} \"Id\" \"id\" \"Name\" \"name\"",
                $"\"HSET\" {quotedKey} \"Name\" \"new\"", $"\"HDEL\" {quotedKey} \"Token\"",
            ];
        await Assert.That(commands.SequenceEqual(expected)).IsTrue();
    }
}

internal sealed class HashMutationMonitor(TcpClient socket, StreamReader reader) : IAsyncDisposable
{
    internal static async Task<HashMutationMonitor> ConnectAsync(string host, int port)
    {
        var socket = new TcpClient();
        try
        {
            await socket.ConnectAsync(host, port);
            var reader = new StreamReader(socket.GetStream(), Encoding.UTF8, leaveOpen: true);
            await socket.GetStream().WriteAsync("*1\r\n$7\r\nMONITOR\r\n"u8.ToArray());
            if (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) != "+OK")
                throw new InvalidOperationException("Redis MONITOR was not accepted.");
            return new HashMutationMonitor(socket, reader);
        }
        catch { socket.Dispose(); throw; }
    }

    internal async Task<string[]> ReadMutationsAsync(IRespireClient client, string key)
    {
        var marker = "monitor-end-" + Guid.NewGuid().ToString("N");
        using var reply = await client.ExecuteAsync(RespireCommands.Connection.ECHO, [marker]);
        var commands = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.Contains(marker, StringComparison.Ordinal)) return commands.ToArray();
            var start = line.IndexOf('"');
            if (start < 0 || !line.Contains("\"" + key + "\"", StringComparison.Ordinal)) continue;
            var command = line[start..];
            if (command.StartsWith("\"HSET\"", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("\"HSETEX\"", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("\"HDEL\"", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("\"HPEXPIRE\"", StringComparison.OrdinalIgnoreCase)) commands.Add(command);
        }
        throw new InvalidOperationException("Redis MONITOR ended before its barrier.");
    }

    public ValueTask DisposeAsync()
    {
        reader.Dispose();
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
