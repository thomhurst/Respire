using System.Text;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelClientTests
{
    [Test]
    [Arguments("", false, false, false)]
    [Arguments("redis_version:unknown", false, false, false)]
    // Bare major versions are deliberately unknown: Version.TryParse requires major.minor.
    [Arguments("redis_version:8", false, false, false)]
    [Arguments("valkey_version:8", false, false, false)]
    [Arguments("redis_version:4.0.14", false, false, false)]
    [Arguments("redis_version:5.0.0", false, true, false)]
    [Arguments("redis_version:7.1.9", false, true, false)]
    [Arguments("redis_version:7.2.0", false, true, true)]
    [Arguments("redis_version:8.10.0", false, true, true)]
    [Arguments("valkey_version:8.0.0", true, true, true)]
    [Arguments("redis_version:7.2.4\r\nvalkey_version:9.0.0", true, true, true)]
    public async Task ServerProfileCentralizesCompatibilityBoundaries(string info, bool primaryAliases, bool replicas, bool multiOption)
    {
        var profile = SentinelServerProfile.FromInfo(info);
        await Assert.That(profile.PrimaryCommand).IsEqualTo(primaryAliases ? "PRIMARY" : "MASTER");
        await Assert.That(profile.PrimariesCommand).IsEqualTo(primaryAliases ? "PRIMARIES" : "MASTERS");
        await Assert.That(profile.DownStateCommand).IsEqualTo(primaryAliases ? "IS-PRIMARY-DOWN-BY-ADDR" : "IS-MASTER-DOWN-BY-ADDR");
        await Assert.That(profile.ReplicasCommand).IsEqualTo(replicas ? "REPLICAS" : "SLAVES");
        await Assert.That(profile.SupportsMultiOptionConfig).IsEqualTo(multiOption);
        if (!primaryAliases && !replicas && !multiOption)
            await Assert.That(profile).IsEqualTo(SentinelServerProfile.Legacy);
    }

    private static byte[] Bulk(string value) => Encoding.UTF8.GetBytes($"${Encoding.UTF8.GetByteCount(value)}\r\n{value}\r\n");

    /// <summary>Checks version-gated multi-option writes and preserves single-option support.</summary>
    [Test]
    [Arguments("redis_version:6.2.14", false)]
    [Arguments("redis_version:7.0.15", false)]
    [Arguments("redis_version:7.2.0", true)]
    [Arguments("redis_version:7.2.4\r\nvalkey_version:8.0.0", true)]
    [Arguments("unknown_version:1.0.0", false)]
    public async Task MultiOptionConfigUsesOnlySupportedServerVersions(string version, bool supported)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "INFO SERVER" ? Bulk(version) : null,
        };
        await using var client = await RespireSentinelClient.ConnectAsync(new("127.0.0.1", server.Port), new() { AllowAdmin = true });
        var options = new Dictionary<string, string> { ["announce-ip"] = "127.0.0.2", ["announce-port"] = "26380" };
        if (supported)
        {
            await client.ConfigSetAsync(options);
            await Assert.That(server.ReceivedCommands.Contains("SENTINEL CONFIG SET announce-ip 127.0.0.2 announce-port 26380")).IsTrue();
        }
        else
        {
            await Assert.That(async () => await client.ConfigSetAsync(options)).Throws<NotSupportedException>();
            await Assert.That(server.CommandsSeen).IsEqualTo(1);
        }
        await client.ConfigSetAsync(new Dictionary<string, string> { ["announce-port"] = "26380" });
        await Assert.That(server.ReceivedCommands.Contains("SENTINEL CONFIG SET announce-port 26380")).IsTrue();
    }

    [Test]
    [Arguments("redis_version:4.0.14", "MASTER", "MASTERS", "SLAVES", "IS-MASTER-DOWN-BY-ADDR")]
    [Arguments("redis_version:8.10.2", "MASTER", "MASTERS", "REPLICAS", "IS-MASTER-DOWN-BY-ADDR")]
    [Arguments("redis_version:7.2.4\r\nvalkey_version:8.1.0", "PRIMARY", "PRIMARIES", "REPLICAS", "IS-PRIMARY-DOWN-BY-ADDR")]
    public async Task SelectsServerNamesAndKeepsRepliesOwned(string version, string primary, string primaries, string replicas, string down)
    {
        var fields = "*12\r\n$4\r\nname\r\n$4\r\nmain\r\n$2\r\nip\r\n$9\r\n127.0.0.1\r\n$4\r\nport\r\n$4\r\n6379\r\n$5\r\nflags\r\n$6\r\nmaster\r\n$12\r\nconfig-epoch\r\n$1\r\n0\r\n$6\r\nquorum\r\n$1\r\n2\r\n";
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "INFO SERVER" => Bulk(version),
                var text when text == "SENTINEL " + primary + " main" => Encoding.UTF8.GetBytes(fields),
                var text when text == "SENTINEL " + primaries => Encoding.UTF8.GetBytes("*1\r\n" + fields),
                var text when text.StartsWith("SENTINEL " + down, StringComparison.Ordinal) => "*3\r\n:0\r\n$1\r\n*\r\n:0\r\n"u8.ToArray(),
                _ => "*0\r\n"u8.ToArray(),
            },
        };
        await using var client = await RespireSentinelClient.ConnectAsync(new("127.0.0.1", server.Port));
        var state = await client.PrimaryAsync("main");
        var all = await client.PrimariesAsync();
        await client.ReplicasAsync("main");
        var observed = await client.IsPrimaryDownByAddressAsync(new("127.0.0.1", 6379));
        await Assert.That(state.Name).IsEqualTo("main");
        await Assert.That(state.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", 6379));
        await Assert.That(all[0].Attributes["quorum"]).IsEqualTo("2");
        await Assert.That(observed.IsDown).IsFalse();
        await Assert.That(server.ReceivedCommands.Contains("SENTINEL " + replicas + " main")).IsTrue();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("SENTINEL " + down, StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task MutationsUseSentinelCredentialsAndNeverDiscoverDataPrimary()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "INFO SERVER" => Bulk("redis_version:7.2.0"),
                "SENTINEL RESET temp*" => ":1\r\n"u8.ToArray(),
                "SENTINEL SIMULATE-FAILURE help" => "*2\r\n$20\r\ncrash-after-election\r\n$21\r\ncrash-after-promotion\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = await RespireSentinelClient.ConnectAsync(new("127.0.0.1", server.Port), new()
        {
            Username = "data", Password = "data-secret", Database = 9, SentinelPrimaryName = "not-discovered",
            SentinelUsername = "operator", SentinelPassword = "sentinel-secret", AllowAdmin = true,
        });
        await client.MonitorAsync("temp name", new("127.0.0.1", 6380), 2);
        await client.SetAsync("temp name", new Dictionary<string, string> { ["down-after-milliseconds"] = "5000" });
        await client.ConfigSetAsync(new Dictionary<string, string> { ["announce-ip"] = "127.0.0.2" });
        await client.FailoverAsync("temp name");
        await client.FlushConfigAsync();
        await client.SimulateFailureAsync(RespireSentinelFailure.CrashAfterElection | RespireSentinelFailure.CrashAfterPromotion);
        await client.SimulateFailureAsync(RespireSentinelFailure.None);
        await client.RemoveAsync("temp name");
        await Assert.That(await client.ResetAsync("temp*")).IsEqualTo(1);
        var commands = server.ReceivedCommands;
        await Assert.That(commands.Contains("AUTH operator sentinel-secret")).IsTrue();
        await Assert.That(commands.Any(command => command.Contains("data-secret", StringComparison.Ordinal)
            || command.StartsWith("SELECT", StringComparison.Ordinal) || command.Contains("GET-MASTER", StringComparison.Ordinal))).IsFalse();
        await Assert.That(commands.Contains("SENTINEL MONITOR temp name 127.0.0.1 6380 2")).IsTrue();
        // A space in the service name remains one wire argument, not two tokens.
        var monitorIndex = commands.ToList().IndexOf("SENTINEL MONITOR temp name 127.0.0.1 6380 2");
        await Assert.That(Encoding.UTF8.GetString(server.ReceivedArguments[monitorIndex][2])).IsEqualTo("temp name");
        await Assert.That(commands.Contains("SENTINEL CONFIG SET announce-ip 127.0.0.2")).IsTrue();
        await Assert.That(commands.Contains("SENTINEL SIMULATE-FAILURE crash-after-election crash-after-promotion")).IsTrue();
        await Assert.That(commands.Contains("SENTINEL SIMULATE-FAILURE help")).IsTrue();
    }

    /// <summary>Checks that the monitoring API sends caller-specified ordering and duplicate options unchanged.</summary>
    [Test]
    public async Task MonitoringOptionsPreserveExplicitOrderAndDuplicates()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "INFO SERVER" ? Bulk("redis_version:7.2.0") : null,
        };
        await using var client = await RespireSentinelClient.ConnectAsync(new("127.0.0.1", server.Port), new() { AllowAdmin = true });
        KeyValuePair<string, string>[] options =
        [
            new("auth-user", "first"), new("auth-pass", "secret"), new("auth-user", "second"),
        ];
        await client.SetAsync("main", options);
        await Assert.That(server.ReceivedCommands.Contains("SENTINEL SET main auth-user first auth-pass secret auth-user second")).IsTrue();
    }

    /// <summary>Checks conservative ACL fallback without hiding authentication failures.</summary>
    [Test]
    [Arguments("NOPERM permission denied", true)]
    [Arguments("NOAUTH authentication required", false)]
    public async Task OnlyInfoPermissionDenialFallsBackToLegacyNames(string error, bool fallback)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "INFO SERVER" => Encoding.UTF8.GetBytes("-" + error + "\r\n"),
                "SENTINEL SLAVES main" => "*0\r\n"u8.ToArray(),
                _ => null,
            },
        };
        if (!fallback)
        {
            await Assert.That(async () => await RespireSentinelClient.ConnectAsync(new("127.0.0.1", server.Port))).Throws<RespireServerException>();
            return;
        }
        await using var client = await RespireSentinelClient.ConnectAsync(new("127.0.0.1", server.Port), new() { AllowAdmin = true });
        await Assert.That(await client.ReplicasAsync("main")).IsEmpty();
        await Assert.That(async () => await client.ConfigSetAsync(new Dictionary<string, string>
        {
            ["announce-ip"] = "127.0.0.2", ["announce-port"] = "26380",
        })).Throws<NotSupportedException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(2);
    }

    [Test]
    public async Task MutationsRequireAdminAndInvalidArgumentsNeverReachServer()
    {
        await using var server = new FakeRespServer(Bulk("redis_version:7.2.0"));
        await using var client = await RespireSentinelClient.ConnectAsync(new("127.0.0.1", server.Port));
        await Assert.That(async () => await client.FailoverAsync("main")).Throws<NotSupportedException>();
        await Assert.That(async () => await client.IsPrimaryDownByAddressAsync(new("127.0.0.1"), runId: "candidate")).Throws<NotSupportedException>();
        await Assert.That(async () => await client.MonitorAsync("main", new("127.0.0.1", 0), 1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.SimulateFailureAsync((RespireSentinelFailure)4)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationAndServerErrorsPropagate()
    {
        await using var server = new FakeRespServer(Bulk("redis_version:7.2.0"))
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL CKQUORUM", StringComparison.Ordinal)
                ? "-NOQUORUM unavailable\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireSentinelClient.ConnectAsync(new("127.0.0.1", server.Port));
        await Assert.That(async () => await client.CheckQuorumAsync("main")).Throws<RespireServerException>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(async () => await client.MyIdAsync(cancelled.Token)).Throws<OperationCanceledException>();
        await client.DisposeAsync();
        await Assert.That(async () => await client.MyIdAsync()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task ParsesReplicaStatesCachedInfoAndPendingScripts()
    {
        using var replica = new RespireResult(RespValue.Array(RespValue.Array(
            RespValue.BulkString("name"), RespValue.BulkString("replica"),
            RespValue.BulkString("ip"), RespValue.BulkString("::1"),
            RespValue.BulkString("port"), RespValue.BulkString("6380"),
            RespValue.BulkString("flags"), RespValue.BulkString("slave,s_down,disconnected"),
            RespValue.BulkString("master-host"), RespValue.BulkString("::1"),
            RespValue.BulkString("master-port"), RespValue.BulkString("6379"),
            RespValue.BulkString("slave-repl-offset"), RespValue.BulkString("42"),
            RespValue.BulkString("slave-priority"), RespValue.BulkString("100"))));
        var parsed = SentinelReply.Replicas(replica)[0];
        await Assert.That(parsed.Flags).IsEqualTo("slave,s_down,disconnected");
        await Assert.That(parsed.ReplicationOffset).IsEqualTo(42);
        using var cache = new RespireResult(RespValue.Array(RespValue.BulkString("main"), RespValue.Array(
            RespValue.Array(RespValue.Integer(123), RespValue.BulkString("# Server")),
            RespValue.Array(RespValue.Integer(0), RespValue.Null))));
        var cached = SentinelReply.InfoCache(cache);
        await Assert.That(cached[0].AgeMilliseconds).IsEqualTo(123);
        await Assert.That(cached[1].Info).IsNull();
        using var scripts = new RespireResult(RespValue.Array(RespValue.Array(
            RespValue.BulkString("argv"), RespValue.Array(RespValue.BulkString("/script"), RespValue.BulkString("argument")),
            RespValue.BulkString("flags"), RespValue.BulkString("scheduled"),
            RespValue.BulkString("pid"), RespValue.BulkString("0"),
            RespValue.BulkString("run-delay"), RespValue.BulkString("50"),
            RespValue.BulkString("retry-num"), RespValue.BulkString("2"))));
        var script = SentinelReply.Scripts(scripts)[0];
        await Assert.That(script.Arguments).IsEquivalentTo(["/script", "argument"]);
        await Assert.That(script.TimeMilliseconds).IsEqualTo(50);
    }

    [Test]
    public async Task KeepsReplicaWhosePrimaryAddressIsNotYetKnown()
    {
        using var reply = new RespireResult(RespValue.Array(RespValue.Array(
            RespValue.BulkString("name"), RespValue.BulkString("replica"),
            RespValue.BulkString("ip"), RespValue.BulkString("127.0.0.1"),
            RespValue.BulkString("port"), RespValue.BulkString("6380"),
            RespValue.BulkString("flags"), RespValue.BulkString("slave,disconnected"),
            RespValue.BulkString("master-host"), RespValue.BulkString("?"),
            RespValue.BulkString("master-port"), RespValue.BulkString("0"),
            RespValue.BulkString("slave-repl-offset"), RespValue.BulkString("0"),
            RespValue.BulkString("slave-priority"), RespValue.BulkString("100"))));
        var replica = SentinelReply.Replicas(reply)[0];
        await Assert.That(replica.Primary).IsNull();
        await Assert.That(replica.Flags).IsEqualTo("slave,disconnected");
    }

    [Test]
    public async Task MalformedRepliesNeverBecomeEmptyOrHealthySnapshots()
    {
        using var scalar = new RespireResult(RespValue.Integer(0));
        using var odd = new RespireResult(RespValue.Array(RespValue.BulkString("ip")));
        using var invalidDown = new RespireResult(RespValue.Array(RespValue.Integer(2), RespValue.BulkString("*"), RespValue.Integer(0)));
        await Assert.That(() => SentinelReply.Primaries(scalar)).Throws<RespireProtocolException>();
        await Assert.That(() => SentinelReply.Fields(odd)).Throws<RespireProtocolException>();
        await Assert.That(() => SentinelReply.DownState(invalidDown)).Throws<RespireProtocolException>();
        await Assert.That(() => SentinelReply.Primary(odd)).Throws<RespireProtocolException>();
    }

    [Test]
    public async Task MapRepliesCopyFieldsBeforeLeaseDisposal()
    {
        // RESP3 map framing is accepted even when the caller negotiated RESP2.
        await using var server = new FakeRespServer(Bulk("redis_version:7.2.0"))
        {
            ReplyOverride = (_, command) => command == "SENTINEL CONFIG GET announce-*"
                ? "%2\r\n$11\r\nannounce-ip\r\n$9\r\n127.0.0.1\r\n$13\r\nannounce-port\r\n$5\r\n26379\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireSentinelClient.ConnectAsync(new("127.0.0.1", server.Port));
        var fields = await client.ConfigGetAsync("announce-*");
        await client.DisposeAsync();
        await Assert.That(fields["announce-ip"]).IsEqualTo("127.0.0.1");
        await Assert.That(fields["announce-port"]).IsEqualTo("26379");
    }
}
