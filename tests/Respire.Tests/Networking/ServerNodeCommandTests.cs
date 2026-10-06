using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ServerNodeCommandTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DestructiveCommandsUseOnlyExplicitEndpointAndExactGrammar(int protocol)
    {
        await using var seed = Server(1);
        await using var target = Server(30);
        await using var client = await Connect(seed.Port, protocol, admin: true);
        var node = client.WithKeyPrefix("ignored:").Server.OnNode(new("127.0.0.1", target.Port));
        await node.AclLoadAsync();
        await node.AclSaveAsync();
        await node.FailoverAsync();
        await node.FailoverAsync(new() { Target = new("replica", 6380), Timeout = TimeSpan.FromTicks(1), Force = true });
        await node.AbortFailoverAsync();
        await node.ReplicaOfAsync(new("primary", 6381));
        await node.PromoteToPrimaryAsync();
        await node.SwapDatabasesAsync(1, 2);
        await node.ModuleLoadAsync("/module.so", ["a", 5]);
        await node.ModuleLoadExtendedAsync("/module.so", [new("c1", "v1"), new("c2", "v2")], ["a", 7]);
        await node.ModuleUnloadAsync("module");
        await node.BackupStartAsync();
        await node.BackupSealAsync();
        await node.BackupAbortAsync();
        await node.BackupCleanupAsync();
        await node.ScriptKillAsync();
        await node.FunctionKillAsync();
        await node.AbortShutdownAsync();
        await node.MigrateAsync(new("destination", 6382), ["physical", RespireKey.Empty], 4, TimeSpan.FromMilliseconds(500),
            new() { Copy = true, Replace = true, Username = "user", Password = "password" });
        await node.MigrateAsync(new("destination", 6382), ["key"], 0, TimeSpan.FromSeconds(1), new() { Password = "password" });
        await Assert.That(Commands(target)).IsEquivalentTo(new[]
        {
            "ACL LOAD", "ACL SAVE", "FAILOVER", "FAILOVER TO replica 6380 FORCE TIMEOUT 1", "FAILOVER ABORT",
            "REPLICAOF primary 6381", "REPLICAOF NO ONE", "SWAPDB 1 2", "MODULE LOAD /module.so a 5",
            "MODULE LOADEX /module.so CONFIG c1 v1 CONFIG c2 v2 ARGS a 7", "MODULE UNLOAD module",
            "BACKUP START", "BACKUP SEAL", "BACKUP ABORT", "BACKUP CLEANUP", "SCRIPT KILL", "FUNCTION KILL", "SHUTDOWN ABORT",
            "MIGRATE destination 6382  4 500 COPY REPLACE AUTH2 user password KEYS physical ",
            "MIGRATE destination 6382  0 1000 AUTH password KEYS key",
        });
        await Assert.That(Commands(seed)).IsEmpty();
        await Assert.That(node.Endpoint.Port).IsEqualTo(target.Port);
    }

    [Test]
    [Arguments("load")]
    [Arguments("save")]
    [Arguments("shutdown")]
    [Arguments("abort-shutdown")]
    [Arguments("failover")]
    [Arguments("abort-failover")]
    [Arguments("replica")]
    [Arguments("promote")]
    [Arguments("swap")]
    [Arguments("module-load")]
    [Arguments("module-loadex")]
    [Arguments("module-unload")]
    [Arguments("script-kill")]
    [Arguments("function-kill")]
    [Arguments("backup-start")]
    [Arguments("backup-seal")]
    [Arguments("backup-abort")]
    [Arguments("backup-cleanup")]
    [Arguments("migrate")]
    public async Task MutationsRequireAdminBeforeConnecting(string operation)
    {
        await using var seed = Server(1);
        await using var target = Server(1);
        await using var client = await Connect(seed.Port, 2, admin: false);
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        await Assert.That(async () => await Mutate(node, operation)).ThrowsExactly<NotSupportedException>();
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    [Arguments("keys")]
    [Arguments("module-load")]
    [Arguments("module-loadex")]
    [Arguments("migrate")]
    [Arguments("command-keys")]
    public async Task BinaryInputsAreSnapshottedBeforeHandshake(string operation)
    {
        await using var seed = Server(1);
        await using var target = Server(1);
        var authenticating = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        target.SuppressReply = command =>
        {
            if (!command.StartsWith("AUTH ", StringComparison.Ordinal)) return false;
            authenticating.TrySetResult(target.ReceivedConnectionIds[^1]);
            return true;
        };
        target.ReplyOverride = (_, command) => command switch
        {
            _ when command.StartsWith("KEYS ", StringComparison.Ordinal) => "*0\r\n"u8.ToArray(),
            _ when command.StartsWith("COMMAND GETKEYSANDFLAGS ", StringComparison.Ordinal) => "*0\r\n"u8.ToArray(),
            _ => FakeRespServer.OkReply,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", seed.Port)], Protocol = RespProtocol.Resp2, Connections = 1,
            Username = "user", Password = "password", AllowAdmin = true,
        });
        byte[] bytes = [255, 0];
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        var pending = operation switch
        {
            "keys" => ReadKeys(),
            "command-keys" => ReadCommandKeys(),
            "module-load" => node.ModuleLoadAsync("/module.so", [bytes]).AsTask(),
            "module-loadex" => node.ModuleLoadExtendedAsync("/module.so", [new("config", bytes)], [bytes]).AsTask(),
            _ => Migrate(),
        };
        var connection = await authenticating.Task.WaitAsync(TimeSpan.FromSeconds(5));
        bytes.AsSpan().Clear();
        await target.SendRawAsync(FakeRespServer.OkReply, connection);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(target.ReceivedArguments[^1].Any(argument => argument.SequenceEqual(new byte[] { 255, 0 }))).IsTrue();

        async Task ReadKeys() => _ = await node.KeysAsync(bytes);
        async Task ReadCommandKeys() => _ = await node.CommandGetKeysAndFlagsAsync("GET", [bytes]);
        async Task Migrate() => _ = await node.MigrateAsync(new("destination", 6379), [bytes], 0, TimeSpan.FromSeconds(1));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BusyControlsBypassParkedClientSocketAndOtherSetup(bool function)
    {
        await using var server = Server(2);
        var parked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = command =>
        {
            if (command != "INFO") return false;
            parked.TrySetResult(server.ReceivedConnectionIds[^1]);
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, Protocol = RespProtocol.Resp3,
            Database = 4, ClientName = "configured", Username = "default", Password = "password", AllowAdmin = true,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });
        var pending = client.Server.InfoAsync().AsTask();
        var originalConnection = await parked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var node = client.Server.OnNode(new("127.0.0.1", server.Port));
        await (function ? node.FunctionKillAsync() : node.ScriptKillAsync()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var controls = server.ReceivedCommands.Zip(server.ReceivedConnectionIds).Where(row => row.Second != originalConnection).Select(row => row.First);
        await Assert.That(controls).IsEquivalentTo(new[] { "AUTH default password", function ? "FUNCTION KILL" : "SCRIPT KILL" });
        await Assert.That(pending.IsCompleted).IsFalse();
        await server.SendRawAsync("$4\r\ninfo\r\n"u8.ToArray(), originalConnection);
        await Assert.That(await pending).IsEqualTo("info");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NodeMutationsFenceCachedReadsThroughSuccessAndFailure(bool fail)
    {
        await using var seed = Server(1);
        await using var target = Server(1);
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        target.SuppressReply = command =>
        {
            if (!command.StartsWith("SWAPDB ", StringComparison.Ordinal)) return false;
            received.TrySetResult(target.ReceivedConnectionIds[^1]);
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", seed.Port)], Protocol = RespProtocol.Resp2, Connections = 1,
            ClientSideCache = new(), AllowAdmin = true,
        });
        var cache = client.Core.ClientCache!;
        Insert();
        var pending = client.Server.OnNode(new("127.0.0.1", target.Port)).SwapDatabasesAsync(0, 1).AsTask();
        var connection = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.Count).IsEqualTo(0);
        Insert();
        await target.SendRawAsync(fail ? "-ERR swap rejected\r\n"u8.ToArray() : FakeRespServer.OkReply, connection);
        if (fail) await Assert.That(async () => await pending).ThrowsExactly<RespireServerException>();
        else await pending;
        await Assert.That(cache.Count).IsEqualTo(0);

        void Insert()
        {
            RespireKey key = "cached";
            var token = cache.BeginRead(in key);
            var reply = RespValue.BulkString("stale");
            cache.CompleteRead(in token, in reply, allowInsert: true);
        }
    }

    [Test]
    [Arguments("-MOVED 123 elsewhere:6379\r\n")]
    [Arguments("-UNKILLABLE writes already occurred\r\n")]
    [Arguments("-NOTBUSY No scripts in execution\r\n")]
    public async Task NodeErrorsPropagateWithoutReplay(string error)
    {
        await using var seed = Server(1);
        await using var target = new FakeRespServer(Encoding.UTF8.GetBytes(error));
        await using var client = await Connect(seed.Port, 2, admin: true);
        await Assert.That(async () => await client.Server.OnNode(new("127.0.0.1", target.Port)).ScriptKillAsync())
            .Throws<RespireServerException>();
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(["SCRIPT KILL"]);
        await Assert.That(Commands(seed)).IsEmpty();
    }

    [Test]
    public async Task ShutdownCompletesAfterWriteWithoutReplyAndUsesControlGrammar()
    {
        await using var seed = Server(1);
        await using var target = Server(1);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.SuppressReply = _ => { received.TrySetResult(); return true; };
        await using var client = await Connect(seed.Port, 3, admin: true);
        await client.Server.OnNode(new("127.0.0.1", target.Port)).RequestShutdownAsync(new()
        { SaveMode = RespireShutdownSaveMode.NoSave, Now = true, Force = true }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(["SHUTDOWN NOSAVE NOW FORCE"]);
        await target.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task CancellationAndValidationDoNotConnectAndDisposalInvalidatesHandle()
    {
        await using var seed = Server(1);
        await using var target = Server(1);
        await using var client = await Connect(seed.Port, 2, admin: true);
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.That(async () => await node.ScriptKillAsync(cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await node.AclGeneratePasswordAsync(0)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await node.AclGeneratePasswordAsync(1025)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await node.FailoverAsync(new() { Force = true })).ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await node.MigrateAsync(new("destination", 6379), ["key"], 0, TimeSpan.Zero)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(async () => await node.MigrateAsync(new("destination", 6379), ["key"], 0, TimeSpan.FromSeconds(1), new() { Username = "user" })).ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await node.SwapDatabasesAsync(-1, 0)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => client.Server.OnNode(new("host", 0))).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
        await client.DisposeAsync();
        await Assert.That(async () => await node.AclUsersAsync()).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationOrClientDisposalClosesOutstandingNodeSocket(bool disposeClient)
    {
        await using var seed = Server(1);
        await using var target = Server(1);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.SuppressReply = _ => { received.TrySetResult(); return true; };
        await using var client = await Connect(seed.Port, 2, admin: true);
        using var cancellation = new CancellationTokenSource();
        var pending = client.Server.OnNode(new("127.0.0.1", target.Port)).ScriptKillAsync(cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (disposeClient)
        {
            await client.DisposeAsync();
            await Assert.That(async () => await pending).Throws<RespireConnectionException>();
        }
        else
        {
            cancellation.Cancel();
            await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        }
        await target.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(["SCRIPT KILL"]);
    }

    private static Task Mutate(RespireServerNode node, string operation) => operation switch
    {
        "load" => node.AclLoadAsync().AsTask(), "save" => node.AclSaveAsync().AsTask(),
        "shutdown" => node.RequestShutdownAsync().AsTask(), "abort-shutdown" => node.AbortShutdownAsync().AsTask(),
        "failover" => node.FailoverAsync().AsTask(), "abort-failover" => node.AbortFailoverAsync().AsTask(),
        "replica" => node.ReplicaOfAsync(new("primary", 6379)).AsTask(), "promote" => node.PromoteToPrimaryAsync().AsTask(),
        "swap" => node.SwapDatabasesAsync(0, 1).AsTask(), "module-load" => node.ModuleLoadAsync("/module.so", []).AsTask(),
        "module-loadex" => node.ModuleLoadExtendedAsync("/module.so", [], []).AsTask(), "module-unload" => node.ModuleUnloadAsync("module").AsTask(),
        "script-kill" => node.ScriptKillAsync().AsTask(), "function-kill" => node.FunctionKillAsync().AsTask(),
        "backup-start" => node.BackupStartAsync().AsTask(), "backup-seal" => node.BackupSealAsync().AsTask(),
        "backup-abort" => node.BackupAbortAsync().AsTask(), "backup-cleanup" => node.BackupCleanupAsync().AsTask(),
        _ => node.MigrateAsync(new("destination", 6379), ["key"], 0, TimeSpan.FromSeconds(1)).AsTask(),
    };

    private static FakeRespServer Server(int connections)
    {
        var server = new FakeRespServer(connections, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command.StartsWith("HELLO ", StringComparison.Ordinal)
            ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : FakeRespServer.OkReply;
        return server;
    }

    private static IEnumerable<string> Commands(FakeRespServer server)
        => server.ReceivedCommands.Where(command => !command.StartsWith("HELLO ", StringComparison.Ordinal));

    private static ValueTask<RespireClient> Connect(int port, int protocol, bool admin)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", port)], Connections = 1, AllowAdmin = admin,
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });
}
