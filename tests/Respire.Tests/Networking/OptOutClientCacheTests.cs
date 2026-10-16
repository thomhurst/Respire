using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class OptOutClientCacheTests
{
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();
    private static readonly byte[] Value = "$5\r\nvalue\r\n"u8.ToArray();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OrdinaryUncachedReadsKeepDefaultOptOutTracking(bool retry)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            ClientSideCache = new() { TrackingMode = RespireClientTrackingMode.OptOut, KeyPrefixes = ["hot:"] },
            RetryPolicy = retry ? new() { MaxAttempts = 2 } : null,
        });
        await Assert.That(await client.GetStringAsync("cold:key")).IsEqualTo("value");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Contains("CLIENT CACHING NO")).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task BypassTransactionPrefixesMultiAndDrainsErrors(bool retry, bool prefixError)
    {
        await using var server = Server();
        var inTransaction = false;
        server.ReplyOverride = (_, command) =>
        {
            if (command == "MULTI") { inTransaction = true; return FakeRespServer.OkReply; }
            if (command == "EXEC") { inTransaction = false; return "*3\r\n$5\r\nvalue\r\n+OK\r\n$5\r\nvalue\r\n"u8.ToArray(); }
            if (inTransaction) return "+QUEUED\r\n"u8.ToArray();
            return command.StartsWith("HELLO ", StringComparison.Ordinal) ? Hello
                : command == "CLIENT CACHING NO" && prefixError ? "-ERR denied caching\r\n"u8.ToArray()
                : command.StartsWith("GET ", StringComparison.Ordinal) ? Value : FakeRespServer.OkReply;
        };
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            RetryPolicy = retry ? new() { MaxAttempts = 2 } : null,
        });
        await using var transaction = client.WithoutClientCache().CreateTransaction();
        var first = transaction.GetString("first");
        _ = transaction.Set("written", "new");
        var second = transaction.GetString("second");
        await Assert.That(transaction.Count).IsEqualTo(3);
        if (prefixError)
        {
            await Assert.That(async () => await transaction.CommitAsync()).ThrowsExactly<RespireServerException>();
            await Assert.That(first.Error).IsTypeOf<RespireServerException>();
            await Assert.That(second.Error).IsTypeOf<RespireServerException>();
        }
        else
        {
            await transaction.CommitAsync();
            await Assert.That(first.Result).IsEqualTo("value");
            await Assert.That(second.Result).IsEqualTo("value");
        }
        await Assert.That(await client.GetStringAsync("after")).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.SequenceEqual(new[]
        {
            "HELLO 3", "CLIENT TRACKING ON OPTOUT", "CLIENT CACHING NO", "MULTI",
            "GET first", "SET written new", "GET second", "EXEC", "GET after",
        })).IsTrue();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task BypassTransactionWithoutReadsNeedsNoExtraCapacity(bool retry, bool empty)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => Hello,
            "SET written new" or "SET second new" => "+QUEUED\r\n"u8.ToArray(),
            "EXEC" => "*2\r\n+OK\r\n+OK\r\n"u8.ToArray(),
            _ => FakeRespServer.OkReply,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            MaxInflightCommands = 4,
            RetryPolicy = retry ? new() { MaxAttempts = 2 } : null,
        });
        await using var transaction = client.WithoutClientCache().CreateTransaction();
        if (!empty)
        {
            _ = transaction.Set("written", "new");
            _ = transaction.Set("second", "new");
        }
        await transaction.CommitAsync();
        // A following reply proves the transaction drained all of its response slots.
        await client.PingAsync();
        string[] expected = empty
            ? ["HELLO 3", "CLIENT TRACKING ON OPTOUT", "PING"]
            : ["HELLO 3", "CLIENT TRACKING ON OPTOUT", "MULTI", "SET written new", "SET second new", "EXEC", "PING"];
        await Assert.That(server.ReceivedCommands.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BypassTransactionRejectsInsufficientCapacityBeforeWriting(bool retry)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            MaxInflightCommands = 4,
            RetryPolicy = retry ? new() { MaxAttempts = 2 } : null,
        });
        await using var transaction = client.WithoutClientCache().CreateTransaction();
        _ = transaction.GetString("key");
        _ = transaction.GetString("second");
        await Assert.That(async () => await transaction.CommitAsync()).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands.Contains("MULTI")).IsFalse();
        await Assert.That(server.ReceivedCommands.Contains("CLIENT CACHING NO")).IsFalse();
        await Assert.That(await client.GetStringAsync("after")).IsEqualTo("value");
    }

    [Test]
    public async Task BypassBatchPreservesReadOnlyScriptingErrorClassification()
    {
        await using var server = Server();
        const string info = "# Scripting Engines\r\nengines_count:0\r\n";
        var infoReply = Encoding.UTF8.GetBytes($"${info.Length}\r\n{info}\r\n");
        server.ReplyOverride = (_, command) => command.StartsWith("HELLO ", StringComparison.Ordinal) ? Hello
            : command.StartsWith("EVAL_RO ", StringComparison.Ordinal) ? "-ERR Could not find scripting engine 'lua'\r\n"u8.ToArray()
            : command == "INFO scriptingengines" ? infoReply : FakeRespServer.OkReply;
        await using var client = await RespireClient.ConnectAsync(Options(server));
        using var batch = client.WithoutClientCache().CreateBatch();
        var pending = batch.Execute(RespireCommands.Scripting.EVAL_RO, "return 42", 0);
        var result = await batch.TryExecuteAsync();
        await Assert.That(result.FailureCount).IsEqualTo(1);
        await Assert.That(pending.Error).IsTypeOf<RespireScriptingEngineUnavailableException>();
        await Assert.That(server.ReceivedCommands.Contains("INFO scriptingengines")).IsTrue();
    }

    [Test]
    [Arguments(RespireClientTrackingMode.OptIn, false)]
    [Arguments(RespireClientTrackingMode.OptIn, true)]
    [Arguments(RespireClientTrackingMode.OptOut, false)]
    [Arguments(RespireClientTrackingMode.OptOut, true)]
    [Arguments(RespireClientTrackingMode.Broadcast, true)]
    public async Task HandshakeAndCachedReadsUseSelectedMode(RespireClientTrackingMode mode, bool noLoop)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            ClientSideCache = new() { TrackingMode = mode, NoLoop = noLoop, KeyPrefixes = ["hot:"] },
        });
        await Assert.That(await client.GetStringAsync("hot:key")).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("hot:key")).IsEqualTo("value");
        var registration = mode switch
        {
            RespireClientTrackingMode.Broadcast => "BCAST PREFIX hot:",
            RespireClientTrackingMode.OptOut => "OPTOUT",
            _ => "OPTIN",
        };
        await Assert.That(server.ReceivedCommands.Contains("CLIENT TRACKING ON " + registration + (noLoop ? " NOLOOP" : ""))).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET hot:key")).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command == "CLIENT CACHING YES"))
            .IsEqualTo(mode == RespireClientTrackingMode.OptIn ? 1 : 0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task BypassPairsNoWithEachReadAndDoesNotPopulateCache(bool raw, bool retry)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            RetryPolicy = retry ? new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero } : null,
        });
        var bypass = client.WithKeyPrefix("hot:").WithoutClientCache();
        for (var i = 0; i < 2; i++)
        {
            if (raw)
            {
                using var reply = await client.WithoutClientCache().ExecuteAsync(RespireCommands.String.GET, "hot:key");
                await Assert.That(reply.AsString()).IsEqualTo("value");
            }
            else await Assert.That(await bypass.GetStringAsync("key")).IsEqualTo("value");
        }
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await client.GetStringAsync("hot:key");
        await client.GetStringAsync("hot:key");
        await Assert.That(server.ReceivedCommands.ToArray()).IsEquivalentTo(new[]
        {
            "HELLO 3", "CLIENT TRACKING ON OPTOUT",
            "CLIENT CACHING NO", "GET hot:key", "CLIENT CACHING NO", "GET hot:key", "GET hot:key",
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StreamedBypassPairsNoWithReadAndDrainsPayload(bool circuit)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            CircuitBreaker = circuit ? new() : null,
        });
        await using (var stream = await client.WithKeyPrefix("hot:").WithoutClientCache().Strings.GetStreamAsync("key"))
        {
            using var received = new MemoryStream();
            await stream!.CopyToAsync(received);
            await Assert.That(Encoding.UTF8.GetString(received.ToArray())).IsEqualTo("value");
        }
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync("hot:key")).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.SequenceEqual(new[]
        {
            "HELLO 3", "CLIENT TRACKING ON OPTOUT", "CLIENT CACHING NO", "GET hot:key", "GET hot:key",
        })).IsTrue();
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(16, false)]
    [Arguments(16, true)]
    public async Task StandaloneBypassBatchPairsNoWithEveryReadOnly(int capacity, bool circuit)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            MaxInflightCommands = capacity, CircuitBreaker = circuit ? new() : null,
        });
        using var batch = client.WithKeyPrefix("hot:").WithoutClientCache().CreateBatch();
        var first = batch.GetString("first");
        _ = batch.Strings.Set("written", "new");
        var second = batch.GetString("second");
        await batch.ExecuteAsync();
        await Assert.That(first.Result).IsEqualTo("value");
        await Assert.That(second.Result).IsEqualTo("value");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync("hot:kept")).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.SequenceEqual(new[]
        {
            "HELLO 3", "CLIENT TRACKING ON OPTOUT", "CLIENT CACHING NO", "GET hot:first",
            "SET hot:written new", "CLIENT CACHING NO", "GET hot:second", "GET hot:kept",
        })).IsTrue();
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(0, true)]
    [Arguments(1, false)]
    [Arguments(1, true)]
    [Arguments(2, false)]
    [Arguments(2, true)]
    public async Task FireAndForgetBypassPairsNoWithRead(int surface, bool circuit)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            CircuitBreaker = circuit ? new() : null,
        });
        var bypass = client.WithoutClientCache();
        RespireKey key = "hot:key";
        if (surface == 0) await bypass.ExecuteFireAndForgetAsync(RespireCommands.String.GET, key);
        else if (surface == 1) await bypass.ExecuteFireAndForgetAsync("GET", key);
        else await bypass.ExecuteFireAndForgetAsync($"GET {key}");
        // The awaited read is also a FIFO barrier for the discarded reply and its prelude.
        await Assert.That(await client.GetStringAsync("kept")).IsEqualTo("value");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.SequenceEqual(new[]
        {
            "HELLO 3", "CLIENT TRACKING ON OPTOUT", "CLIENT CACHING NO", "GET hot:key", "GET kept",
        })).IsTrue();
    }

    [Test]
    public async Task NoLoopBypassWriteFencesReadStartedWhileWriteIsPending()
    {
        var writeArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            ClientSideCache = new() { TrackingMode = RespireClientTrackingMode.OptOut, NoLoop = true },
        });
        server.MinimumCommandsBeforeReply = 2;
        server.SuppressReply = command =>
        {
            if (command == "SET key new") writeArrived.TrySetResult();
            return false;
        };
        var write = client.WithoutClientCache().SetAsync("key", "new").AsTask();
        await writeArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var read = client.GetStringAsync("key").AsTask();
        await write.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(await read.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("value");
        // No invalidation pushes are sent: NOLOOP requires the local mutation fence to
        // reject a read token acquired after eager eviction but before the write completed.
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        server.MinimumCommandsBeforeReply = 1;
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(2);
    }

    [Test]
    public async Task BypassRetryRepeatsNoAndReadTogether()
    {
        var calls = 0;
        await using var server = Server();
        server.ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
            ? Interlocked.Increment(ref calls) == 1 ? "-TRYAGAIN retry\r\n"u8.ToArray() : Value
            : command.StartsWith("HELLO ", StringComparison.Ordinal) ? Hello : FakeRespServer.OkReply;
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            RetryPolicy = new() { MaxAttempts = 2, InitialBackoff = TimeSpan.Zero },
        });
        await Assert.That(await client.WithoutClientCache().GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.Count(command => command == "CLIENT CACHING NO")).IsEqualTo(2);
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BypassPropagatesPreludeAndReadErrorsAndDrainsReplies(bool preludeError)
    {
        await using var server = new FakeRespServer(Hello, FakeRespServer.OkReply,
            preludeError ? "-ERR caching denied\r\n"u8.ToArray() : FakeRespServer.OkReply,
            preludeError ? Value : "-WRONGTYPE invalid type\r\n"u8.ToArray(), FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(Options(server));
        await Assert.That(async () => await client.WithoutClientCache().GetStringAsync("key"))
            .ThrowsExactly<RespireServerException>();
        await client.PingAsync();
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentBypassAndCachedReadsKeepPreludeAdjacentToBypass()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var bypass = client.WithoutClientCache();
        await Task.WhenAll(Enumerable.Range(0, 40).Select(async i =>
        {
            var reader = i % 2 == 0 ? bypass : client;
            await reader.GetStringAsync((i % 2 == 0 ? "skip:" : "keep:") + i);
        }));
        var commands = server.ReceivedCommands.ToArray();
        for (var i = 0; i < commands.Length; i++)
        {
            if (commands[i].StartsWith("GET skip:", StringComparison.Ordinal))
                await Assert.That(commands[i - 1]).IsEqualTo("CLIENT CACHING NO");
            if (commands[i].StartsWith("GET keep:", StringComparison.Ordinal))
                await Assert.That(commands[i - 1] == "CLIENT CACHING NO").IsFalse();
        }
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(20);
    }

    [Test]
    [Arguments("ASK", false)]
    [Arguments("ASK", true)]
    [Arguments("MOVED", false)]
    [Arguments("MOVED", true)]
    public async Task RedirectsPreserveOptOutRegistrationAndPerCallBypass(string redirect, bool bypass)
    {
        var slot = ClusterHash.GetSlot("key");
        await using var target = Server();
        await using var initial = Server();
        initial.ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
            ? Encoding.ASCII.GetBytes($"-{redirect} {slot} 127.0.0.1:{target.Port}\r\n")
            : command.StartsWith("HELLO ", StringComparison.Ordinal) ? Hello : FakeRespServer.OkReply;
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{initial.Port}\r\n");
        await using var seed = new FakeRespServer(topology)
        {
            ReplyOverride = (_, command) => command.StartsWith("HELLO ", StringComparison.Ordinal) ? Hello
                : command.StartsWith("CLIENT ", StringComparison.Ordinal) ? FakeRespServer.OkReply : topology,
        };
        await using var client = await RespireClient.ConnectAsync(Options(seed) with
        {
            UseCluster = true, RetryPolicy = new() { MaxAttempts = 2 },
        });
        var reader = bypass ? client.WithoutClientCache() : client;
        await Assert.That(await reader.GetStringAsync("key")).IsEqualTo("value");
        var commands = target.ReceivedCommands.ToArray();
        await Assert.That(commands.Contains("CLIENT TRACKING ON OPTOUT")).IsTrue();
        await Assert.That(commands.Contains("CLIENT CACHING YES")).IsFalse();
        await Assert.That(commands.Contains("CLIENT CACHING NO")).IsEqualTo(bypass);
        var readIndex = Array.IndexOf(commands, "GET key");
        if (bypass) await Assert.That(commands[readIndex - 1]).IsEqualTo("CLIENT CACHING NO");
        if (redirect == "ASK") await Assert.That(commands[readIndex - (bypass ? 2 : 1)]).IsEqualTo("ASKING");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(bypass ? 0 : 1);
    }

    [Test]
    public async Task BypassPreservesReplicaRoutingWithoutSendingCachingToUntrackedConnections()
    {
        await using var primary = Server();
        await using var replica = Server();
        replica.ReplyOverride = (_, command) => command == "ROLE"
            ? "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray()
            : command.StartsWith("HELLO ", StringComparison.Ordinal) ? Hello
            : command.StartsWith("GET ", StringComparison.Ordinal) ? Value : FakeRespServer.OkReply;
        await using var client = await RespireClient.ConnectAsync(Options(primary) with
        {
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        var bypass = client.WithReadFrom(RespireReadFrom.Replica).WithoutClientCache();
        await Assert.That(await bypass.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(replica.ReceivedCommands.Contains("GET key")).IsTrue();
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("CLIENT CACHING", StringComparison.Ordinal))).IsFalse();
        await Assert.That(primary.ReceivedCommands.Contains("GET key")).IsFalse();
    }

    [Test]
    public async Task AskingAndOptOutPreludeAreOneValidatedSequence()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, FakeRespServer.OkReply, Value);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var get = new Cmd1(Verbs.Get, "key");
        using var reply = await ClusterRouter.SendTrackedAskingAsync(connection, in get, default, optOut: true);
        await Assert.That(reply.AsString()).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands.ToArray()).IsEquivalentTo(new[] { "ASKING", "CLIENT CACHING NO", "GET key" });
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task StreamingAskingAndOptOutValidateBothPreludesAndDrainReplies(int failingPrelude)
    {
        await using var server = new FakeRespServer(
            failingPrelude == 1 ? "-ERR asking denied\r\n"u8.ToArray() : FakeRespServer.OkReply,
            failingPrelude == 2 ? "-ERR caching denied\r\n"u8.ToArray() : FakeRespServer.OkReply,
            Value, FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var asking = new ProtocolCommand<Cmd>(new Cmd(new Verb(-1, "ASKING")));
        var caching = new ClientCachingCommand(optOut: true);
        var get = new Cmd1(Verbs.Get, "key");
        if (failingPrelude == 0)
        {
            await using var stream = await connection.SendPrefixedBulkStreamAsync(asking, caching, get);
            using var text = new StreamReader(stream!);
            await Assert.That(await text.ReadToEndAsync()).IsEqualTo("value");
        }
        else
        {
            await Assert.That(async () => await connection.SendPrefixedBulkStreamAsync(asking, caching, get))
                .ThrowsExactly<RespireServerException>();
        }
        using var pong = await connection.SendAsync(new Cmd(new Verb(-1, "PING")));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(server.ReceivedCommands.SequenceEqual(new[]
        {
            "ASKING", "CLIENT CACHING NO", "GET key", "PING",
        })).IsTrue();
    }

    [Test]
    [Arguments("ASK")]
    [Arguments("MOVED")]
    public async Task StreamingRedirectsKeepOptOutBypassOnTarget(string redirect)
    {
        await using var target = Server();
        await using var initial = Server();
        initial.ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
            ? Encoding.ASCII.GetBytes($"-{redirect} {ClusterHash.GetSlot("key")} 127.0.0.1:{target.Port}\r\n")
            : command.StartsWith("HELLO ", StringComparison.Ordinal) ? Hello : FakeRespServer.OkReply;
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{initial.Port}\r\n");
        await using var seed = new FakeRespServer(topology)
        {
            ReplyOverride = (_, command) => command.StartsWith("HELLO ", StringComparison.Ordinal) ? Hello
                : command.StartsWith("CLIENT ", StringComparison.Ordinal) ? FakeRespServer.OkReply : topology,
        };
        await using var client = await RespireClient.ConnectAsync(Options(seed) with
        {
            UseCluster = true, RetryPolicy = new() { MaxAttempts = 2 },
        });
        await using var stream = await client.WithoutClientCache().Strings.GetStreamAsync("key");
        using var text = new StreamReader(stream!);
        await Assert.That(await text.ReadToEndAsync()).IsEqualTo("value");
        var commands = target.ReceivedCommands.ToArray();
        var readIndex = Array.IndexOf(commands, "GET key");
        await Assert.That(commands[readIndex - 1]).IsEqualTo("CLIENT CACHING NO");
        if (redirect == "ASK") await Assert.That(commands[readIndex - 2]).IsEqualTo("ASKING");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StreamingTwoPreludeSequenceRejectsInsufficientCapacityBeforeWriting()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { MaxInflightCommands = 2 });
        var asking = new ProtocolCommand<Cmd>(new Cmd(new Verb(-1, "ASKING")));
        var caching = new ClientCachingCommand(optOut: true);
        var get = new Cmd1(Verbs.Get, "key");
        await Assert.That(async () => await connection.SendPrefixedBulkStreamAsync(asking, caching, get)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5))).ThrowsExactly<InvalidOperationException>();
        using var pong = await connection.SendAsync(new Cmd(new Verb(-1, "PING")));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(server.ReceivedCommands.SequenceEqual(new[] { "PING" })).IsTrue();
    }

    [Test]
    [Arguments(false, 1)]
    [Arguments(true, 2)]
    public async Task OptOutReservesSpaceForBypassPreludes(bool cluster, int capacity)
    {
        await Assert.That(() => RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("localhost", 1)], UseCluster = cluster, MaxInflightCommands = capacity,
            ClientSideCache = new() { TrackingMode = RespireClientTrackingMode.OptOut },
        })).ThrowsExactly<RespireConfigurationException>();
    }

    private static FakeRespServer Server() => new(Hello)
    {
        ReplyOverride = (_, command) => command.StartsWith("HELLO ", StringComparison.Ordinal) ? Hello
            : command.StartsWith("GET ", StringComparison.Ordinal) ? Value : FakeRespServer.OkReply,
    };

    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
        MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        ClientSideCache = new() { TrackingMode = RespireClientTrackingMode.OptOut },
    };
}
