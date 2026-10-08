using System.Reflection;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CommandCatalogTests
{
    [Test]
    public async Task BuiltInReadVerbsRequireAuditedReadOnlyCatalogMetadata()
    {
        var catalog = RespireCommands.All.ToArray().ToDictionary(command => command.Name);
        foreach (var field in typeof(Verbs).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType != typeof(Verb)) continue;
            var verb = (Verb)field.GetValue(null)!;
            var words = System.Text.Encoding.ASCII.GetString(verb.Bulk).Split("\r\n")
                .Where((_, index) => (index & 1) != 0);
            var name = string.Join(' ', words);
            if (!catalog.TryGetValue(name, out var descriptor))
            {
                // Some pre-encoded verbs include options, such as SCRIPT FLUSH SYNC.
                await Assert.That(verb.ReadKind).IsEqualTo(ReadCommandKind.None);
                continue;
            }
            await Assert.That(descriptor.ReadKind).IsEqualTo(verb.ReadKind);
            if (verb.ReadKind != ReadCommandKind.None)
                await Assert.That(descriptor.IsReadOnly).IsTrue();
        }
        await Assert.That(Verbs.Touch.ReadKind).IsEqualTo(ReadCommandKind.None);
        await Assert.That(catalog["TOUCH"].ReadKind).IsEqualTo(ReadCommandKind.None);
        await Assert.That(Verbs.MemoryUsage.ReadKind).IsEqualTo(ReadCommandKind.Read);
        await Assert.That(Verbs.EvalRo.ReadKind).IsEqualTo(ReadCommandKind.Read);
        await Assert.That(Verbs.EvalShaRo.ReadKind).IsEqualTo(ReadCommandKind.Read);
    }

    [Test]
    public async Task EveryAuditedCommandUsesTheSameTypedAndRawReadClassification()
    {
        foreach (var descriptor in RespireCommands.All.ToArray())
        {
            var typed = new Verb(descriptor.Name);
            await Assert.That(typed.ReadKind).IsEqualTo(descriptor.ReadKind);
            await Assert.That(typed.CursorArgumentIndex).IsEqualTo(descriptor.CursorArgumentIndex);
            await Assert.That(Internal.RawCommandDescriptorLookup.GetReadKind(descriptor.Name.ToLowerInvariant()))
                .IsEqualTo(descriptor.ReadKind);
            await Assert.That(RespireCommand.Create(descriptor.Name.Split(' ')[0]).ReadKind)
                .IsEqualTo(ReadCommandKind.None);
        }
        await Assert.That(new Verb("CUSTOM.READ").ReadKind).IsEqualTo(ReadCommandKind.None);
        await Assert.That(new Verb(2, "EVAL_RO").RoutingKeyIndex).IsEqualTo(2);
        await Assert.That(new Verb(1, "SINTERCARD").RoutingKeyIndex).IsEqualTo(1);
        await Assert.That(new Verb(0, "MEMORY", "USAGE").RoutingKeyIndex).IsEqualTo(0);
    }

    [Test]
    [NotInParallel]
    public async Task SharedReadClassificationAllocatesNothingAfterInitialization()
    {
        var command = new Cmd1(Verbs.Get, "key");
        _ = MeasureReadClassification(in command, false);
        _ = MeasureReadClassification(in command, true);
        var measurements = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Normal: MeasureReadClassification(in command, false),
                Control: MeasureReadClassification(in command, true)));
        await Assert.That(measurements.Normal.Bytes).IsEqualTo(0);
        await Assert.That(measurements.Control.Bytes).IsGreaterThanOrEqualTo(37_000);
        await Assert.That(measurements.Normal.Total).IsEqualTo(5_000);
        await Assert.That(measurements.Control.Total).IsEqualTo(5_000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Bytes, int Total) MeasureReadClassification(in Cmd1 command, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var total = 0;
        for (var index = 0; index < 1_000; index++)
        {
            total += (int)command.ReadKind;
            total += (int)Internal.RawCommandDescriptorLookup.GetReadKind("get");
            total += (int)CommandReadMetadata.Get("HSCAN").Kind;
            total += CommandReadMetadata.Get("HSCAN").CursorArgumentIndex;
            total += (int)Internal.RawCommandDescriptorLookup.GetReadKind("CUSTOM.READ");
            if (allocate) GC.KeepAlive(new byte[37]);
        }
        return (GC.GetAllocatedBytesForCurrentThread() - before, total);
    }

    [Test]
    [Arguments("GET", true)]
    [Arguments("HGET", true)]
    [Arguments("EVAL_RO", true)]
    [Arguments("EVALSHA_RO", true)]
    [Arguments("FCALL_RO", true)]
    [Arguments("XREAD", true)]
    [Arguments("SET", false)]
    [Arguments("GETEX", false)]
    [Arguments("EVAL", false)]
    [Arguments("EVALSHA", false)]
    [Arguments("FCALL", false)]
    [Arguments("XREADGROUP", false)]
    [Arguments("SORT", false)]
    [Arguments("SORT_RO", true)]
    [Arguments("JSON.GET", false)]
    [Arguments("KEYDB.NHGET", false)]
    [Arguments("READONLY", false)]
    public async Task CatalogReadOnlyMetadataRequiresAuthoritativeFlags(string name, bool expected)
    {
        var descriptor = RespireCommands.All.ToArray().Single(command => command.Name == name);
        await Assert.That(descriptor.IsReadOnly).IsEqualTo(expected);
        RespireCommand callerSupplied = name;
        await Assert.That(callerSupplied.IsReadOnly).IsFalse();
    }

    [Test]
    public async Task ReadOnlyMetadataPreservesBehaviorAndDescriptorFootprint()
    {
        await Assert.That(default(RespireCommand).IsReadOnly).IsFalse();
        await Assert.That(RespireCommands.Stream.XREAD.Behavior)
            .IsEqualTo(RespireCommandBehavior.BlockingWhenRequested);
        await Assert.That(RespireCommands.Cluster.READONLY.Behavior)
            .IsEqualTo(RespireCommandBehavior.ConnectionScoped);
        // The original descriptor held a Verb, name reference, source flags and an int behavior.
        await Assert.That(Unsafe.SizeOf<RespireCommand>())
            .IsEqualTo(Unsafe.SizeOf<Verb>() + IntPtr.Size + 2 * sizeof(int));
    }

    [Test]
    public async Task CreatePreservesOneArgumentOverloadAndKeepsCallerMetadataUnaudited()
    {
        var oneArgumentOverload = typeof(RespireCommand).GetMethod(
            nameof(RespireCommand.Create), [typeof(string)]);
        await Assert.That(oneArgumentOverload).IsNotNull();

        var callerCommand = RespireCommand.Create("CUSTOM.GET", RespireCacheMutation.ReadOnly);
        await Assert.That(callerCommand.CacheMutation).IsEqualTo(RespireCacheMutation.ReadOnly);
        await Assert.That(callerCommand.IsReadOnly).IsFalse();
        await Assert.That(callerCommand.Sources).IsEqualTo(RespireCommandSource.None);

        var explicitUnknown = new CatalogCommand(RespireCommand.Create("GET", RespireCacheMutation.Unknown), ["key"]);
        var inferred = new CatalogCommand(RespireCommand.Create("GET"), ["key"]);
        await Assert.That(explicitUnknown.GetCacheMutation("GET")).IsEqualTo(RespireCacheMutation.Unknown);
        await Assert.That(inferred.GetCacheMutation("GET")).IsEqualTo(RespireCacheMutation.ReadOnly);
    }

    [Test]
    public async Task Catalog_ContainsEveryAuditedCommandExactlyOnce()
    {
        var commands = RespireCommands.All.ToArray();

        await Assert.That(commands.Length).IsEqualTo(645);
        await Assert.That(commands.Select(static command => command.Name).Distinct(StringComparer.Ordinal).Count())
            .IsEqualTo(commands.Length);
        await Assert.That(commands.Count(static command => command.Sources.HasFlag(RespireCommandSource.Redis)))
            .IsEqualTo(598);
        await Assert.That(commands.Count(static command => command.Sources.HasFlag(RespireCommandSource.Valkey)))
            .IsEqualTo(464);
        await Assert.That(commands.Count(static command => command.Sources.HasFlag(RespireCommandSource.KeyDb)))
            .IsEqualTo(9);
        await Assert.That(commands.Count(static command => command.Sources.HasFlag(RespireCommandSource.Dragonfly)))
            .IsEqualTo(18);
        // IsCallerSupplied keys off the pre-encoded verb because RespireCommand.Create also uses
        // RespireCommandSource.None; every catalog entry must still be pre-encoded with a source.
        await Assert.That(commands.Where(static command =>
                command.IsCallerSupplied || command.Sources == RespireCommandSource.None))
            .IsEmpty();
    }

    [Test]
    public async Task CompatibleServerExtensionsHaveExactNamesAndProvenance()
    {
        var commands = RespireCommands.All.ToArray();
        await Assert.That(commands.Where(command => command.Sources == RespireCommandSource.Dragonfly)
            .Select(command => command.Name)).IsEquivalentTo(new[]
        {
            "STICK", "CL.THROTTLE", "SADDEX", "FIELDEXPIRE", "FIELDTTL", "RM",
            "SCRIPT LATENCY", "SCRIPT LIST", "DFLYCLUSTER CONFIG", "DFLYCLUSTER FLUSHSLOTS",
            "DFLYCLUSTER GETSLOTINFO", "DFLYCLUSTER SLOT-MIGRATION-STATUS", "MEMORY ARENA",
            "MEMORY DECOMMIT", "MEMORY DEFRAGMENT", "CF.COMPACT", "JSON.DEBUG FIELDS", "JSON.DEBUG HELP",
        });
        await Assert.That(commands.Where(command => command.Sources == RespireCommandSource.KeyDb)
            .Select(command => command.Name)).IsEquivalentTo(new[]
        {
            "EXPIREMEMBER", "EXPIREMEMBERAT", "PEXPIREMEMBERAT", "KEYDB.CRON", "KEYDB.HRENAME",
            "KEYDB.MEXISTS", "KEYDB.NHGET", "KEYDB.NHSET", "REPLPING",
        });
    }

    [Test]
    public async Task CompatibleServerCatalogPreservesBinaryArgumentsAndReplyShapes()
    {
        await using var server = new FakeRespServer("*5\r\n:0\r\n:1\r\n:0\r\n:-1\r\n:10\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(), FakeRespServer.OkReply, "*0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var throttle = await client.ExecuteAsync(RespireCommands.Dragonfly.CL_THROTTLE, "user 42", 0, 1, 10, 1);
        await Assert.That(throttle[0].AsInteger()).IsEqualTo(0);
        await Assert.That(throttle[3].AsInteger()).IsEqualTo(-1);
        byte[] member = [0xff, 0, 0x80];
        using var added = await client.ExecuteAsync(RespireCommands.Dragonfly.SADDEX, "set", 30, member);
        await Assert.That(added.AsInteger()).IsEqualTo(1);
        using var expires = await client.ExecuteAsync(RespireCommands.KeyDb.PEXPIREMEMBERAT, "set", member, 2000000000000L);
        await Assert.That(expires.AsString()).IsEqualTo("OK");
        using var migration = await client.ExecuteAsync(RespireCommands.Dragonfly.DFLYCLUSTER_SLOT_MIGRATION_STATUS);
        await Assert.That(server.ReceivedArguments[0][1]).IsEquivalentTo("user 42"u8.ToArray(), CollectionOrdering.Matching);
        await Assert.That(server.ReceivedArguments[1][3]).IsEquivalentTo(member, CollectionOrdering.Matching);
        await Assert.That(server.ReceivedArguments[2][2]).IsEquivalentTo(member, CollectionOrdering.Matching);
        await Assert.That(server.ReceivedCommands[^1]).IsEqualTo("DFLYCLUSTER SLOT-MIGRATION-STATUS");
    }

    [Test]
    public async Task VendorAdministrationDoesNotRouteConfigurationOrCursorsAsKeys()
    {
        RespireCommand[] unkeyed =
        [
            RespireCommands.Dragonfly.DFLYCLUSTER_CONFIG, RespireCommands.Dragonfly.DFLYCLUSTER_FLUSHSLOTS,
            RespireCommands.Dragonfly.DFLYCLUSTER_GETSLOTINFO, RespireCommands.Dragonfly.DFLYCLUSTER_SLOT_MIGRATION_STATUS,
            RespireCommands.Dragonfly.MEMORY_ARENA, RespireCommands.Dragonfly.MEMORY_DECOMMIT,
            RespireCommands.Dragonfly.MEMORY_DEFRAGMENT, RespireCommands.Dragonfly.RM,
            RespireCommands.Dragonfly.SCRIPT_LIST, RespireCommands.Dragonfly.SCRIPT_LATENCY,
            RespireCommands.Dragonfly.JSON_DEBUG_HELP,
        ];
        foreach (var descriptor in unkeyed)
        {
            var command = new CatalogCommand(descriptor, ["not-a-key"]);
            await Assert.That(command.TryGetPrimaryKey(out _)).IsFalse();
            await Assert.That(command.TryGetClusterSlot(out _)).IsFalse();
        }
        RespireCommand[] keyed =
        [
            RespireCommands.Dragonfly.CL_THROTTLE, RespireCommands.Dragonfly.SADDEX,
            RespireCommands.Dragonfly.FIELDEXPIRE, RespireCommands.Dragonfly.FIELDTTL,
            RespireCommands.Dragonfly.CF_COMPACT, RespireCommands.Dragonfly.JSON_DEBUG_FIELDS,
            RespireCommands.KeyDb.PEXPIREMEMBERAT, RespireCommands.KeyDb.KEYDB_HRENAME,
            RespireCommands.KeyDb.KEYDB_MEXISTS, RespireCommands.KeyDb.KEYDB_NHGET, RespireCommands.KeyDb.KEYDB_NHSET,
        ];
        foreach (var descriptor in keyed)
        {
            var command = new CatalogCommand(descriptor, ["{key}:data", "value"]);
            await Assert.That(command.TryGetClusterSlot(out var slot)).IsTrue();
            await Assert.That(slot).IsEqualTo(new RespireKey("{key}:data").ClusterSlot);
        }
    }

    [Test]
    [Arguments(1024)]
    [Arguments(1025)]
    public async Task EveryCatalogFrameBoundCoversBoundaryArguments(int textLength)
    {
        RespireValue[] arguments = [new string('é', textLength - 1) + "\uD800",
            long.MinValue, new byte[] { 0, 255, 13, 10 }];
        foreach (var descriptor in RespireCommands.All.ToArray())
        {
            var command = new CatalogCommand(descriptor, arguments);
            var bound = command.GetWriteSizeHint();
            if (textLength == 1024) await Assert.That(bound).IsGreaterThan(0);
            else await Assert.That(bound).IsEqualTo(0);
            var buffer = new WriteBuffer(16);
            try
            {
                var writer = new RespWriter(buffer, bound);
                command.Write(ref writer);
                writer.Complete();
                if (bound > 0) await Assert.That(buffer.Count).IsLessThanOrEqualTo(bound);
                var position = 0;
                var status = RespParser.TryParseValue(buffer.WrittenMemory.Span, ref position, out var frame);
                using (frame)
                {
                    await Assert.That(status).IsEqualTo(RespParseStatus.Done);
                    await Assert.That(position).IsEqualTo(buffer.Count);
                    var elements = frame.AsArray().ToArray();
                    var words = descriptor.Name.Split(' ');
                    await Assert.That(elements.Length).IsEqualTo(words.Length + arguments.Length);
                    for (var index = 0; index < words.Length; index++)
                        await Assert.That(elements[index].AsString()).IsEqualTo(words[index]);
                    for (var index = 0; index < arguments.Length; index++)
                    {
                        var expected = new byte[arguments[index].GetWireLength()];
                        arguments[index].WriteWirePayload(expected);
                        await Assert.That(elements[words.Length + index].AsSpan().SequenceEqual(expected)).IsTrue();
                    }
                }
            }
            finally { buffer.Release(); }
        }
    }

    [Test]
    public async Task EveryCatalogCommand_SerializesItsExactCommandWords()
    {
        var commands = RespireCommands.All.ToArray();
        foreach (var command in commands)
        {
            var buffer = new WriteBuffer(64);
            try
            {
                var writer = new RespWriter(buffer);
                new CatalogCommand(command, []).Write(ref writer);
                writer.Complete();
                var position = 0;
                var status = RespParser.TryParseValue(buffer.WrittenMemory.Span, ref position, out var frame);
                try
                {
                    await Assert.That(status).IsEqualTo(RespParseStatus.Done);
                    await Assert.That(position).IsEqualTo(buffer.Count);
                    var elements = frame.AsArray();
                    var actualWords = new string[elements.Length];
                    for (var index = 0; index < elements.Length; index++)
                    {
                        actualWords[index] = elements[index].AsString();
                    }

                    var words = command.Name.Split(' ');
                    await Assert.That(actualWords.Length).IsEqualTo(words.Length);
                    for (var index = 0; index < words.Length; index++)
                    {
                        await Assert.That(actualWords[index]).IsEqualTo(words[index]);
                    }
                }
                finally
                {
                    frame.Dispose();
                }
            }
            finally
            {
                buffer.Release();
            }
        }
    }

    [Test]
    public async Task Catalog_ClassifiesCommandsThatCannotUseTheMultiplexedPath()
    {
        var commands = RespireCommands.All.ToArray();
        var blocking = commands
            .Where(static command => command.Behavior == RespireCommandBehavior.Blocking)
            .Select(static command => command.Name)
            .ToArray();
        var connectionScoped = commands
            .Where(static command => command.Behavior == RespireCommandBehavior.ConnectionScoped)
            .Select(static command => command.Name)
            .ToArray();

        await Assert.That(blocking).IsEquivalentTo(new[]
        {
            "BLMOVE", "BLMOVEM", "BLMPOP", "BLPOP", "BRPOP", "BRPOPLPUSH", "BZMPOP", "BZPOPMAX", "BZPOPMIN",
        });
        await Assert.That(connectionScoped).IsEquivalentTo(new[]
        {
            "ASKING", "AUTH", "CLIENT", "CLIENT CACHING", "CLIENT CAPA", "CLIENT GETNAME", "CLIENT GETREDIR",
            "CLIENT ID", "CLIENT IMPORT-SOURCE", "CLIENT INFO", "CLIENT MAINT_NOTIFICATIONS", "CLIENT NO-EVICT",
            "CLIENT NO-TOUCH", "CLIENT REPLY", "CLIENT SETINFO", "CLIENT SETNAME", "CLIENT TRACKING",
            "CLIENT TRACKINGINFO", "DISCARD", "EXEC", "HELLO", "MONITOR", "MULTI", "PSUBSCRIBE", "PSYNC",
            "PUNSUBSCRIBE", "QUIT", "READONLY", "READWRITE", "REPLCONF", "RESET", "SCRIPT", "SCRIPT DEBUG",
            "SELECT", "SSUBSCRIBE", "SUBSCRIBE", "SUNSUBSCRIBE", "SYNC", "UNSUBSCRIBE", "UNWATCH", "WAIT",
            "WAITAOF", "WATCH",
        });
        await Assert.That(RespireCommands.Stream.XREAD.IsBlocking(["STREAMS", "source", "0"]))
            .IsFalse();
        await Assert.That(RespireCommands.Stream.XREAD.IsBlocking(["block", 1000, "STREAMS", "source", "0"]))
            .IsTrue();
        await Assert.That(RespireCommands.Stream.XREAD.IsBlocking(["STREAMS", "BLOCK", "0"]))
            .IsFalse();
        await Assert.That(RespireCommands.Stream.XREADGROUP.IsBlocking(
                ["GROUP", "workers", "consumer", "BLOCK"u8.ToArray(), 1000, "STREAMS", "source", ">"]))
            .IsTrue();
        await Assert.That(RespireCommands.Stream.XREADGROUP.IsBlocking(
                ["GROUP", "BLOCK", "BLOCK", "STREAMS", "BLOCK", ">"]))
            .IsFalse();
        await Assert.That(RespireCommands.TimeSeries.TS_READ.IsBlocking(["BLOCK", 0]))
            .IsFalse();
        await Assert.That(RespireCommands.TimeSeries.TS_READ.IsBlocking(["series", 0, "BLOCK", 100, 1]))
            .IsTrue();
    }

    [Test]
    public async Task ConnectionScopedCatalogCommands_AreRejectedBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        foreach (var command in RespireCommands.All.ToArray().Where(
                     static command => command.Behavior == RespireCommandBehavior.ConnectionScoped))
        {
            await Assert.That(async () => await client.ExecuteAsync(command))
                .Throws<NotSupportedException>();
        }

        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task PreencodedSafeSubcommands_UseSubcommandAwareExecution()
    {
        await using var server = new FakeRespServer(
            "*1\r\n:1\r\n"u8.ToArray(),
            "$6\r\nstring\r\n"u8.ToArray(),
            "+OK\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        using (var exists = await client.ExecuteAsync(RespireCommands.Scripting.SCRIPT, "EXISTS", "sha1"))
        {
            await Assert.That(exists.Count).IsEqualTo(1);
        }

        using (var encoding = await client.ExecuteAsync(RespireCommands.Key.OBJECT, "ENCODING", "key"))
        {
            await Assert.That(encoding.AsString()).IsEqualTo("string");
        }

        using (var clients = await client.ExecuteAsync(RespireCommands.Connection.CLIENT, "LIST"))
        {
            await Assert.That(clients.AsString()).IsEqualTo("OK");
        }

        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "SCRIPT EXISTS sha1",
            "OBJECT ENCODING key",
            "CLIENT LIST",
        ]);
    }

    [Test]
    public async Task CatalogCommands_OnKeyPrefixedViews_AreRejectedBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var tenant = client.WithKeyPrefix("tenant:42:");

        await Assert.That(async () => await tenant.ExecuteAsync(RespireCommands.String.GET, "settings"))
            .Throws<NotSupportedException>();
        // Subcommand-normalized descriptors report the rejection through the returned task.
        var pending = tenant.ExecuteAsync(RespireCommand.Create("XGROUP"), "DESTROY", "stream", "group");
        await Assert.That(async () => await pending).Throws<NotSupportedException>();
        var pendingFireAndForget = tenant.ExecuteFireAndForgetAsync(
            RespireCommand.Create("XGROUP"), "DESTROY", "stream", "group");
        await Assert.That(async () => await pendingFireAndForget).Throws<NotSupportedException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task CatalogCommand_PreservesArgumentsAsSingleTokens()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        using var result = await client.ExecuteAsync(
            RespireCommands.Json.JSON_SET, "document", "$", "{\"message\":\"hello world\"}");

        await Assert.That(server.ReceivedCommands.Single())
            .IsEqualTo("JSON.SET document $ {\"message\":\"hello world\"}");
    }

    /// <summary>
    /// Compile-coverage for the collapsed execute surface: every shape below must bind through the
    /// interface alone, and a literal <c>0</c> must stay an argument rather than becoming
    /// <see cref="RespireCommandFlags.None"/>.
    /// </summary>
    [Test]
    public async Task ExecuteOverloads_BindWithoutAmbiguityThroughTheInterface()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var concrete = await FakeRespServer.ConnectClientAsync(server.Port);
        IRespireClient client = concrete;
        using var cancellation = new CancellationTokenSource();
        RespireValue interpolatedValue = "interpolated-value";

        using var rawZero = await client.ExecuteAsync("SET", 0, "raw-key");
        using var catalogZero = await client.ExecuteAsync(RespireCommands.String.SET, 0, "catalog-key");
        using var rawParams = await client.ExecuteAsync("SET", "raw-params", "value");
        using var catalogParams = await client.ExecuteAsync(
            RespireCommands.String.SET, "catalog-params", "value");
        using var rawFlags = await client.ExecuteAsync(
            "SET", ["raw-flags", "value"], RespireCommandFlags.NoRedirect);
        using var catalogFlags = await client.ExecuteAsync(
            RespireCommands.String.SET, ["catalog-flags", "value"], flags: RespireCommandFlags.NoRedirect);
        using var rawCancellation = await client.ExecuteAsync(
            "SET", ["raw-cancellation", "value"], cancellationToken: cancellation.Token);
        using var catalogCancellation = await client.ExecuteAsync(
            RespireCommands.String.SET, ["catalog-cancellation", "value"], cancellationToken: cancellation.Token);
        using var interpolated = await client.ExecuteAsync($"SET interpolated {interpolatedValue}");
        using var interpolatedCancellation = await client.ExecuteAsync(
            $"SET interpolated-cancellation {interpolatedValue}", cancellationToken: cancellation.Token);

        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "SET 0 raw-key",
            "SET 0 catalog-key",
            "SET raw-params value",
            "SET catalog-params value",
            "SET raw-flags value",
            "SET catalog-flags value",
            "SET raw-cancellation value",
            "SET catalog-cancellation value",
            "SET interpolated interpolated-value",
            "SET interpolated-cancellation interpolated-value",
        ]);
    }

    [Test]
    public async Task ExecuteSurface_UsesRespireCommandWithoutStringForwarders()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var concrete = await FakeRespServer.ConnectClientAsync(server.Port);
        IRespireClient client = concrete;
        RespireCommand raw = "CONFIG GET";

        foreach (var type in new[] { typeof(IRespireClient), typeof(RespireClient) })
        {
            var executeMethods = type.GetMethods(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(static method => method.Name is nameof(IRespireClient.ExecuteAsync)
                    or nameof(IRespireClient.ExecuteFireAndForgetAsync))
                .ToArray();
            var stringForwarders = executeMethods
                .Where(static method => method.GetParameters()[0].ParameterType == typeof(string))
                .ToArray();

            await Assert.That(executeMethods.Count(static method => method.Name == nameof(IRespireClient.ExecuteAsync)))
                .IsEqualTo(3);
            await Assert.That(executeMethods.Count(static method => method.Name == nameof(IRespireClient.ExecuteFireAndForgetAsync)))
                .IsEqualTo(3);
            await Assert.That(stringForwarders).IsEmpty();
        }
        await Assert.That(Enum.GetNames<RespireCommandFlags>()).IsEquivalentTo(["None", "NoRedirect"]);
        var noRedirect = Enum.GetValues<RespireCommandFlags>()
            .Single(static value => value.ToString() == "NoRedirect");
        await Assert.That(Convert.ToInt32(noRedirect)).IsEqualTo(1);
        await Assert.That(raw.Name).IsEqualTo("CONFIG GET");
        await Assert.That(raw.Sources).IsEqualTo(RespireCommandSource.None);
        await Assert.That(raw.Verb.Bulk).IsNull();
        await Assert.That(raw.Verb.Tokens).IsEqualTo(0);

        await client.ExecuteFireAndForgetAsync("SET", "raw-key", "value");
        await client.ExecuteFireAndForgetAsync(
            RespireCommands.String.SET, ["catalog-key", "value"], CancellationToken.None);
        await WaitForCommandsAsync(server, 2);

        await Assert.That(server.ReceivedCommands)
            .IsEquivalentTo(["SET raw-key value", "SET catalog-key value"]);
    }

    [Test]
    public async Task InterpolatedCommand_HonorsInvariantFormatsAndAlignment()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var number = 1234.5m;
        var left = 42;

        using var result = await client.ExecuteAsync($"SET formatted {number,12:N2} {left,-5:D4} {"é",3}");

        await Assert.That(server.ReceivedCommands.Single())
            .IsEqualTo("SET formatted     1,234.50 0042    é");
    }

    [Test]
    public async Task InterpolatedCommand_AlignmentPreservesSpecializedEncodings()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var bytes = "ab"u8.ToArray();
        ReadOnlyMemory<byte> memory = "cd"u8.ToArray();
        RespireValue binaryValue = new byte[] { 0xff, 0x00 };
        RespireKey binaryKey = new byte[] { 0xfe, 0x01 };

        using var booleanResult = await client.ExecuteAsync($"SET boolean {true,3}");
        using var bytesResult = await client.ExecuteAsync($"SET bytes {bytes,-4}");
        using var memoryResult = await client.ExecuteAsync($"SET memory {memory,4}");

        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "SET boolean   1",
            "SET bytes ab  ",
            "SET memory   cd",
        ]);

        var binaryTokens = BuildTokens($"SET binary {binaryValue,4} {binaryKey,-4}");
        await Assert.That(binaryTokens[2]).IsEqualTo((RespireValue)new byte[] { 0x20, 0x20, 0xff, 0x00 });
        await Assert.That(binaryTokens[3]).IsEqualTo((RespireValue)new byte[] { 0xfe, 0x01, 0x20, 0x20 });

        var formattedTokens = BuildTokens($"SET format {true:X} {bytes:X} {memory:X} {binaryValue:X} {binaryKey:X}");
        await Assert.That(formattedTokens[2]).IsEqualTo((RespireValue)new byte[] { (byte)'1' });
        await Assert.That(formattedTokens[3]).IsEqualTo((RespireValue)bytes);
        await Assert.That(formattedTokens[4]).IsEqualTo((RespireValue)memory);
        await Assert.That(formattedTokens[5]).IsEqualTo(binaryValue);
        await Assert.That(formattedTokens[6]).IsEqualTo(binaryKey.AsValue());

        static RespireValue[] BuildTokens(RespireCommandInterpolatedStringHandler handler)
            => handler.Build().Tokens;
    }

    [Test]
    public async Task InterpolatedFireAndForget_PreservesEmptyArgumentsThroughInterface()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var concrete = await FakeRespServer.ConnectClientAsync(server.Port);
        IRespireClient client = concrete;
        var empty = string.Empty;

        await client.ExecuteFireAndForgetAsync($"SET {empty} x");
        await WaitForCommandsAsync(server, 1);

        await Assert.That(server.ReceivedCommands.Single()).IsEqualTo("SET  x");
    }

    [Test]
    public async Task RawFireAndForget_CompletesWithoutPendingResultAndDiscardsReply()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.PongReply)
        {
            MinimumCommandsBeforeReply = 2,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.ExecuteFireAndForgetAsync("SET", "key", "value")
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForCommandsAsync(server, 1);
        using var response = await client.ExecuteAsync("PING")
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(response.AsString()).IsEqualTo("PONG");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["SET key value", "PING"]);
    }

    [Test]
    public async Task RawFireAndForget_BlockingCommandsAreRejectedBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.ExecuteFireAndForgetAsync("blpop key 0"))
            .Throws<NotSupportedException>()
            .WithMessage("BLPOP can block and cannot run through ExecuteFireAndForgetAsync.");
        await Assert.That(async () => await client.ExecuteFireAndForgetAsync(
                "XREAD", "BLOCK", 0, "STREAMS", "events", "$"))
            .Throws<NotSupportedException>()
            .WithMessage("XREAD can block and cannot run through ExecuteFireAndForgetAsync.");
        await Assert.That(async () => await client.ExecuteFireAndForgetAsync(
                "XREAD BLOCK 0 STREAMS events $"))
            .Throws<NotSupportedException>()
            .WithMessage("XREAD can block and cannot run through ExecuteFireAndForgetAsync.");

        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task RawFireAndForget_BlockTokensAfterStreamsAreNotOptions()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.ExecuteFireAndForgetAsync("XREAD STREAMS BLOCK $");
        await client.ExecuteFireAndForgetAsync(
            "XREADGROUP GROUP", "BLOCK", "BLOCK", "STREAMS", "BLOCK", ">");
        await WaitForCommandsAsync(server, 2);

        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "XREAD STREAMS BLOCK $",
            "XREADGROUP GROUP BLOCK BLOCK STREAMS BLOCK >",
        ]);
    }

    [Test]
    public async Task RawCommand_CancellationOnlyOverloadSendsCommand()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        using var response = await client.ExecuteAsync("PING", [], cancellationToken: CancellationToken.None);

        await Assert.That(response.AsString()).IsEqualTo("PONG");
    }

    [Test]
    public async Task RawBlockingCommands_CancelDedicatedConnectionsWithoutStallingSharedTraffic()
    {
        await using var server = new FakeRespServer(3, FakeRespServer.PongReply)
        {
            SuppressReply = static command => command.StartsWith("BLPOP ", StringComparison.Ordinal),
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        using var rawCancellation = new CancellationTokenSource();
        var raw = client.ExecuteAsync(
            "BLPOP", ["raw-key", 0], cancellationToken: rawCancellation.Token).AsTask();
        await WaitForCommandsAsync(server, 1);
        rawCancellation.Cancel();
        await Assert.That(async () => await raw.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();

        using var interpolatedCancellation = new CancellationTokenSource();
        RespireKey interpolatedKey = "interpolated-key";
        var interpolated = client.ExecuteAsync(
            $"BLPOP {interpolatedKey} {0}", cancellationToken: interpolatedCancellation.Token).AsTask();
        await WaitForCommandsAsync(server, 2);
        interpolatedCancellation.Cancel();
        await Assert.That(async () => await interpolated.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();

        using var ping = await client.ExecuteAsync("PING")
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(ping.AsString()).IsEqualTo("PONG");
        await Assert.That(server.ReceivedCommands)
            .IsEquivalentTo(["BLPOP raw-key 0", "BLPOP interpolated-key 0", "PING"]);
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task RawFireAndForget_ConnectionScopedCommandsAreRejectedBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.ExecuteFireAndForgetAsync("CLIENT REPLY", "OFF"))
            .Throws<NotSupportedException>()
            .WithMessage(
                "CLIENT requires connection affinity and cannot run through ExecuteFireAndForgetAsync.");
        await Assert.That(async () => await client.ExecuteFireAndForgetAsync("client reply skip"))
            .Throws<NotSupportedException>()
            .WithMessage(
                "CLIENT requires connection affinity and cannot run through ExecuteFireAndForgetAsync.");
        await Assert.That(async () => await client.ExecuteFireAndForgetAsync("CLIENT UNKNOWN"))
            .Throws<NotSupportedException>()
            .WithMessage(
                "CLIENT requires connection affinity and cannot run through ExecuteFireAndForgetAsync.");
        await Assert.That(async () => await client.ExecuteFireAndForgetAsync("SCRIPT DEBUG", "YES"))
            .Throws<NotSupportedException>()
            .WithMessage(
                "SCRIPT DEBUG requires connection affinity and cannot run through ExecuteFireAndForgetAsync.");
        await Assert.That(async () => await client.ExecuteFireAndForgetAsync("SCRIPT", "DEBUG", "YES"))
            .Throws<NotSupportedException>()
            .WithMessage(
                "SCRIPT requires connection affinity and cannot run through ExecuteFireAndForgetAsync.");

        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task RawFireAndForget_SafeClientSubcommandsAreAccepted()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.ExecuteFireAndForgetAsync("CLIENT LIST");
        await client.ExecuteFireAndForgetAsync("CLIENT", "HELP");
        await client.ExecuteFireAndForgetAsync("CLIENT KILL", "ID", 42);
        await client.ExecuteFireAndForgetAsync("CLIENT", "UNBLOCK", 42);
        await client.ExecuteFireAndForgetAsync("CLIENT PAUSE", 100);
        await client.ExecuteFireAndForgetAsync("CLIENT", "UNPAUSE");
        await WaitForCommandsAsync(server, 6);

        await Assert.That(server.ReceivedCommands)
            .IsEquivalentTo([
                "CLIENT LIST",
                "CLIENT HELP",
                "CLIENT KILL ID 42",
                "CLIENT UNBLOCK 42",
                "CLIENT PAUSE 100",
                "CLIENT UNPAUSE",
            ]);
    }

    [Test]
    public async Task RawFireAndForget_SafeScriptSubcommandsAreAccepted()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.ExecuteFireAndForgetAsync("SCRIPT KILL");
        await client.ExecuteFireAndForgetAsync("SCRIPT EXISTS", "sha1");
        await client.ExecuteFireAndForgetAsync("SCRIPT HELP");
        await client.ExecuteFireAndForgetAsync("SCRIPT SHOW", "sha1");
        await client.ExecuteFireAndForgetAsync("SCRIPT", "KILL");
        await client.ExecuteFireAndForgetAsync("SCRIPT", "HELP");
        await WaitForCommandsAsync(server, 6);

        await Assert.That(server.ReceivedCommands)
            .IsEquivalentTo([
                "SCRIPT KILL",
                "SCRIPT EXISTS sha1",
                "SCRIPT HELP",
                "SCRIPT SHOW sha1",
                "SCRIPT KILL",
                "SCRIPT HELP",
            ]);
    }

    [Test]
    public async Task CatalogFireAndForget_CompletesWithoutPendingResultAndDiscardsReply()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.PongReply)
        {
            MinimumCommandsBeforeReply = 2,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.ExecuteFireAndForgetAsync(RespireCommands.String.SET, "catalog-key", "value")
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForCommandsAsync(server, 1);
        using var response = await client.ExecuteAsync(RespireCommands.Connection.PING)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(response.AsString()).IsEqualTo("PONG");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["SET catalog-key value", "PING"]);
    }

    [Test]
    public async Task CatalogCommand_PropagatesServerErrors()
    {
        await using var server = new FakeRespServer("-ERR catalog failure\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var error = await Assert.That(async () => await client.ExecuteAsync(RespireCommands.String.GETEX, "key"))
            .Throws<RespireServerException>()
            .WithMessage("ERR catalog failure");
        await Assert.That(error!.CommandName).IsEqualTo("GETEX");
    }

    private static async Task WaitForCommandsAsync(FakeRespServer server, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen < count)
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
