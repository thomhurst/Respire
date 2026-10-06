using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ServerNodeCommandTests
{
    [Test]
    public async Task ControlOptionsPreserveTransportAndAuthenticationWithoutOrdinarySetup()
    {
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp3, Database = 4, ClientName = "configured", UseCluster = true,
            ClientAvailabilityZone = "zone", ClientSideCache = new(),
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            Username = "user", Password = "password", UseTls = true,
            ConnectTimeout = TimeSpan.FromSeconds(7), CommandTimeout = TimeSpan.FromSeconds(11),
            ConnectionIdleReadTimeout = TimeSpan.FromSeconds(13),
        };
        var ordinary = options.ToConnectionOptions(enableClientTracking: true, enableMaintenanceNotifications: true);
        var control = options.ToControlConnectionOptions();
        await Assert.That(ordinary.Protocol).IsEqualTo(RespProtocol.Resp3);
        await Assert.That(ordinary.Database).IsEqualTo(4);
        await Assert.That(ordinary.RequireClusterDatabaseSupport && ordinary.DiscoverAvailabilityZone
            && ordinary.EnableClientTracking).IsTrue();
        await Assert.That(ordinary.MaintenanceNotifications).IsEqualTo(RespireMaintenanceNotificationMode.Enabled);
        await Assert.That(control.Protocol).IsEqualTo(RespProtocol.Resp2);
        await Assert.That(control.Database).IsEqualTo(0);
        await Assert.That(control.ClientName).IsNull();
        await Assert.That(control.RequireClusterDatabaseSupport || control.DiscoverAvailabilityZone
            || control.EnableClientTracking || control.ReadOnly).IsFalse();
        await Assert.That(control.MaintenanceNotifications).IsEqualTo(RespireMaintenanceNotificationMode.Disabled);
        await Assert.That(control.PushHandler).IsNull();
        await Assert.That(control.Username).IsEqualTo(options.Username);
        await Assert.That(control.Password).IsEqualTo(options.Password);
        await Assert.That(control.UseTls).IsTrue();
        await Assert.That(control.ConnectTimeout).IsEqualTo(options.ConnectTimeout);
        await Assert.That(control.CommandTimeout).IsEqualTo(options.CommandTimeout);
        await Assert.That(control.ResponseTimeout).IsEqualTo(options.ConnectionIdleReadTimeout);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnixNodeHandlesFromTcpClientsDoNotNegotiateMaintenance(bool control)
    {
        await using var target = Server(1);
        var opened = new TaskCompletionSource<RespireEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 6379)], Protocol = RespProtocol.Resp3, AllowAdmin = true,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            TestingStreamFactory = async (host, port, token) =>
            {
                opened.TrySetResult(new(host, port));
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync("127.0.0.1", target.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        });
        var endpoint = RespireEndpoint.UnixSocket("/tmp/respire-node.sock");
        var node = client.Server.OnNode(endpoint);
        await (control ? node.ScriptKillAsync() : node.AclSaveAsync());
        await Assert.That(await opened.Task).IsEqualTo(endpoint);
        await Assert.That(Commands(target)).IsEquivalentTo([control ? "SCRIPT KILL" : "ACL SAVE"]);
        await Assert.That(target.ReceivedCommands.Count(command => command == "HELLO 3")).IsEqualTo(control ? 0 : 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MigrateTimeoutOverrideCanShortenOrExtendOnlyItsOwnConnection(bool extend)
    {
        await using var target = Server(2);
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        target.SuppressReply = command =>
        {
            if (!command.StartsWith("MIGRATE ", StringComparison.Ordinal)) return extend && command == "SCRIPT KILL";
            received.TrySetResult(target.ReceivedConnectionIds[^1]);
            return true;
        };
        var shortBudget = TimeSpan.FromMilliseconds(100);
        var longBudget = TimeSpan.FromSeconds(30);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 6379)], Protocol = RespProtocol.Resp2, AllowAdmin = true,
            CommandTimeout = extend ? shortBudget : longBudget,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        var migration = node.MigrateAsync(new("destination", 6382), ["key"], 0, TimeSpan.FromSeconds(1),
            new() { CommandTimeout = extend ? longBudget : shortBudget }).AsTask();
        var connection = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (extend)
        {
            // A real inherited deadline expires before releasing the overridden migration.
            await Assert.That(async () => await node.ScriptKillAsync()).ThrowsExactly<RespireTimeoutException>();
            await Assert.That(migration.IsCompleted).IsFalse();
            await target.SendRawAsync(FakeRespServer.OkReply, connection);
            await Assert.That(await migration).IsEqualTo(RespireMigrateResult.Migrated);
        }
        else
        {
            var error = await Assert.That(async () => await migration).ThrowsExactly<RespireTimeoutException>();
            await Assert.That(error!.CommandName).IsEqualTo("MIGRATE");
            await node.ScriptKillAsync();
        }
        await Assert.That(client.Core.Options.CommandTimeout).IsEqualTo(extend ? shortBudget : longBudget);
        await Assert.That(target.ReceivedCommands).IsEquivalentTo([
            "MIGRATE destination 6382  0 1000 KEYS key", "SCRIPT KILL"]);
    }

    [Test]
    [Arguments(-1L)]
    [Arguments(0L)]
    [Arguments(9999L)]
    public async Task InvalidMigrateClientTimeoutDoesNotConnect(long ticks)
    {
        await using var target = Server(1);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 6379)], AllowAdmin = true,
        });
        var error = await Assert.That(async () => await client.Server.OnNode(new("127.0.0.1", target.Port))
            .MigrateAsync(new("destination", 6382), ["key"], 0, TimeSpan.FromSeconds(1),
                new() { CommandTimeout = TimeSpan.FromTicks(ticks) })).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(error!.Message).Contains("MIGRATE CommandTimeout must be at least one millisecond.");
        await Assert.That(target.ConnectionAccepted.IsCompleted).IsFalse();
    }

    [Test]
    public async Task ShutdownDoesNotCompleteOrDisposeBeforeSocketWriteFinishes()
    {
        await using var target = Server(1);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.SuppressReply = _ => { received.TrySetResult(); return true; };
        var opened = new TaskCompletionSource<GatedWriteStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 6379)], AllowAdmin = true,
            TestingStreamFactory = async (host, port, token) =>
            {
                var stream = await GatedWriteStream.ConnectAsync(host, port, received.Task, token);
                opened.TrySetResult(stream);
                return stream;
            },
        });
        var pending = client.Server.OnNode(new("127.0.0.1", target.Port)).SendShutdownAsync().AsTask();
        var transport = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await transport.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(pending.IsCompleted).IsFalse();
            await Assert.That(transport.Disposed).IsFalse();
            await Assert.That(target.ReceivedCommands).IsEmpty();
        }
        finally { transport.ReleaseWrite.TrySetResult(); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await target.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(["SHUTDOWN"]);
        await Assert.That(transport.Disposed).IsTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ForcedFailoverPreservesEveryArgumentBoundaryAndOrder(int protocol)
    {
        await using var seed = Server(1);
        await using var target = Server(1);
        await using var client = await Connect(seed.Port, protocol, admin: true);
        await client.Server.OnNode(new("127.0.0.1", target.Port)).FailoverAsync(new()
        {
            Target = new("replica", 6380), Force = true, Timeout = TimeSpan.FromMilliseconds(123),
        });
        byte[][] expected = ["FAILOVER"u8.ToArray(), "TO"u8.ToArray(), "replica"u8.ToArray(), "6380"u8.ToArray(),
            "FORCE"u8.ToArray(), "TIMEOUT"u8.ToArray(), "123"u8.ToArray()];
        var actual = target.ReceivedArguments[^1];
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        for (var index = 0; index < expected.Length; index++)
            await Assert.That(actual[index].AsSpan().SequenceEqual(expected[index])).IsTrue();
        await Assert.That(Commands(seed)).IsEmpty();
    }

    [Test]
    [NotInParallel] // Activity and meter listeners are process-wide.
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task MigrateCredentialsStayOutOfClientTelemetryAndDiagnostics(bool auth2, bool fail)
    {
        const string password = "destination-password-unique";
        const string username = "destination-user-unique";
        await using var seed = Server(1);
        await using var target = Server(1);
        target.ReplyOverride = (_, command) =>
        {
            if (!command.StartsWith("MIGRATE ", StringComparison.Ordinal)) return null;
            return fail ? "-ERR migration refused\r\n"u8.ToArray() : FakeRespServer.OkReply;
        };
        using var logger = new DiagnosticCapture();
        using var metrics = new MetricConfigurationScope();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", seed.Port)], Connections = 1, Protocol = RespProtocol.Resp2,
            AllowAdmin = true, LoggerFactory = logger,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RespireTelemetry.Source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("db.operation.name") as string == "MIGRATE" && TestTelemetry.IsFrom(activity, target.Port))
                    activities.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        var measurements = new ConcurrentQueue<string>();
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == RespireTelemetry.Meter.Name && instrument.Name == "db.client.operation.duration")
                owner.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            if (TestTelemetry.IsFrom(tags, target.Port))
                measurements.Enqueue(string.Join('\n', tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}")));
        });
        meter.Start();
        RespireServerException? error = null;
        try
        {
            await client.Server.OnNode(new("127.0.0.1", target.Port)).MigrateAsync(new("destination", 6382), ["key"],
                0, TimeSpan.FromSeconds(1), new() { Password = password, Username = auth2 ? username : null });
        }
        catch (RespireServerException caught) { error = caught; }
        await Assert.That(error is not null).IsEqualTo(fail);
        if (error is not null) await Assert.That(error.CommandName).IsEqualTo("MIGRATE");
        var activity = activities.Single();
        await Assert.That(activity.DisplayName).IsEqualTo("MIGRATE");
        await Assert.That(activity.Status).IsEqualTo(fail ? ActivityStatusCode.Error : ActivityStatusCode.Unset);
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(logger.Messages).IsNotEmpty();
        var diagnostics = string.Join('\n', logger.Messages.Concat(measurements)
            .Concat(activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"))
            .Concat(activity.Events.SelectMany(item => item.Tags.Select(tag => $"{tag.Key}={tag.Value}"))))
            + activity.StatusDescription + error?.ToString();
        await Assert.That(diagnostics.Contains(password, StringComparison.Ordinal)).IsFalse();
        await Assert.That(diagnostics.Contains(username, StringComparison.Ordinal)).IsFalse();
        // Positive controls: authentication really travels on the wire, and capture is enabled.
        var arguments = target.ReceivedArguments[^1];
        await Assert.That(arguments.Any(argument => Encoding.UTF8.GetString(argument) == password)).IsTrue();
        await Assert.That(arguments.Any(argument => Encoding.UTF8.GetString(argument) == username)).IsEqualTo(auth2);
    }

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
        await using var target = Server(1);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.SuppressReply = _ => { received.TrySetResult(); return true; };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", target.Port)], Protocol = RespProtocol.Resp3, AllowAdmin = true,
            TestingStreamFactory = async (host, port, token) =>
            {
                var stream = await GatedWriteStream.ConnectAsync(host, port, received.Task, token);
                stream.ReleaseWrite.TrySetResult();
                return stream;
            },
        });
        await client.Server.OnNode(new("127.0.0.1", target.Port)).SendShutdownAsync(new()
        { SaveMode = RespireShutdownSaveMode.NoSave, Now = true, Force = true }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(["SHUTDOWN NOSAVE NOW FORCE"]);
        await target.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ShutdownLocalWriteCanCompleteBeforePeerReceivesCommand()
    {
        await using var target = Server(1);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.SuppressReply = command =>
        {
            if (command == "AUTH password")
            {
                target.ReadGate = releaseRead.Task;
                return false;
            }
            received.TrySetResult();
            return true;
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", target.Port)], Protocol = RespProtocol.Resp3,
            AllowAdmin = true, Password = "password",
        });
        try
        {
            await client.Server.OnNode(new("127.0.0.1", target.Port)).SendShutdownAsync(new()
            { SaveMode = RespireShutdownSaveMode.NoSave, Now = true, Force = true }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(received.Task.IsCompleted).IsFalse();
            await Assert.That(target.ReceivedCommands).IsEquivalentTo(["AUTH password"]);
        }
        finally { releaseRead.TrySetResult(); }
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
        "shutdown" => node.SendShutdownAsync().AsTask(), "abort-shutdown" => node.AbortShutdownAsync().AsTask(),
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

    private sealed class DiagnosticCapture : ILoggerFactory, ILogger
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Enqueue(formatter(state, exception) + exception?.ToString());
    }

    // Single-command test connections only: no AUTH or HELLO setup writes before the observed command.
    private sealed class GatedWriteStream(Socket socket, Task received) : NetworkStream(socket, ownsSocket: true)
    {
        internal TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }

        internal static async Task<GatedWriteStream> ConnectAsync(string host, int port, Task received, CancellationToken token)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(host, port, token);
                return new GatedWriteStream(socket, received);
            }
            catch { socket.Dispose(); throw; }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await ReleaseWrite.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
            // Keep this test transport alive until the peer records the bytes, without requiring a reply.
            // Production promises only local write completion; it does not provide this synchronization.
            await received.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            ReleaseWrite.TrySetResult();
            base.Dispose(disposing);
        }
    }
}
