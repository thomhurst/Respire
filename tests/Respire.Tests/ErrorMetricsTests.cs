using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Runtime.CompilerServices;
using System.Text;
using Respire.Internal;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public partial class ErrorMetricsTests
{
    [Test]
    [MatrixDataSource]
    public async Task ReconnectOwnsEstablishmentFailureOnce(
        [Matrix(2, 3)] int protocol, [Matrix(false, true)] bool observeEstablishment)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command.StartsWith("HELLO 3", StringComparison.Ordinal)
                ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray() : null,
        };
        await using var multiplexer = await Respire.Infrastructure.RespireConnectionMultiplexer.CreateAsync(
            "127.0.0.1", server.Port, options: new()
            {
                Protocol = (RespProtocol)protocol,
                ObserveEstablishmentErrors = observeEstablishment,
                ReconnectPolicy = new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 },
            });
        await multiplexer.GetConnection().DisposeAsync();
        await server.DisposeAsync();
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        multiplexer.SlotStateChanged += (_, change) =>
        {
            if (change.State == RespireConnectionState.Disconnected && change.ReconnectExhausted)
                exhausted.TrySetResult();
        };
        using var capture = new Capture(throwOnMeasurement: true);

        await Assert.That(() => multiplexer.GetConnection()).Throws<RespireConnectionException>();
        // The lifecycle event follows the reconnect owner's complete error report.
        await exhausted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        var item = capture.Items.Single();
        await Assert.That(item.Tags["redis.client.errors.category"]).IsEqualTo("network");
        await Assert.That(item.Tags["error.type"]).IsEqualTo(typeof(SocketException).FullName);
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InitialEstablishmentKeepsItsConfiguredObserver(bool observeEstablishment)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        var error = new SocketException((int)SocketError.ConnectionRefused);
        await Assert.That(async () => await Respire.Infrastructure.RespireConnectionMultiplexer.CreateAsync(
            "scripted", options: new()
            {
                Protocol = RespProtocol.Resp2,
                ObserveEstablishmentErrors = observeEstablishment,
                TestingStreamFactory = (_, _, _) => ValueTask.FromException<Stream>(error),
            })).Throws<SocketException>();
        await Assert.That(capture.Items.Count).IsEqualTo(observeEstablishment ? 1 : 0);
        if (observeEstablishment)
        {
            var item = capture.Items.Single();
            await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
    }

    [Test]
    [MatrixDataSource]
    public async Task AggregateConnectionFailuresRetainNetworkClassification(
        [Matrix(1, 2)] int causes, [Matrix(false, true)] bool nested)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var failures = Enumerable.Range(0, causes)
            .Select(_ => (Exception)new SocketException((int)SocketError.ConnectionRefused)).ToArray();
        Exception error = new RespireConnectionException("private endpoints", new AggregateException(failures));
        if (nested) error = new RespireConnectionException("private outer", new AggregateException(error));
        using var capture = new Capture(throwOnMeasurement: true);
        RespireTelemetry.RecordError(error, internallyHandled: false, retryAttempts: 2);
        var item = capture.Items.Single();
        await Assert.That(item.Tags["redis.client.errors.category"]).IsEqualTo("network");
        await Assert.That(item.Tags["error.type"]).IsEqualTo(
            causes == 1 ? typeof(SocketException).FullName : typeof(RespireConnectionException).FullName);
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(2);
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(item.Tags.ContainsKey("db.response.status_code")).IsFalse();
    }

    [Test]
    [MatrixDataSource]
    public async Task DiscardedRepliesRetainSubmissionRetries(
        [Matrix(2, 3)] int protocol, [Matrix(false, true)] bool retire,
        [Matrix(false, true)] bool capacityWait, [Matrix(false, true)] bool borrowed,
        [Matrix(false, true)] bool lateActivation, [Matrix(false, true)] bool direct)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = lateActivation ? RespireMetricGroups.None : RespireMetricGroups.Resiliency });
        var key = direct ? "key" + new string('x', 70_000) : "key";
        var get = "GET " + key;
        // Binary keys provide a complete-frame bound without scanning text on the send path.
        var command = new Cmd1(Verbs.Get, Encoding.ASCII.GetBytes(key));
        await Assert.That(command.GetWriteSizeHint() > 64 * 1024).IsEqualTo(direct);
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply)
        {
            SuppressReply = command => command == "PING hold" || command == get,
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
                "PING" => FakeRespServer.PongReply,
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, Connections = 2, MaxInflightCommands = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var connection = client.Core.Multiplexer.GetConnection();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<RespValue>? held = null;
        if (capacityWait)
        {
            held = connection.SendCheckedAsync(
                new RawCommand("*2\r\n$4\r\nPING\r\n$4\r\nhold\r\n"u8.ToArray())).AsTask();
            while (!server.ReceivedCommands.Contains("PING hold")) await Task.Delay(1, deadline.Token);
        }
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var capture = new Capture(throwOnMeasurement: true, onMeasurement: item =>
        {
            if (Equals(item.Tags.GetValueOrDefault("db.response.status_code"), "NOPERM")) recorded.TrySetResult();
        });
        var observation = borrowed ? RespireTelemetry.ErrorObservation.Rent(force: true) : default;
        var unrelated = new List<RespireTelemetry.ErrorObservation>();
        Task? retirement = null;
        try
        {
            if (retire && !capacityWait) await connection.RetireAsync();
            var pending = StartSend();
            if (capacityWait)
            {
                await Assert.That(pending.IsCompleted).IsFalse();
                if (retire) retirement = connection.RetireAsync();
                else await ReleaseHeldAsync();
            }
            await pending.WaitAsync(deadline.Token);
            // A completed submission can return its caller lease before the delayed reply.
            var returned = observation;
            observation.Dispose();
            observation = default;
            ReuseReturnedObservation(returned);
            if (lateActivation) RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
            while (!server.ReceivedCommands.Contains(get)) await Task.Delay(1, deadline.Token);
            var index = server.ReceivedCommands.ToList().IndexOf(get);
            await server.SendRawAsync("-NOPERM private-key\r\n"u8.ToArray(), server.ReceivedConnectionIds[index]);
            await recorded.Task.WaitAsync(deadline.Token);
            var error = capture.Items.Single(item => Equals(item.Tags.GetValueOrDefault("db.response.status_code"), "NOPERM"));
            await Assert.That((bool)error.Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(error.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(retire ? 1 : 0);
            await Assert.That(capture.Items.Count).IsEqualTo(retire && !lateActivation ? 2 : 1);
            if (held is not null) await ReleaseHeldAsync();
            if (retirement is not null) await retirement.WaitAsync(deadline.Token);
        }
        finally
        {
            observation.Dispose();
            foreach (var lease in unrelated) lease.Dispose();
            if (held is not null) await ReleaseHeldAsync();
        }

        async Task ReleaseHeldAsync()
        {
            var index = server.ReceivedCommands.ToList().IndexOf("PING hold");
            await server.SendRawAsync(FakeRespServer.PongReply, server.ReceivedConnectionIds[index]);
            (await held!.WaitAsync(deadline.Token)).Dispose();
            held = null;
        }

        Task StartSend()
        {
            return connection.SendFireAndForgetAsync(in command,
                deadline.Token, commandName: "GET", observation: observation).AsTask();
        }

        void ReuseReturnedObservation(RespireTelemetry.ErrorObservation returned)
        {
            // The bounded pool need not return its newest lease first. Hold intervening rents
            // so this control proves reuse even after other tests have filled the pool.
            for (var i = 0; i <= 4096; i++)
            {
                var lease = RespireTelemetry.ErrorObservation.Rent(force: true);
                unrelated.Add(lease);
                lease.SetAttempts(99);
                if (returned.IsEmpty || ReferenceEquals(ObservationStorage(returned), ObservationStorage(lease))) return;
            }
            throw new InvalidOperationException("The returned observation was not re-rented from its bounded pool.");
        }
    }

    [Test]
    [MatrixDataSource]
    public async Task FunctionPreflightReportsOneFinalError(
        [Matrix(2, 3)] int protocol, [Matrix(false, true)] bool commandMetrics,
        [Matrix(false, true)] bool errorsEnabled, [Matrix("integer", "string", "generic", "raw")] string surface)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = (errorsEnabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None)
                | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None),
        });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n"),
            _ when command.StartsWith("FCALL ", StringComparison.Ordinal) => ":42\r\n"u8.ToArray(),
            _ => null,
        };
        await using var root = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, UseCluster = true, Connections = 1,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", server.Port)],
        });
        var client = root.WithKeyPrefix("tenant:");
        var function = RespireFunction.Create("function");
        RespireKey[] keys = ["{foo}:first", "{bar}:second"];
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var rawCallReturned = false;
        var error = await Assert.That(async () =>
        {
            switch (surface)
            {
                case "integer": await client.Functions.ExecuteIntegerAsync(function, keys); break;
                case "string": await client.Functions.ExecuteStringAsync(function, keys); break;
                case "generic": await client.Functions.ExecuteAsync<int>(function, keys); break;
                default:
                    var pending = client.Functions.ExecuteSpanAsync(function, keys, []);
                    rawCallReturned = true;
                    using (await pending) { }
                    break;
            }
        }).Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("CROSSSLOT");
        if (surface == "raw") await Assert.That(rawCallReturned).IsFalse();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FCALL ", StringComparison.Ordinal))).IsFalse();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(errorsEnabled ? 1 : 0);
        if (errorsEnabled)
        {
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("CROSSSLOT");
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        // A later successful operation must neither inherit the final flag nor emit an error.
        await Assert.That(await client.Functions.ExecuteIntegerAsync(function, ["{foo}:first"])).IsEqualTo(42);
        await Assert.That(capture.Items.Count).IsEqualTo(items.Length);
    }

    [Test]
    [MatrixDataSource]
    public async Task ShardedRecoveryRedirectsReportInternalErrors(
        [Matrix(2, 3)] int protocol, [Matrix(false, true)] bool commandMetrics,
        [Matrix(false, true)] bool errorsEnabled, [Matrix("MOVED", "ASK")] string redirect)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = (errorsEnabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None)
                | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None),
        });
        await using var source = new FakeRespServer(6, FakeRespServer.OkReply);
        await using var target = new FakeRespServer(6, FakeRespServer.OkReply);
        var recovering = false;
        var slot = ClusterHash.GetSlot("one");
        source.ReplyOverride = (_, command) => command == "SSUBSCRIBE one" && Volatile.Read(ref recovering)
            ? Encoding.ASCII.GetBytes($"-{redirect} {slot} 127.0.0.1:{target.Port}\r\n") : Reply(command);
        target.ReplyOverride = (_, command) => command == "SSUBSCRIBE one"
            ? [.. Confirmation("ssubscribe"), .. Encoding.ASCII.GetBytes(
                $"{(protocol == 3 ? '>' : '*')}3\r\n$8\r\nsmessage\r\n$3\r\none\r\n$9\r\nrecovered\r\n")]
            : Reply(command);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, UseCluster = true, Connections = 1,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", source.Port)],
        });
        await using var subscription = await client.SubscribeShardedAsync("one").AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var subscribeIndex = source.ReceivedCommands.ToList().LastIndexOf("SSUBSCRIBE one");
        var socket = source.ReceivedConnectionIds[subscribeIndex];
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        Volatile.Write(ref recovering, true);
        // An unsolicited unsubscribe starts the detached recovery owner, with no caller lease.
        await source.SendRawAsync(Confirmation("sunsubscribe"), socket);
        await using var reader = subscription.GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Gap);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(reader.Current.Text).IsEqualTo("recovered");
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(errorsEnabled ? 1 : 0);
        if (errorsEnabled)
        {
            await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo(redirect);
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That(target.ReceivedCommands.Contains("ASKING")).IsEqualTo(redirect == "ASK");

        byte[] Confirmation(string verb) => Encoding.ASCII.GetBytes(
            $"{(protocol == 3 ? '>' : '*')}3\r\n${verb.Length}\r\n{verb}\r\n$3\r\none\r\n:1\r\n");
        byte[]? Reply(string command) => command switch
        {
            "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{source.Port}\r\n"),
            "SSUBSCRIBE one" => Confirmation("ssubscribe"),
            "SUNSUBSCRIBE one" => Confirmation("sunsubscribe"),
            _ => null,
        };
    }

    [Test]
    [MatrixDataSource]
    public async Task SingleHashExpiryConversionRetainsFinalOwner(
        [Matrix(2, 3)] int protocol, [Matrix(false, true)] bool commandMetrics,
        [Matrix("standalone", "cluster", "moved")] string route,
        [Matrix("empty", "extra", "non-array", "non-integer", "overflow", "server", "success", "missing", "persistent")] string outcome)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        var response = outcome switch
        {
            "empty" => "*0\r\n"u8.ToArray(),
            "extra" => "*2\r\n:1\r\n:2\r\n"u8.ToArray(),
            "non-array" => ":1\r\n"u8.ToArray(),
            "non-integer" => "*1\r\n$3\r\nbad\r\n"u8.ToArray(),
            "overflow" => "*1\r\n:9223372036854775807\r\n"u8.ToArray(),
            "server" => "-NOPERM private-field\r\n"u8.ToArray(),
            "missing" => "*1\r\n:-2\r\n"u8.ToArray(),
            "persistent" => "*1\r\n:-1\r\n"u8.ToArray(),
            _ => "*1\r\n:2500\r\n"u8.ToArray(),
        };
        await using var target = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "CLUSTER SLOTS" => "*0\r\n"u8.ToArray(),
                _ when command.StartsWith("HPTTL ", StringComparison.Ordinal) => response,
                _ => null,
            },
        };
        await using var seed = new FakeRespServer(4, FakeRespServer.OkReply);
        var slot = ClusterHash.GetSlot("tenant:key");
        seed.ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n"),
            _ when command.StartsWith("HPTTL ", StringComparison.Ordinal) => route == "moved"
                ? Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n") : response,
            _ => null,
        };
        await using var root = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, Connections = 1, UseCluster = route != "standalone",
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var client = root.WithKeyPrefix("tenant:");
        byte[] field = [255, 0, (byte)'x'];
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = client.Hashes.ExpiryAsync("key", (RespireKey)field, deadline.Token).AsTask();
        field[0] = 1;
        Exception? error = null;
        RespireTtl ttl = default;
        try { ttl = await pending.WaitAsync(deadline.Token); }
        catch (Exception caught) { error = caught; }
        var success = outcome is "success" or "missing" or "persistent";
        await Assert.That(error is null).IsEqualTo(success);
        var items = capture.Items.ToArray();
        var final = items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(success ? 0 : 1);
        if (error is not null)
        {
            await Assert.That(final[0].Tags["error.type"]).IsEqualTo(error.GetType().FullName);
            await Assert.That(final[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(route == "moved" ? 1 : 0);
            if (outcome is "empty" or "extra") await Assert.That(error is RespireProtocolException).IsTrue();
            if (outcome == "server") await Assert.That(final[0].Tags["db.response.status_code"]).IsEqualTo("NOPERM");
        }
        else
        {
            await Assert.That(ttl.Exists).IsEqualTo(outcome != "missing");
            await Assert.That(ttl.HasExpiry).IsEqualTo(outcome == "success");
            if (outcome == "success") await Assert.That(ttl.TimeToLive).IsEqualTo(TimeSpan.FromMilliseconds(2500));
        }
        var handled = items.Where(item => (bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(handled.Length).IsEqualTo(route == "moved" ? 1 : 0);
        var responder = route == "moved" ? target : seed;
        var index = responder.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("HPTTL ", StringComparison.Ordinal));
        var arguments = responder.ReceivedArguments[index];
        await Assert.That(arguments[1]).IsEquivalentTo("tenant:key"u8.ToArray());
        await Assert.That(arguments[4]).IsEquivalentTo(new byte[] { 255, 0, (byte)'x' });
    }

    [Test]
    [MatrixDataSource]
    public async Task ValidatedPrefixRetirementRecordsNullObservation(
        [Matrix(2, 3)] int protocol, [Matrix(false, true)] bool commandMetrics,
        [Matrix(false, true)] bool capacityWait, [Matrix(false, true)] bool withObservation)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        var heldWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldCount = 0;
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "PING hold") return false;
                if (Interlocked.Increment(ref heldCount) == 2) heldWritten.TrySetResult();
                return true;
            },
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "GET key" => "-WRONGTYPE private-key\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, Connections = 2, MaxInflightCommands = 2,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var original = client.Core.Multiplexer.GetConnection(0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<RespValue>? first = null;
        Task<RespValue>? second = null;
        if (capacityWait)
        {
            var hold = new RawCommand("*2\r\n$4\r\nPING\r\n$4\r\nhold\r\n"u8.ToArray());
            first = original.SendCheckedAsync(hold, deadline.Token).AsTask();
            second = original.SendCheckedAsync(hold, deadline.Token).AsTask();
            await heldWritten.Task.WaitAsync(deadline.Token);
        }
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        using var observation = withObservation ? RespireTelemetry.ErrorObservation.Rent(force: true) : default;
        Task? retirement = capacityWait ? null : original.RetireAsync();
        var pending = ClusterRouter.SendAskingAsync(original, new Cmd1(Verbs.Get, "key"), deadline.Token,
            observation: observation).AsTask();
        if (capacityWait)
        {
            await Assert.That(pending.IsCompleted).IsFalse();
            retirement = original.RetireAsync();
        }
        try
        {
            var error = await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<RespireServerException>();
            await Assert.That(error!.Code).IsEqualTo("WRONGTYPE");
        }
        finally
        {
            if (first is not null)
            {
                var index = server.ReceivedCommands.ToList().IndexOf("PING hold");
                await server.SendRawAsync("+PONG\r\n+PONG\r\n"u8.ToArray(), server.ReceivedConnectionIds[index]);
                using var firstReply = await first.WaitAsync(deadline.Token);
                using var secondReply = await second!.WaitAsync(deadline.Token);
            }
            await retirement!.WaitAsync(deadline.Token);
        }
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(1);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[0].Tags["error.type"]).IsEqualTo(typeof(RespireConnectionRetiredException).FullName);
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        if (!observation.IsEmpty) await Assert.That(observation.Attempts).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    [MatrixDataSource]
    public async Task ClusterBatchRetainsEachPendingTransparentRetry(
        [Matrix(2, 3)] int protocol, [Matrix(0, 1, 2, 3)] int selection,
        [Matrix(false, true)] bool capacityWait, [Matrix(false, true)] bool redirect)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = selection >= 2
            ? RespireMetricGroups.None : RespireMetricGroups.Resiliency
                | (selection == 1 ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        var heldWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = new FakeRespServer(8, FakeRespServer.OkReply);
        byte[]? topology = null;
        byte[]? moved = null;
        byte[]? Reply(string command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "CLUSTER SLOTS" => topology,
            "GET {batch}:one" or "GET {batch}:two" => "-WRONGTYPE private-key\r\n"u8.ToArray(),
            "GET {batch}:ok" => "$2\r\nok\r\n"u8.ToArray(),
            _ => null,
        };
        target.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray() : Reply(command);
        await using var seed = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "PING hold") return false;
                heldWritten.TrySetResult();
                return true;
            },
            ReplyOverride = (_, command) => redirect && command.StartsWith("GET ", StringComparison.Ordinal)
                ? moved : Reply(command),
        };
        topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n");
        var slot = ClusterHash.GetSlot("{batch}:one");
        moved = Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n");
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, Connections = 2, MaxInflightCommands = 1,
            UseCluster = true, ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var original = await client.Core.Cluster!.GetConnectionAsync(slot, default, discovery: null);
        _ = original.Multiplexer!.GetConnection();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<RespValue>? held = null;
        if (capacityWait)
        {
            held = original.SendCheckedAsync(new RawCommand("*2\r\n$4\r\nPING\r\n$4\r\nhold\r\n"u8.ToArray())).AsTask();
            await heldWritten.Task.WaitAsync(deadline.Token);
        }
        Task? retirement = null;
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.GetTagItem("db.operation.name")?.ToString() != "GET") return;
                if (!capacityWait && retirement is null)
                {
                    if (selection == 2) RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
                    retirement = original.RetireAsync();
                }
                if (Interlocked.Increment(ref started) == 3) allStarted.TrySetResult();
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: selection == 1);
        using var batch = client.CreateBatch();
        var first = batch.GetString("{batch}:one");
        var second = batch.GetString("{batch}:two");
        var succeeded = batch.GetString("{batch}:ok");
        var execution = batch.TryExecuteAsync().AsTask();
        await allStarted.Task.WaitAsync(deadline.Token);
        if (capacityWait)
        {
            await Assert.That(execution.IsCompleted).IsFalse();
            await Assert.That(seed.ReceivedCommands.Any(command => command.StartsWith("GET ", StringComparison.Ordinal))).IsFalse();
            if (selection == 2) RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
            retirement = original.RetireAsync();
        }
        try
        {
            var result = await execution.WaitAsync(deadline.Token);
            await Assert.That(result.FailureCount).IsEqualTo(2);
            await Assert.That(result.ThrowIfAnyFailed).Throws<RespireServerException>();
            await Assert.That(succeeded.Result).IsEqualTo("ok");
            await Assert.That(() => first.Result).Throws<RespireServerException>();
            await Assert.That(() => first.Result).Throws<RespireServerException>();
            await Assert.That(() => second.Result).Throws<RespireServerException>();
        }
        finally
        {
            if (held is not null)
            {
                var index = seed.ReceivedCommands.ToList().IndexOf("PING hold");
                await seed.SendRawAsync(FakeRespServer.PongReply, seed.ReceivedConnectionIds[index]);
                using var reply = await held.WaitAsync(deadline.Token);
            }
            if (retirement is not null) await retirement.WaitAsync(deadline.Token);
        }
        var items = capture.Items.ToArray();
        var expectedMeasurements = selection == 3 ? 0 : 5 + (redirect ? 3 : 0);
        await Assert.That(items.Length).IsEqualTo(expectedMeasurements);
        if (selection != 3)
        {
            var retirements = items.Where(item => (bool)item.Tags["redis.client.errors.internal"]!
                && item.Tags["error.type"]?.ToString() == typeof(RespireConnectionRetiredException).FullName).ToArray();
            await Assert.That(retirements.Length).IsEqualTo(3);
            foreach (var item in retirements)
                await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            if (redirect)
            {
                var redirects = items.Where(item => (bool)item.Tags["redis.client.errors.internal"]!
                    && item.Tags.TryGetValue("db.response.status_code", out var status) && status?.ToString() == "MOVED").ToArray();
                await Assert.That(redirects.Length).IsEqualTo(3);
                foreach (var item in redirects)
                    await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
            }
            var final = items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
            await Assert.That(final.Length).IsEqualTo(2);
            foreach (var item in final)
            {
                await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(redirect ? 2 : 1);
                await Assert.That(item.Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
            }
        }
        using var nextBatch = client.CreateBatch();
        var next = nextBatch.GetString("{batch}:one");
        await nextBatch.TryExecuteAsync();
        await Assert.That(() => next.Result).Throws<RespireServerException>();
        if (selection != 3)
            await Assert.That(capture.Items.Last().Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    [MatrixDataSource]
    public async Task PrefixedSubscriptionFailurePreservesPhysicalTargetAndFinalOwner(
        [Matrix("channel", "pattern", "sharded")] string path, [Matrix(false, true)] bool binary,
        [Matrix(false, true)] bool commandMetrics, [Matrix(2, 3)] int protocol)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply);
        var verb = path switch { "pattern" => "PSUBSCRIBE", "sharded" => "SSUBSCRIBE", _ => "SUBSCRIBE" };
        server.ReplyOverride = (_, command) => command switch
        {
            _ when command.StartsWith("HELLO ", StringComparison.Ordinal) => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n"),
            _ when command.StartsWith(verb + " ", StringComparison.Ordinal) => "-NOPERM prefixed activation denied\r\n"u8.ToArray(),
            _ => null,
        };
        await using var root = RespireClient.Create(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, UseCluster = true, Connections = 1,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", server.Port)],
        });
        var client = binary ? root.WithPubSubPrefix((RespireKey)new byte[] { 255, (byte)'*', 0 })
            : root.WithPubSubPrefix("tenant*:");
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var error = await Assert.That(async () => await (path switch
        {
            "pattern" => client.SubscribePatternAsync("ch"),
            "sharded" => client.SubscribeShardedAsync("ch"),
            _ => client.SubscribeAsync("ch"),
        })).Throws<RespireServerException>();
        await Assert.That(error!.Message).Contains("prefixed activation denied");
        var index = server.ReceivedCommands.ToList().FindIndex(command => command.StartsWith(verb + " ", StringComparison.Ordinal));
        byte[] prefix;
        if (binary) prefix = path == "pattern" ? [255, (byte)'\\', (byte)'*', 0] : [255, (byte)'*', 0];
        else prefix = Encoding.UTF8.GetBytes(path == "pattern" ? "tenant\\*:" : "tenant*:");
        byte[] expected = [.. prefix, (byte)'c', (byte)'h'];
        await Assert.That(server.ReceivedArguments[index][1]).IsEquivalentTo(expected, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(1);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("NOPERM");
    }

    [Test]
    [MatrixDataSource]
    public async Task DurabilityAdmissionFailureReportsOnlyCaller(
        [Matrix(false, true)] bool aof, [Matrix(false, true)] bool commandMetrics,
        [Matrix("cancelled", "client-disposed", "batch-disposed", "empty", "cross-slot", "keyless")] string failure)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command switch
        {
            _ when command.StartsWith("WAIT ", StringComparison.Ordinal) => ":1\r\n"u8.ToArray(),
            _ when command.StartsWith("WAITAOF ", StringComparison.Ordinal) => "*2\r\n:1\r\n:1\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = failure is "cross-slot" or "keyless",
            Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var batch = client.CreateBatch();
        RespirePending<bool>? queued = failure is "empty" or "keyless" ? null : batch.Set("{one}:key", "value");
        if (failure == "cross-slot") _ = batch.Set("{two}:key", "value");
        if (failure == "keyless") _ = batch.Functions.List();
        if (failure == "client-disposed") await client.DisposeAsync();
        if (failure == "batch-disposed") batch.Dispose();
        var previousStatus = queued?.Status;
        using var caller = new CancellationTokenSource();
        if (failure == "cancelled") await caller.CancelAsync();
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var error = await Assert.That(async () =>
        {
            if (aof) await batch.ExecuteAndWaitForAofAsync(true, 1, TimeSpan.Zero, caller.Token);
            else await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.Zero, caller.Token);
        }).Throws<Exception>();
        if (failure == "cancelled")
        {
            await Assert.That(error).IsTypeOf<OperationCanceledException>();
            await Assert.That(((OperationCanceledException)error!).CancellationToken).IsEqualTo(caller.Token);
        }
        if (failure is "client-disposed" or "batch-disposed") await Assert.That(error).IsTypeOf<ObjectDisposedException>();
        if (failure == "empty") await Assert.That(error).IsTypeOf<InvalidOperationException>();
        if (failure is "cross-slot" or "keyless") await Assert.That(error).IsTypeOf<NotSupportedException>();
        await Assert.That(batch.IsSent).IsFalse();
        if (queued is not null) await Assert.That(queued.Status).IsEqualTo(previousStatus!.Value);
        await Assert.That(server.ReceivedCommands).IsEmpty();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(1);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        if (failure is "cancelled" or "empty")
        {
            queued ??= batch.Set("key", "value");
            if (aof) await batch.ExecuteAndWaitForAofAsync(true, 1, TimeSpan.Zero);
            else await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.Zero);
            await Assert.That(queued.Result).IsTrue();
            await Assert.That(batch.IsSent).IsTrue();
            await Assert.That(capture.Items.Count).IsEqualTo(1);
        }
    }

    [Test]
    [MatrixDataSource]
    public async Task SubscriptionActivationHasOneFinalOwner(
        [Matrix("channel", "pattern", "sharded", "notification")] string path,
        [Matrix(false, true)] bool cluster,
        [Matrix("success", "server", "transport", "disposed", "cancelled")] string outcome,
        [Matrix(false, true)] bool commandMetrics,
        [Matrix(2, 3)] int protocol,
        [Matrix(false, true)] bool prefixed)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        var names = path == "notification"
            ? new[] { RespireChannel.KeySpacePrefix("one:", 0), RespireChannel.KeySpacePrefix("two:", 0) }
            : new[] { new RespireChannel("one"), new RespireChannel("two") };
        var verb = path switch { "sharded" => "SSUBSCRIBE", "pattern" or "notification" => "PSUBSCRIBE", _ => "SUBSCRIBE" };
        var prefix = prefixed && path != "notification" ? "tenant:" : "";
        var first = prefix + names[0].ToString();
        var second = prefix + names[1].ToString();
        server.SuppressReply = command => outcome == "cancelled" && command == $"{verb} {second}";
        server.ReplyOverride = (_, command) =>
        {
            if (command.StartsWith("HELLO ", StringComparison.Ordinal)) return "%1\r\n+proto\r\n:3\r\n"u8.ToArray();
            if (command == "CLUSTER SLOTS") return Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n");
            var parts = command.Split(' ', 2);
            if (parts[0] is "SUBSCRIBE" or "PSUBSCRIBE" or "SSUBSCRIBE"
                or "UNSUBSCRIBE" or "PUNSUBSCRIBE" or "SUNSUBSCRIBE")
            {
                if (parts[0] == verb && parts[1] == first && outcome == "transport")
                    server.CloseConnectionAfterCommand = server.CommandsSeen + 1;
                if (parts[0] == verb && parts[1] == second && outcome == "server")
                    return "-NOPERM activation denied\r\n"u8.ToArray();
                var confirmation = parts[0].ToLowerInvariant();
                return Encoding.UTF8.GetBytes($"{(protocol == 3 ? '>' : '*')}3\r\n${confirmation.Length}\r\n{confirmation}\r\n${Encoding.UTF8.GetByteCount(parts[1])}\r\n{parts[1]}\r\n:1\r\n");
            }
            return null;
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, UseCluster = cluster, Connections = 1,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", server.Port)],
        });
        if (outcome == "disposed") await client.DisposeAsync();
        var subscriber = prefixed ? client.WithPubSubPrefix("tenant:") : client;
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var pending = ActivateAsync();
        async Task<RespireSubscription> ActivateAsync() => await (path switch
        {
            "pattern" => subscriber.SubscribePatternAsync(names, caller.Token),
            "sharded" => subscriber.SubscribeShardedAsync(names, caller.Token),
            _ => subscriber.SubscribeAsync(names, caller.Token),
        });
        if (outcome == "cancelled")
        {
            while (!server.ReceivedCommands.Contains($"{verb} {second}")) await Task.Delay(1, caller.Token);
            await caller.CancelAsync();
        }
        if (outcome == "success")
        {
            await using var subscription = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(server.ReceivedCommands).Contains($"{verb} {first}");
            await Assert.That(server.ReceivedCommands).Contains($"{verb} {second}");
        }
        else
        {
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10))).Throws<Exception>();
            if (outcome == "server")
            {
                await Assert.That(error).IsTypeOf<RespireServerException>();
                await Assert.That(error!.Message).Contains("activation denied");
            }
            if (outcome == "transport") await Assert.That(error).IsTypeOf<RespireConnectionException>();
            if (outcome == "disposed") await Assert.That(error).IsTypeOf<ObjectDisposedException>();
            if (outcome == "cancelled")
            {
                await Assert.That(error).IsTypeOf<OperationCanceledException>();
                await Assert.That(((OperationCanceledException)error!).CancellationToken).IsEqualTo(caller.Token);
            }
        }
        var final = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(outcome == "success" ? 0 : 1);
        if (final.Length != 0)
            await Assert.That(final[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        if (outcome == "server") await Assert.That(final[0].Tags["db.response.status_code"]).IsEqualTo("NOPERM");
    }

    [Test]
    [Arguments("MOVED", "success", false, 2)]
    [Arguments("MOVED", "success", false, 3)]
    [Arguments("MOVED", "success", true, 2)]
    [Arguments("MOVED", "success", true, 3)]
    [Arguments("MOVED", "server", false, 2)]
    [Arguments("MOVED", "server", false, 3)]
    [Arguments("MOVED", "server", true, 2)]
    [Arguments("MOVED", "server", true, 3)]
    [Arguments("ASK", "success", false, 2)]
    [Arguments("ASK", "success", false, 3)]
    [Arguments("ASK", "success", true, 2)]
    [Arguments("ASK", "success", true, 3)]
    [Arguments("ASK", "server", false, 2)]
    [Arguments("ASK", "server", false, 3)]
    [Arguments("ASK", "server", true, 2)]
    [Arguments("ASK", "server", true, 3)]
    [Arguments("ASK", "prefix", false, 2)]
    [Arguments("ASK", "prefix", false, 3)]
    [Arguments("ASK", "prefix", true, 2)]
    [Arguments("ASK", "prefix", true, 3)]
    public async Task SubscriptionRedirectBorrowsActivationOwner(
        string redirect, string outcome, bool commandMetrics, int protocol)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var source = new FakeRespServer(4, FakeRespServer.OkReply);
        await using var target = new FakeRespServer(4, FakeRespServer.OkReply);
        var slot = ClusterHash.GetSlot("one");
        source.ReplyOverride = (_, command) => command == "SSUBSCRIBE one"
            ? Encoding.ASCII.GetBytes($"-{redirect} {slot} 127.0.0.1:{target.Port}\r\n") : Reply(command);
        target.ReplyOverride = (_, command) => (command == "SSUBSCRIBE one" && outcome == "server")
            || (command == "ASKING" && outcome == "prefix")
                ? "-NOPERM redirected activation denied\r\n"u8.ToArray() : Reply(command);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, UseCluster = true, Connections = 1,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", source.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        if (outcome == "success")
        {
            await using var subscription = await client.SubscribeShardedAsync("one").AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        else
        {
            var error = await Assert.That(async () => await client.SubscribeShardedAsync("one").AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10))).Throws<RespireServerException>();
            await Assert.That(error!.Message).Contains("redirected activation denied");
        }
        var items = capture.Items.ToArray();
        var handled = items.Where(item => Equals(item.Tags.GetValueOrDefault("db.response.status_code"), redirect)).ToArray();
        await Assert.That(handled.Length).IsEqualTo(1);
        await Assert.That((bool)handled[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(handled[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        var final = items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(outcome == "success" ? 0 : 1);
        if (final.Length != 0)
        {
            await Assert.That(final[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
            await Assert.That(final[0].Tags["db.response.status_code"]).IsEqualTo("NOPERM");
        }

        byte[]? Reply(string command)
        {
            if (command.StartsWith("HELLO ", StringComparison.Ordinal)) return "%1\r\n+proto\r\n:3\r\n"u8.ToArray();
            if (command == "CLUSTER SLOTS") return Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{source.Port}\r\n");
            var parts = command.Split(' ', 2);
            if (parts[0] is "SSUBSCRIBE" or "SUNSUBSCRIBE")
            {
                var confirmation = parts[0].ToLowerInvariant();
                return Encoding.UTF8.GetBytes($"{(protocol == 3 ? '>' : '*')}3\r\n${confirmation.Length}\r\n{confirmation}\r\n${parts[1].Length}\r\n{parts[1]}\r\n:1\r\n");
            }
            return null;
        }
    }

    [Test]
    [MatrixDataSource]
    public async Task ResumableScanRoutesHaveOneFinalBoundary(
        [Matrix("legacy", "modern", "metadata-denied", "command-absent", "command-denied")] string route,
        [Matrix("success", "server", "transport", "protocol", "disposed")] string outcome,
        [Matrix(false, true)] bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(3, FakeRespServer.PongReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*3\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n$4\r\nnode\r\n");
        server.ReplyOverride = (_, command) =>
        {
            if (command == "CLUSTER SLOTS") return topology;
            if (command == "CLUSTER NODES") return Bulk($"node 127.0.0.1:{server.Port}@17000 myself,master - 0 0 1 connected 0-16383\n");
            if (command == "INFO server") return Bulk("# Server\r\nrun_id:run\r\n");
            if (command == "COMMAND INFO CLUSTERSCAN")
            {
                if (outcome == "transport" && route is not ("command-absent" or "command-denied"))
                    server.CloseConnectionAfterCommand = server.CommandsSeen + 1;
                return route switch
                {
                    "legacy" => "*1\r\n$-1\r\n"u8.ToArray(),
                    "metadata-denied" => "-NOPERM metadata denied\r\n"u8.ToArray(),
                    _ => "*1\r\n*1\r\n$11\r\nclusterscan\r\n"u8.ToArray(),
                };
            }
            if (command.StartsWith("CLUSTERSCAN ", StringComparison.Ordinal) && (route is "command-absent" or "command-denied"))
            {
                if (outcome == "transport") server.CloseConnectionAfterCommand = server.CommandsSeen + 1;
                return route == "command-absent" ? "-ERR unknown command 'CLUSTERSCAN'\r\n"u8.ToArray()
                    : "-NOPERM CLUSTERSCAN denied\r\n"u8.ToArray();
            }
            if (command.StartsWith("SCAN ", StringComparison.Ordinal) || command.StartsWith("CLUSTERSCAN ", StringComparison.Ordinal))
                return outcome switch
                {
                    "server" => "-WRONGTYPE scan failed\r\n"u8.ToArray(),
                    "protocol" => "+malformed\r\n"u8.ToArray(),
                    _ => "*2\r\n$1\r\n0\r\n*0\r\n"u8.ToArray(),
                };
            return null;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        if (outcome == "disposed") await client.DisposeAsync();
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        if (outcome == "success")
            await Assert.That((await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start)).Keys).IsEmpty();
        else
        {
            var error = await Assert.That(async () => await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start)).Throws<Exception>();
            if (outcome == "server") await Assert.That(error).IsTypeOf<RespireServerException>();
            if (outcome == "protocol") await Assert.That(error).IsTypeOf<RespireProtocolException>();
            if (outcome == "disposed") await Assert.That(error).IsTypeOf<ObjectDisposedException>();
        }
        var fallback = route is "metadata-denied" or "command-absent" or "command-denied";
        var attempts = outcome != "disposed" && fallback ? 1 : 0;
        var items = capture.Items.ToArray();
        var final = items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(outcome == "success" ? 0 : 1);
        // A physical disconnect is an independent internal event, not a logical retry.
        await Assert.That(items.Length).IsEqualTo(attempts + final.Length + (outcome == "transport" ? 1 : 0));
        if (outcome == "transport")
        {
            var disconnect = items.Single(item => (bool)item.Tags["redis.client.errors.internal"]!
                && Equals(item.Tags["error.type"], final[0].Tags["error.type"]));
            await Assert.That(disconnect.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        if (final.Length != 0) await Assert.That(final[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(attempts);
        if (attempts != 0)
        {
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }

        static byte[] Bulk(string value) => Encoding.UTF8.GetBytes($"${Encoding.UTF8.GetByteCount(value)}\r\n{value}\r\n");
    }

    [Test]
    [MatrixDataSource]
    public async Task ClusterShutdownSubmissionBorrowsRetryOwner(
        [Matrix("raw", "catalog", "interpolated")] string path,
        [Matrix(false, true)] bool commandMetrics,
        [Matrix(false, true)] bool capacityWait)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            SuppressReply = command => command == "PING hold",
        };
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n");
        server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2, MaxInflightCommands = 1, AllowAdmin = true,
            UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var original = await client.Core.Cluster!.GetConnectionAsync(null, default, discovery: null);
        _ = await client.Core.Cluster.GetConnectionAsync(null, default, discovery: null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<RespValue>? held = null;
        if (capacityWait)
        {
            held = original.SendCheckedAsync(new RawCommand("*2\r\n$4\r\nPING\r\n$4\r\nhold\r\n"u8.ToArray())).AsTask();
            while (!server.ReceivedCommands.Contains("PING hold")) await Task.Delay(1, deadline.Token);
        }
        Task? retirement = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (!capacityWait && retirement is null && activity.GetTagItem("db.operation.name")?.ToString() == "SHUTDOWN")
                    retirement = original.RetireAsync();
            },
        };
        if (!capacityWait) ActivitySource.AddActivityListener(listener);
        using var payload = new RejectedSubmissionPayload();
        ReadOnlyMemory<byte> bytes = payload.Memory;
        payload.RejectReads = true;
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var pending = path switch
        {
            "raw" => client.ExecuteFireAndForgetAsync("SHUTDOWN", [bytes], deadline.Token).AsTask(),
            "catalog" => client.ExecuteFireAndForgetAsync(RespireCommands.Server.SHUTDOWN, [bytes], deadline.Token).AsTask(),
            _ => client.ExecuteFireAndForgetAsync($"SHUTDOWN {bytes}", deadline.Token).AsTask(),
        };
        if (capacityWait)
        {
            var signal = typeof(RespireConnection).GetField("_capacitySignal",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(original)!;
            var waiters = signal.GetType().GetField("_waiters",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            while (waiters.GetValue(signal) is null)
            {
                if (pending.IsCompleted) { await pending; throw new InvalidOperationException("SHUTDOWN did not wait for target capacity."); }
                await Task.Delay(1, deadline.Token);
            }
            retirement = original.RetireAsync();
        }
        var error = await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<IOException>();
        await Assert.That(ReferenceEquals(error, payload.Error)).IsTrue();
        if (held is not null)
        {
            var index = server.ReceivedCommands.ToList().IndexOf("PING hold");
            await server.SendRawAsync(FakeRespServer.PongReply, server.ReceivedConnectionIds[index]);
            using var reply = await held.WaitAsync(deadline.Token);
        }
        await Assert.That(retirement).IsNotNull();
        await retirement!.WaitAsync(deadline.Token);
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[0].Tags["error.type"]).IsEqualTo(typeof(RespireConnectionRetiredException).FullName);
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[1].Tags["error.type"]).IsEqualTo(typeof(IOException).FullName);
        await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("SHUTDOWN", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("completed", false)]
    [Arguments("completed", true)]
    [Arguments("expired-import", false)]
    [Arguments("expired-import", true)]
    [Arguments("capacity", false)]
    [Arguments("capacity", true)]
    public async Task TransactionPreflightHasOneFinalOwner(string failure, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, MaxInflightCommands = 2,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        await using var session = failure == "completed" ? null : await client.Hashes.CreateImportSessionAsync();
        await using var transaction = session is null ? client.CreateTransaction() : session.CreateTransaction();
        if (failure == "completed") await transaction.CommitAsync();
        else
        {
            _ = transaction.Hashes.Import("first", "schema", "one");
            if (failure == "capacity") _ = transaction.Hashes.Import("second", "schema", "two");
            else await session!.DisposeAsync();
        }
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var error = await Assert.That(async () => await transaction.CommitAsync()).Throws<Exception>();
        await Assert.That(error!.GetType()).IsEqualTo(failure switch
        {
            "completed" => typeof(InvalidOperationException),
            "expired-import" => typeof(ObjectDisposedException),
            _ => typeof(ArgumentOutOfRangeException),
        });
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(1);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[0].Tags["error.type"]).IsEqualTo(error.GetType().FullName);
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Contains("MULTI")).IsFalse();
        await Assert.That(server.ReceivedCommands.Contains("EXEC")).IsFalse();
        if (failure == "capacity")
        {
            // Rejected admission must release the session's usage lease.
            await Assert.That(await session!.PrepareAsync("later", "field")).IsTrue();
            await Assert.That(capture.Items.Count).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments("raw", false, false, true)]
    [Arguments("raw", false, true, true)]
    [Arguments("raw", true, false, true)]
    [Arguments("raw", true, true, true)]
    [Arguments("catalog", false, false, true)]
    [Arguments("catalog", false, true, true)]
    [Arguments("catalog", true, false, true)]
    [Arguments("catalog", true, true, true)]
    [Arguments("interpolated", false, false, true)]
    [Arguments("interpolated", false, true, true)]
    [Arguments("interpolated", true, false, true)]
    [Arguments("interpolated", true, true, true)]
    [Arguments("raw", false, true, false)]
    [Arguments("raw", true, true, false)]
    [Arguments("catalog", false, true, false)]
    [Arguments("catalog", true, true, false)]
    [Arguments("interpolated", false, true, false)]
    [Arguments("interpolated", true, true, false)]
    public async Task ClusterWideSubmissionBorrowsRetryOwner(
        string path, bool fireAndForget, bool commandMetrics, bool capacityWait)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            SuppressReply = command => command == "PING hold",
        };
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n");
        server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2, MaxInflightCommands = 1,
            UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var original = (await client.Core.Cluster!.GetMasterConnectionsAsync(default, discovery: null))[0];
        _ = await client.Core.Cluster.GetMasterConnectionsAsync(default, discovery: null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<RespValue>? held = null;
        if (capacityWait)
        {
            held = original.SendCheckedAsync(new RawCommand("*2\r\n$4\r\nPING\r\n$4\r\nhold\r\n"u8.ToArray())).AsTask();
            while (!server.ReceivedCommands.Contains("PING hold")) await Task.Delay(1, deadline.Token);
        }
        Task? retirement = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (!capacityWait && retirement is null && activity.GetTagItem("db.operation.name")?.ToString() == "FUNCTION FLUSH")
                    retirement = original.RetireAsync();
            },
        };
        // Capacity controls also cover the transport path without command instrumentation.
        if (!capacityWait) ActivitySource.AddActivityListener(listener);
        using var payload = new RejectedSubmissionPayload();
        ReadOnlyMemory<byte> bytes = payload.Memory;
        payload.RejectReads = true;
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        async Task Execute()
        {
            if (fireAndForget)
            {
                switch (path)
                {
                    case "raw": await client.ExecuteFireAndForgetAsync("FUNCTION FLUSH", [bytes], deadline.Token); break;
                    case "catalog": await client.ExecuteFireAndForgetAsync(RespireCommands.Scripting.FUNCTION_FLUSH, [bytes], deadline.Token); break;
                    default: await client.ExecuteFireAndForgetAsync($"FUNCTION FLUSH {bytes}", deadline.Token); break;
                }
            }
            else
            {
                using var reply = path switch
                {
                    "raw" => await client.ExecuteAsync("FUNCTION FLUSH", [bytes], cancellationToken: deadline.Token),
                    "catalog" => await client.ExecuteAsync(RespireCommands.Scripting.FUNCTION_FLUSH, [bytes], cancellationToken: deadline.Token),
                    _ => await client.ExecuteAsync($"FUNCTION FLUSH {bytes}", cancellationToken: deadline.Token),
                };
            }
        }
        var pending = Execute();
        if (capacityWait)
        {
            var signal = typeof(RespireConnection).GetField("_capacitySignal",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(original)!;
            var waiters = signal.GetType().GetField("_waiters",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            while (waiters.GetValue(signal) is null)
            {
                if (pending.IsCompleted) { await pending; throw new InvalidOperationException("Mutation did not wait for target capacity."); }
                await Task.Delay(1, deadline.Token);
            }
            retirement = original.RetireAsync();
        }
        var error = await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<Exception>();
        if (fireAndForget)
            await Assert.That(ReferenceEquals(((AggregateException)error!).InnerExceptions.Single(), payload.Error)).IsTrue();
        else await Assert.That(ReferenceEquals(error, payload.Error)).IsTrue();
        if (held is not null)
        {
            var index = server.ReceivedCommands.ToList().IndexOf("PING hold");
            await server.SendRawAsync(FakeRespServer.PongReply, server.ReceivedConnectionIds[index]);
            using var reply = await held.WaitAsync(deadline.Token);
        }
        await Assert.That(retirement).IsNotNull();
        await retirement!.WaitAsync(deadline.Token);
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(fireAndForget ? 3 : 2);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[0].Tags["error.type"]).IsEqualTo(typeof(RespireConnectionRetiredException).FullName);
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        if (fireAndForget)
        {
            await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
        await Assert.That((bool)items[^1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[^1].Tags["error.type"]).IsEqualTo(payload.Error.GetType().FullName);
        await Assert.That(items[^1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FUNCTION FLUSH", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CacheAsideWaitersInheritProducerRedirects(bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = new FakeRespServer(4, FakeRespServer.OkReply);
        var targetTopology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        target.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? targetTopology
            : command.StartsWith("SET ", StringComparison.Ordinal) ? "-WRONGTYPE write failed\r\n"u8.ToArray()
            : command == "HELLO 3" ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray() : FakeRespServer.OkReply;
        await using var source = new FakeRespServer(4, FakeRespServer.OkReply);
        var sourceTopology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{source.Port}\r\n");
        source.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? sourceTopology
            : command == "GET key" ? "$-1\r\n"u8.ToArray()
            : command.StartsWith("SET ", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes($"-MOVED {ClusterHash.GetSlot("key")} 127.0.0.1:{target.Port}\r\n")
            : command == "HELLO 3" ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray() : FakeRespServer.OkReply;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            ClientSideCache = new() { CoalesceConcurrentMisses = true },
            Endpoints = [new("127.0.0.1", source.Port)],
        });
        var factories = 0;
        async ValueTask<string?> Factory(CancellationToken token)
        {
            Interlocked.Increment(ref factories);
            arrived.TrySetResult();
            await release.Task.WaitAsync(token);
            return "created";
        }
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var first = client.GetOrSetAsync("key", Factory, TimeSpan.FromSeconds(10)).AsTask();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.GetOrSetAsync("key", Factory, TimeSpan.FromSeconds(10)).AsTask();
        release.TrySetResult();
        await Assert.That(async () => await first.WaitAsync(TimeSpan.FromSeconds(5))).Throws<RespireServerException>();
        await Assert.That(async () => await second.WaitAsync(TimeSpan.FromSeconds(5))).Throws<RespireServerException>();
        var items = capture.Items.ToArray();
        var final = items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(2);
        await Assert.That(final.All(item => (int)item.Tags["redis.client.operation.retry_attempts"]! == 1)).IsTrue();
        await Assert.That(items.Count(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(1);
        await Assert.That(factories).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith("SET ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task ImmediateTransactionTransportRetirementBorrowsOwner()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "SET key value" => "+QUEUED\r\n"u8.ToArray(),
                "EXEC" => "*1\r\n+OK\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2, Endpoints = [new("127.0.0.1", server.Port)],
        });
        var original = client.Core.Multiplexer.GetConnection();
        await original.RetireAsync();
        using var capture = new Capture(throwOnMeasurement: true);
        using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        using var response = await original.SendTransactionAsync(
            "*3\r\n$3\r\nSET\r\n$3\r\nkey\r\n$5\r\nvalue\r\n"u8.ToArray(), 1, observation: observation);
        await Assert.That(response.AsArray()[0].AsString()).IsEqualTo("OK");
        await Assert.That(observation.Attempts).IsEqualTo(1);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command == "MULTI")).IsEqualTo(1);
    }

    [Test]
    [MatrixDataSource]
    public async Task CacheAsideWaitersKeepIndependentFinalOwners(
        [Matrix("GET", "SET", "factory", "cancel")] string outcome,
        [Matrix(false, true)] bool commandMetrics,
        [Matrix(false, true)] bool coalesce)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
                "GET key" => outcome == "GET" && !coalesce
                    ? "-WRONGTYPE read failed\r\n"u8.ToArray() : "$-1\r\n"u8.ToArray(),
                var text when text.StartsWith("SET ", StringComparison.Ordinal) => outcome == "SET"
                    ? "-WRONGTYPE write failed\r\n"u8.ToArray() : "$-1\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
            SuppressReply = command =>
            {
                if (command != "GET key" || outcome != "GET") return false;
                arrived.TrySetResult();
                return coalesce;
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, ClientSideCache = new() { CoalesceConcurrentMisses = coalesce },
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var factories = 0;
        async ValueTask<string?> Factory(CancellationToken token)
        {
            if (Interlocked.Increment(ref factories) == 2) secondFactory.TrySetResult();
            arrived.TrySetResult();
            await release.Task.WaitAsync(token);
            if (outcome == "factory") throw new InvalidOperationException("factory rejected");
            return "created";
        }
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        using var caller = new CancellationTokenSource();
        var first = client.GetOrSetAsync("key", Factory, TimeSpan.FromSeconds(10)).AsTask();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = client.GetOrSetAsync("key", Factory, TimeSpan.FromSeconds(10), caller.Token).AsTask();
        if (outcome == "cancel")
        {
            if (!coalesce) await secondFactory.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            var error = await Assert.That(async () => await second).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
            await Assert.That(first.IsCompleted).IsFalse();
            await Assert.That(capture.Items.Count).IsEqualTo(1);
            release.TrySetResult();
            await Assert.That(await first.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("created");
        }
        else
        {
            if (outcome == "GET" && coalesce)
                await server.SendRawAsync("-WRONGTYPE read failed\r\n"u8.ToArray());
            else release.TrySetResult();
            await Assert.That(async () => await first.WaitAsync(TimeSpan.FromSeconds(5))).Throws<Exception>();
            await Assert.That(async () => await second.WaitAsync(TimeSpan.FromSeconds(5))).Throws<Exception>();
        }
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(outcome == "cancel" ? 1 : 2);
        await Assert.That(items.All(item => !(bool)item.Tags["redis.client.errors.internal"]!)).IsTrue();
        await Assert.That(items.All(item => (int)item.Tags["redis.client.operation.retry_attempts"]! == 0)).IsTrue();
        await Assert.That(factories).IsEqualTo(outcome == "GET" ? 0 : coalesce ? 1 : 2);
    }

    [Test]
    [MatrixDataSource]
    public async Task TransactionTransportRetirementKeepsPendingAttempts(
        [Matrix(false, true)] bool failed,
        [Matrix(false, true)] bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldCount = 0;
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                var text when text.StartsWith("SET ", StringComparison.Ordinal) => "+QUEUED\r\n"u8.ToArray(),
                "EXEC" => failed ? "*1\r\n-WRONGTYPE item failed\r\n"u8.ToArray() : "*1\r\n+OK\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
            SuppressReply = command =>
            {
                if (command != "PING hold") return false;
                if (Interlocked.Increment(ref heldCount) == 2) arrived.TrySetResult();
                return true;
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2, MaxInflightCommands = 3,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var original = client.Core.Multiplexer.GetConnection();
        _ = client.Core.Multiplexer.GetConnection();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var held = original.SendAsync(new RawCommand("*2\r\n$4\r\nPING\r\n$4\r\nhold\r\n"u8.ToArray())).AsTask();
        var heldSecond = original.SendAsync(new RawCommand("*2\r\n$4\r\nPING\r\n$4\r\nhold\r\n"u8.ToArray())).AsTask();
        await arrived.Task.WaitAsync(deadline.Token);
        await using var transaction = client.CreateTransaction();
        var pending = transaction.Set("key", "value");
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var commit = transaction.CommitAsync(deadline.Token).AsTask();
        await Assert.That(commit.IsCompleted).IsFalse();
        var retirement = original.RetireAsync();
        await commit.WaitAsync(deadline.Token);
        var index = server.ReceivedCommands.ToList().IndexOf("PING hold");
        await server.SendRawAsync("+PONG\r\n+PONG\r\n"u8.ToArray(), server.ReceivedConnectionIds[index]);
        using var heldReply = await held.WaitAsync(deadline.Token);
        using var heldSecondReply = await heldSecond.WaitAsync(deadline.Token);
        await retirement.WaitAsync(deadline.Token);
        if (failed) await Assert.That(() => pending.Result).Throws<RespireServerException>();
        else await Assert.That(pending.Result).IsTrue();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(failed ? 2 : 1);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        if (failed)
        {
            await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
        await Assert.That(server.ReceivedCommands.Count(command => command == "MULTI")).IsEqualTo(1);
    }

    [Test]
    [MatrixDataSource]
    public async Task ValkeyPageConversionKeepsFinalOwner(
        [Matrix(0, 1, 2, 3)] int replyKind,
        [Matrix(false, true)] bool commandMetrics,
        [Matrix(false, true)] bool redirected,
        [Matrix(false, true)] bool prefixed)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        byte[] reply = replyKind switch
        {
            0 => "*1\r\n$1\r\n0\r\n"u8.ToArray(),
            1 => [.. "*2\r\n$1\r\n"u8, 255, .. "\r\n*0\r\n"u8],
            2 => "*2\r\n$1\r\n0\r\n*1\r\n:1\r\n"u8.ToArray(),
            _ => "*2\r\n$1\r\n0\r\n*0\r\n"u8.ToArray(),
        };
        await using var target = new FakeRespServer(reply);
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(redirected ? "*0\r\n"u8.ToArray() : topology,
            Encoding.ASCII.GetBytes($"-MOVED 0 127.0.0.1:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var view = prefixed ? client.WithKeyPrefix((RespireKey)new byte[] { 255, (byte)':' }) : client;
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (replyKind < 3)
            await Assert.That(async () => await view.Keys.ScanValkeyClusterPageAsync(cancellationToken: deadline.Token))
                .Throws<RespireProtocolException>();
        else
        {
            var page = await view.Keys.ScanValkeyClusterPageAsync(cancellationToken: deadline.Token);
            await Assert.That(page.IsComplete).IsTrue();
            await Assert.That(page.Keys.Count).IsEqualTo(0);
        }
        var items = capture.Items.ToArray();
        var final = items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(replyKind < 3 ? 1 : 0);
        if (replyKind < 3)
        {
            await Assert.That(final[0].Tags["redis.client.errors.category"]).IsEqualTo("other");
            await Assert.That(final[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(redirected ? 1 : 0);
        }
        await Assert.That(items.Count(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(redirected ? 1 : 0);
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith("CLUSTERSCAN ", StringComparison.Ordinal)))
            .IsEqualTo(1);
    }

    [Test]
    [Arguments("GET", false)]
    [Arguments("GET", true)]
    [Arguments("MGET", false)]
    [Arguments("MGET", true)]
    public async Task RawCoalescedReadViewRetainsTransportAttempts(string operation, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        var heldWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldCount = 0;
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "PING hold") return false;
                if (Interlocked.Increment(ref heldCount) == 2) heldWritten.TrySetResult();
                return true;
            },
            ReplyOverride = (_, command) => command == "HELLO 3"
                ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray()
                : command.StartsWith(operation + " ", StringComparison.Ordinal)
                    ? "-WRONGTYPE private-key\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2, MaxInflightCommands = 2,
            Endpoints = [new("127.0.0.1", server.Port)], ReplicaEndpoints = [new("127.0.0.1", server.Port)],
            ClientSideCache = new() { CoalesceConcurrentMisses = true },
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var policy = RespireReadFrom.PrimaryPreferred;
        var original = await client.Core.ReadRouter.GetConnectionAsync(policy, deadline.Token);
        _ = await client.Core.ReadRouter.GetConnectionAsync(policy, deadline.Token);
        // The cache-enabled transport admits only protocol commands outside the logical client.
        var held = original.SendCheckedAsync(new ProtocolCommand<RawCommand>(new("*2\r\n$4\r\nPING\r\n$4\r\nhold\r\n"u8.ToArray()))).AsTask();
        var secondHeld = original.SendCheckedAsync(new ProtocolCommand<RawCommand>(new("*2\r\n$4\r\nPING\r\n$4\r\nhold\r\n"u8.ToArray()))).AsTask();
        await heldWritten.Task.WaitAsync(deadline.Token);
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var view = client.WithReadFrom(policy);
        var pending = view.ExecuteAsync(operation, ["key"], cancellationToken: deadline.Token).AsTask();
        await Assert.That(pending.IsCompleted).IsFalse();
        var retirement = original.RetireAsync();
        try
        {
            await Assert.That(async () => { using var reply = await pending.WaitAsync(deadline.Token); })
                .Throws<RespireServerException>();
        }
        finally
        {
            var index = server.ReceivedCommands.ToList().IndexOf("PING hold");
            await server.SendRawAsync("+PONG\r\n+PONG\r\n"u8.ToArray(), server.ReceivedConnectionIds[index]);
            using var reply = await held.WaitAsync(deadline.Token);
            using var secondReply = await secondHeld.WaitAsync(deadline.Token);
            await retirement.WaitAsync(deadline.Token);
        }
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command == operation + " key")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ScriptingConversionPreservesOwnedAndBorrowedAttempts(bool borrowed, bool conversionFailure)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(conversionFailure ? ":1\r\n"u8.ToArray()
            : "-ERR ordinary-script-error\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var observation = borrowed ? RespireTelemetry.ErrorObservation.Rent(force: true) : default;
        observation.SetAttempts(3);
        var conversionError = new InvalidOperationException("private-conversion-error");
        using var capture = new Capture(throwOnMeasurement: true);
        var error = await Assert.That(async () => await client.Core.Multiplexer.GetConnection().SendConvertedAsync<Cmd2, InvalidOperationException, long>(
            new Cmd2(Verbs.Eval, "return 1", 0), conversionError,
            static (InvalidOperationException error, in RespValue _) => throw error,
            transferOwnership: false, commandName: "EVAL", errorAttempts: 3, observation: observation)).Throws<Exception>();
        if (conversionFailure) await Assert.That(error).IsSameReferenceAs(conversionError);
        else await Assert.That(error).IsTypeOf<RespireServerException>();
        if (borrowed)
        {
            await Assert.That(capture.Items.Count).IsEqualTo(0);
            await Assert.That(observation!.Attempts).IsEqualTo(3);
            observation.Final(error!);
        }
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(1);
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(3);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ScriptingConversionRetainsOneFinalMetricOwner(bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer("-ERR ordinary-script-error\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var error = await Assert.That(async () => await client.IntegerAsync("EVAL",
            new Cmd2(Verbs.Eval, "return 1", 0), default)).Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("ERR");
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(1);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[0].Tags["redis.client.errors.category"]).IsEqualTo("server");
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["EVAL return 1 0"]);
    }

    [Test]
    public async Task CoalescedCancellationKeepsProducerAttemptsAfterCallerLeaseReturn()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var cache = new ClientSideCacheCoordinator(new() { CoalesceConcurrentMisses = true });
        var identity = new ClientCacheCommandKey("GET", "key");
        var release = new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var capture = new Capture(throwOnMeasurement: true);
        Task<RespValue> Read(CancellationToken token)
        {
            var caller = RespireTelemetry.ErrorObservation.Rent(force: true);
            return RespireTelemetry.ObserveFinalError(cache.CoalesceReadAsync(identity, release,
                static (source, _, producer) =>
                {
                    producer!.Handled(new RespireServerException("MOVED 1 private-host:6379"));
                    return new ValueTask<RespValue>(source.Task);
                }, token, caller), caller).AsTask();
        }
        var first = Read(cancellation.Token);
        var second = Read(default);
        try
        {
            cancellation.Cancel();
            var canceled = await Assert.That(async () =>
                { using var value = await first.WaitAsync(TimeSpan.FromSeconds(5)); }).Throws<OperationCanceledException>();
            await Assert.That(canceled!.CancellationToken).IsEqualTo(cancellation.Token);
            await Assert.That(second.IsCompleted).IsFalse();
            // A different operation can reuse the returned caller lease while the producer
            // remains pending. Its count must never become the remaining waiter's count.
            using var unrelated = RespireTelemetry.ErrorObservation.Rent(force: true);
            unrelated.SetAttempts(7);
            var expected = new RespireServerException("WRONGTYPE private-key");
            release.SetException(expected);
            var failure = await Assert.That(async () =>
                { using var value = await second.WaitAsync(TimeSpan.FromSeconds(5)); }).Throws<RespireServerException>();
            await Assert.That(failure).IsSameReferenceAs(expected);
            var items = capture.Items.ToArray();
            await Assert.That(items.Length).IsEqualTo(3);
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            foreach (var item in items.Skip(1))
            {
                await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
                await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
            }
        }
        finally { release.TrySetException(new OperationCanceledException()); cache.StopSharedReads(); }
    }

    [Test]
    [MatrixDataSource]
    public async Task CachedReadRetainsProducerRedirectForEachCaller(
        [Matrix(false, true)] bool coalesce, [Matrix(false, true)] bool raw,
        [Matrix(false, true)] bool many, [Matrix(0, 1, 2, 3)] int selection,
        [Matrix(false, true)] bool succeeds)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = selection >= 2
            ? RespireMetricGroups.None : RespireMetricGroups.Resiliency
                | (selection == 1 ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        var operation = many ? "MGET" : "GET";
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "CLUSTER SLOTS" => "*0\r\n"u8.ToArray(),
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                _ when command.StartsWith(operation + " ", StringComparison.Ordinal) => succeeds
                    ? (many ? "*2\r\n$1\r\na\r\n$1\r\nb\r\n"u8.ToArray() : "$1\r\na\r\n"u8.ToArray())
                    : "-WRONGTYPE private-key\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var source = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "CLUSTER SLOTS" => "*0\r\n"u8.ToArray(),
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
            SuppressReply = command =>
            {
                if (!command.StartsWith(operation + " ", StringComparison.Ordinal)) return false;
                arrived.TrySetResult();
                return true;
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            ClientSideCache = new() { CoalesceConcurrentMisses = coalesce },
            Endpoints = [new("127.0.0.1", source.Port)],
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: selection == 1);
        async Task Read()
        {
            if (raw)
            {
                using var reply = many
                    ? await client.ExecuteAsync("MGET", ["{same}:one", "{same}:two"], cancellationToken: timeout.Token)
                    : await client.ExecuteAsync("GET", ["{same}:one"], cancellationToken: timeout.Token);
            }
            else if (many) await client.Strings.GetManyAsync(["{same}:one", "{same}:two"], timeout.Token);
            else await client.GetStringAsync("{same}:one", timeout.Token);
        }
        var first = Read();
        await arrived.Task.WaitAsync(timeout.Token);
        var second = coalesce ? Read() : null;
        if (selection == 2) RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        var index = source.ReceivedCommands.ToList().FindIndex(command => command.StartsWith(operation + " ", StringComparison.Ordinal));
        await source.SendRawAsync(Encoding.UTF8.GetBytes($"-MOVED {ClusterHash.GetSlot("{same}:one")} 127.0.0.1:{target.Port}\r\n"),
            source.ReceivedConnectionIds[index]);
        if (succeeds)
        {
            await first.WaitAsync(timeout.Token);
            if (second is not null) await second.WaitAsync(timeout.Token);
        }
        else
        {
            await Assert.That(async () => await first.WaitAsync(timeout.Token)).Throws<RespireServerException>();
            if (second is not null)
                await Assert.That(async () => await second.WaitAsync(timeout.Token)).Throws<RespireServerException>();
        }
        var items = capture.Items.ToArray();
        await Assert.That(items.Count(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(selection == 3 ? 0 : 1);
        var final = items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(succeeds || selection == 3 ? 0 : coalesce ? 2 : 1);
        foreach (var item in final)
        {
            await Assert.That(item.Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
            await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
        await Assert.That(source.ReceivedCommands.Count(command => command.StartsWith(operation + " ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith(operation + " ", StringComparison.Ordinal))).IsEqualTo(1);
        capture.Items.Clear();
        // A later caller cannot inherit a returned producer/caller lease's attempt count.
        // Failed reads do not insert an entry, so this operation reaches the replacement.
        if (!succeeds)
        {
            await Assert.That(async () => await Read().WaitAsync(timeout.Token)).Throws<RespireServerException>();
            var later = capture.Items.ToArray();
            await Assert.That(later.Length).IsEqualTo(selection == 3 ? 0 : 1);
            if (later.Length != 0)
                await Assert.That(later[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
    }

    [Test]
    [MatrixDataSource]
    public async Task ConnectAnyPreflightHasOneFinalOwner(
        [Matrix("null", "empty", "null-entry", "iterator")] string failure,
        [Matrix(0, 1, 2)] int selection)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = selection == 2
            ? RespireMetricGroups.None : RespireMetricGroups.Resiliency
                | (selection == 1 ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: selection == 1);
        IEnumerable<RespireOptions> ThrowingIterator()
        {
            yield return null!;
            throw new InvalidOperationException("private-candidate");
        }
        IEnumerable<RespireOptions> candidates = failure switch
        {
            "null" => null!,
            "empty" => [],
            "null-entry" => [null!],
            _ => ThrowingIterator().Skip(1),
        };
        Exception? error = null;
        try { await using var client = await RespireClient.ConnectAnyAsync(candidates); }
        catch (Exception caught) { error = caught; }
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.GetType()).IsEqualTo(failure switch
        {
            "null" => typeof(ArgumentNullException),
            "empty" => typeof(RespireConnectionException),
            "null-entry" => typeof(ArgumentException),
            _ => typeof(InvalidOperationException),
        });
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(selection == 2 ? 0 : 1);
        if (selection == 2) return;
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(items[0].Tags["error.type"]).IsEqualTo(error.GetType().FullName);
        await Assert.That(items[0].Tags.Values.Contains("private-candidate")).IsFalse();
    }

    [Test]
    [MatrixDataSource]
    public async Task ConnectAnyPreflightRetainsCompletedFailures(
        [Matrix("null-entry", "iterator", "exhausted", "success")] string outcome,
        [Matrix(1, 2)] int failures,
        [Matrix(0, 1, 2)] int selection)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = selection == 2
            ? RespireMetricGroups.None : RespireMetricGroups.Resiliency
                | (selection == 1 ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var failed = new FakeRespServer(failures, "-WRONGPASS private-secret\r\n"u8.ToArray());
        await using var healthy = new FakeRespServer(FakeRespServer.OkReply);
        var failedOptions = new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Password = "private-secret", Endpoints = [new("127.0.0.1", failed.Port)],
        };
        IEnumerable<RespireOptions> Candidates()
        {
            for (var i = 0; i < failures; i++) yield return failedOptions;
            if (outcome == "null-entry") yield return null!;
            if (outcome == "iterator") throw new InvalidOperationException("private-candidate");
            if (outcome == "success") yield return new RespireOptions
            {
                Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", healthy.Port)],
            };
        }
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: selection == 1);
        Exception? error = null;
        try { await using var client = await RespireClient.ConnectAnyAsync(Candidates()); }
        catch (Exception caught) { error = caught; }
        await Assert.That(error is null).IsEqualTo(outcome == "success");
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(selection == 2 ? 0 : failures + (error is null ? 0 : 1));
        if (selection == 2) return;
        for (var i = 0; i < failures; i++)
        {
            await Assert.That((bool)items[i].Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(items[i].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(i);
            await Assert.That(items[i].Tags["db.response.status_code"]).IsEqualTo("WRONGPASS");
        }
        if (error is null) return;
        await Assert.That((bool)items[^1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[^1].Tags["redis.client.operation.retry_attempts"])
            .IsEqualTo(outcome == "exhausted" ? failures - 1 : failures);
    }

    [Test]
    [MatrixDataSource]
    public async Task StandaloneBatchRetainsEachPendingTransportRetry(
        [Matrix(2, 3)] int protocol,
        [Matrix(0, 1, 2, 3)] int selection)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = selection >= 2
            ? RespireMetricGroups.None : RespireMetricGroups.Resiliency
                | (selection == 1 ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        var heldWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "PING hold") return false;
                heldWritten.TrySetResult();
                return true;
            },
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "GET private-one" or "GET private-two" => "-WRONGTYPE private-key\r\n"u8.ToArray(),
                "GET private-ok" => "$2\r\nok\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, Connections = 2, MaxInflightCommands = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var original = client.Core.Multiplexer.GetConnection();
        _ = client.Core.Multiplexer.GetConnection();
        var held = original.SendCheckedAsync(new RawCommand("*2\r\n$4\r\nPING\r\n$4\r\nhold\r\n"u8.ToArray())).AsTask();
        await heldWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: selection == 1);
        using var batch = client.CreateBatch();
        var first = batch.GetString("private-one");
        var second = batch.GetString("private-two");
        var succeeded = batch.GetString("private-ok");
        var execution = batch.TryExecuteAsync().AsTask();
        await Assert.That(execution.IsCompleted).IsFalse();
        if (selection == 2) RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        var retirement = original.RetireAsync();
        try
        {
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(result.FailureCount).IsEqualTo(2);
            await Assert.That(result.ThrowIfAnyFailed).Throws<RespireServerException>();
            await Assert.That(succeeded.Result).IsEqualTo("ok");
            await Assert.That(() => first.Result).Throws<RespireServerException>();
            await Assert.That(() => first.Result).Throws<RespireServerException>();
            await Assert.That(() => second.Result).Throws<RespireServerException>();
        }
        finally
        {
            var index = server.ReceivedCommands.ToList().IndexOf("PING hold");
            await server.SendRawAsync(FakeRespServer.PongReply, server.ReceivedConnectionIds[index]);
            using var reply = await held.WaitAsync(TimeSpan.FromSeconds(5));
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(selection == 3 ? 0 : 5);
        if (selection != 3)
        {
            var internalItems = items.Where(item => (bool)item.Tags["redis.client.errors.internal"]!).ToArray();
            var finalItems = items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
            await Assert.That(internalItems.Length).IsEqualTo(3);
            await Assert.That(finalItems.Length).IsEqualTo(2);
            foreach (var item in internalItems)
                await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            foreach (var item in finalItems)
            {
                await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
                await Assert.That(item.Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
            }
        }
        using var nextBatch = client.CreateBatch();
        var nextFailure = nextBatch.GetString("private-one");
        var nextSuccess = nextBatch.GetString("private-ok");
        var nextResult = await nextBatch.TryExecuteAsync();
        await Assert.That(nextResult.FailureCount).IsEqualTo(1);
        await Assert.That(() => nextFailure.Result).Throws<RespireServerException>();
        await Assert.That(nextSuccess.Result).IsEqualTo("ok");
        var nextItems = capture.Items.Skip(items.Length).ToArray();
        await Assert.That(nextItems.Length).IsEqualTo(selection == 3 ? 0 : 1);
        if (selection != 3)
        {
            await Assert.That((bool)nextItems[0].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(nextItems[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("GET ", StringComparison.Ordinal)))
            .IsEqualTo(5);
    }

    [Test]
    [MatrixDataSource]
    public async Task PreferredReadHandshakeFailureRetainsInternalObservation(
        [Matrix(2, 3)] int protocol,
        [Matrix(RespireReadFrom.PrimaryPreferred, RespireReadFrom.ReplicaPreferred)] RespireReadFrom policy,
        [Matrix(0, 1, 2)] int selection,
        [Matrix("peer", "authentication", "refused")] string failure,
        [Matrix("success", "rejection", "cancellation")] string outcome)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = selection == 2
            ? RespireMetricGroups.None : RespireMetricGroups.Resiliency
                | (selection == 1 ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var failed = new FakeRespServer(FakeRespServer.OkReply)
        {
            CloseConnectionAfterCommand = failure == "peer" ? 1 : null,
            ReplyOverride = (_, command) => command.StartsWith("AUTH", StringComparison.Ordinal)
                || command.StartsWith("HELLO", StringComparison.Ordinal)
                ? "-WRONGPASS private-password\r\n"u8.ToArray() : null,
        };
        await using var fallback = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                var hello when hello.StartsWith("HELLO 3", StringComparison.Ordinal)
                    => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "ROLE" => "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray(),
                "GET private-key" => outcome == "rejection" ? "-WRONGTYPE private-key\r\n"u8.ToArray()
                    : "$2\r\nok\r\n"u8.ToArray(),
                _ => null,
            },
        };
        var primary = policy == RespireReadFrom.PrimaryPreferred ? failed : fallback;
        var replica = policy == RespireReadFrom.PrimaryPreferred ? fallback : failed;
        if (failure == "refused") await failed.DisposeAsync();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = (RespProtocol)protocol, Connections = 1, ClientName = "private-name", Password = "private-password",
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", replica.Port)], ReadFrom = policy,
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (outcome == "cancellation") deadline.Cancel();
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: selection == 1);
        if (outcome == "success")
            await Assert.That(await client.GetStringAsync("private-key", deadline.Token)).IsEqualTo("ok");
        else if (outcome == "rejection")
            await Assert.That(async () => await client.GetStringAsync("private-key", deadline.Token))
                .Throws<RespireServerException>();
        else
        {
            var error = await Assert.That(async () => await client.GetStringAsync("private-key", deadline.Token))
                .Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(deadline.Token);
        }

        var items = capture.Items.ToArray();
        var recovered = items.Where(item => (bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        var final = items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(recovered.Length).IsEqualTo(selection == 2 || outcome == "cancellation" ? 0 : 1);
        await Assert.That(final.Length).IsEqualTo(selection == 2 || outcome == "success" ? 0 : 1);
        if (recovered.Length != 0)
        {
            await Assert.That(recovered[0].Tags["redis.client.errors.category"])
                .IsEqualTo(failure == "authentication" ? "auth" : "network");
            await Assert.That(recovered[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        if (outcome == "rejection" && final.Length != 0)
        {
            await Assert.That(final[0].Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
            await Assert.That(final[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
        await Assert.That(items.SelectMany(item => item.Tags.Values)
            .Any(value => value?.ToString()?.Contains("private-") == true)).IsFalse();
        await Assert.That(failed.ReceivedCommands.Count(command => command == "GET private-key")).IsEqualTo(0);
        await Assert.That(fallback.ReceivedCommands.Count(command => command == "GET private-key"))
            .IsEqualTo(outcome == "cancellation" ? 0 : 1);
    }

    [Test]
    [Arguments(null, false)]
    [Arguments("", false)]
    [Arguments(" ", false)]
    [Arguments("localhost:invalid", false)]
    [Arguments("redis://localhost?protocol=oops", false)]
    [Arguments("redis://localhost?db=private-database", false)]
    [Arguments("localhost,ssl=private-boolean", false)]
    [Arguments("localhost,asyncTimeout=2147483648", false)]
    [Arguments(null, true)]
    [Arguments("", true)]
    [Arguments(" ", true)]
    [Arguments("localhost:invalid", true)]
    [Arguments("redis://localhost?protocol=oops", true)]
    [Arguments("redis://localhost?db=private-database", true)]
    [Arguments("localhost,ssl=private-boolean", true)]
    [Arguments("localhost,asyncTimeout=2147483648", true)]
    public async Task ConnectionStringParseFailureHasOneFinalObservation(string? connectionString, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : 0) });
        var expected = Assert.Throws<ArgumentException>(() => RespireOptions.Parse(connectionString!));
        using var capture = new Capture(throwOnMeasurement: commandMetrics, commandMetrics: commandMetrics);

        // Parsing must still throw synchronously, before a ValueTask is returned.
        var error = Assert.Throws<ArgumentException>(() => { _ = RespireClient.ConnectAsync(connectionString!); });
        await Assert.That(error.GetType()).IsEqualTo(expected.GetType());
        await Assert.That(error.ParamName).IsEqualTo(expected.ParamName);
        await Assert.That(error.Message).IsEqualTo(expected.Message);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        var item = capture.Items.Single();
        await Assert.That(item.Value).IsEqualTo(1L);
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(item.Tags["redis.client.errors.category"]).IsEqualTo("other");
        await Assert.That(item.Tags["error.type"]).IsEqualTo(error.GetType().FullName);
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(item.Tags.Values.Any(value => value?.ToString()?.Contains("private-") == true)).IsFalse();
    }

    [Test]
    [Arguments("auth", false)]
    [Arguments("cancellation", false)]
    [Arguments("success", false)]
    [Arguments("auth", true)]
    [Arguments("cancellation", true)]
    [Arguments("success", true)]
    public async Task ParsedConnectionDelegatesItsFinalObservation(string outcome, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : 0) });
        await using var server = new FakeRespServer(outcome == "auth"
            ? "-WRONGPASS private-password\r\n"u8.ToArray() : FakeRespServer.OkReply);
        using var cancellation = new CancellationTokenSource();
        if (outcome == "cancellation") cancellation.Cancel();
        using var capture = new Capture(throwOnMeasurement: commandMetrics, commandMetrics: commandMetrics);
        var connectionString = $"127.0.0.1:{server.Port},protocol=2";
        if (outcome == "auth") connectionString += ",password=private-password";

        async Task Connect()
        {
            await using var client = await RespireClient.ConnectAsync(connectionString, cancellation.Token);
        }

        if (outcome == "auth") await Assert.That(Connect).Throws<RespireException>();
        else if (outcome == "cancellation")
        {
            var error = await Assert.That(Connect).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        else await Connect();

        if (outcome == "success") await Assert.That(capture.Items.Count).IsEqualTo(0);
        else
        {
            var item = capture.Items.Single();
            await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            await Assert.That(item.Tags["redis.client.errors.category"]).IsEqualTo(outcome switch { "auth" => "auth", "cancellation" => "cancelled", _ => "other" });
            await Assert.That(item.Tags.Values.Any(value => value?.ToString()?.Contains("private-") == true)).IsFalse();
        }
    }

    [Test]
    [Arguments("EVALSHA", "raw", 0)]
    [Arguments("EVALSHA", "catalog", 0)]
    [Arguments("EVALSHA", "interpolated", 0)]
    [Arguments("FCALL", "raw", 0)]
    [Arguments("FCALL", "catalog", 0)]
    [Arguments("FCALL", "interpolated", 0)]
    [Arguments("FCALL_RO", "raw", 0)]
    [Arguments("FCALL_RO", "catalog", 0)]
    [Arguments("FCALL_RO", "interpolated", 0)]
    [Arguments("EVALSHA", "raw", 1)]
    [Arguments("EVALSHA", "catalog", 1)]
    [Arguments("EVALSHA", "interpolated", 1)]
    [Arguments("FCALL", "raw", 1)]
    [Arguments("FCALL", "catalog", 1)]
    [Arguments("FCALL", "interpolated", 1)]
    [Arguments("FCALL_RO", "raw", 1)]
    [Arguments("FCALL_RO", "catalog", 1)]
    [Arguments("FCALL_RO", "interpolated", 1)]
    [Arguments("EVALSHA", "raw", 2)]
    [Arguments("EVALSHA", "catalog", 2)]
    [Arguments("EVALSHA", "interpolated", 2)]
    [Arguments("FCALL", "raw", 2)]
    [Arguments("FCALL", "catalog", 2)]
    [Arguments("FCALL", "interpolated", 2)]
    [Arguments("FCALL_RO", "raw", 2)]
    [Arguments("FCALL_RO", "catalog", 2)]
    [Arguments("FCALL_RO", "interpolated", 2)]
    public async Task RawProcedureOwnershipRetainsRetries(string operation, string path, int selection)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = selection == 2
            ? RespireMetricGroups.None : RespireMetricGroups.Resiliency
                | (selection == 1 ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        var heldWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "PING hold") return false;
                heldWritten.TrySetResult();
                return true;
            },
            ReplyOverride = (_, command) => command.StartsWith(operation + " ", StringComparison.Ordinal)
                ? "-WRONGTYPE private-procedure\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2, MaxInflightCommands = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var original = client.Core.Multiplexer.GetConnection();
        _ = client.Core.Multiplexer.GetConnection();
        var held = original.SendCheckedAsync(new RawCommand("*2\r\n$4\r\nPING\r\n$4\r\nhold\r\n"u8.ToArray())).AsTask();
        await heldWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var earlyCapture = selection == 2 ? null : new Capture(throwOnMeasurement: true, commandMetrics: selection == 1);
        var pending = ExecuteRawProcedure(client, operation, path).AsTask();
        await Assert.That(pending.IsCompleted).IsFalse();
        using var lateCapture = selection == 2 ? new Capture(throwOnMeasurement: true) : null;
        if (selection == 2) RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        var retirement = original.RetireAsync();
        try
        {
            var error = await Assert.That(async () => { using var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); })
                .Throws<RespireServerException>();
            await Assert.That(error!.Code).IsEqualTo("WRONGTYPE");
        }
        finally
        {
            var index = server.ReceivedCommands.ToList().IndexOf("PING hold");
            await server.SendRawAsync(FakeRespServer.PongReply, server.ReceivedConnectionIds[index]);
            using var reply = await held.WaitAsync(TimeSpan.FromSeconds(5));
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var items = (earlyCapture ?? lateCapture)!.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[0].Tags["error.type"]).IsEqualTo(typeof(RespireConnectionRetiredException).FullName);
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith(operation + " ", StringComparison.Ordinal)))
            .IsEqualTo(1);
    }

    [Test]
    [Arguments("EVALSHA", "raw")]
    [Arguments("EVALSHA", "catalog")]
    [Arguments("EVALSHA", "interpolated")]
    [Arguments("FCALL", "raw")]
    [Arguments("FCALL", "catalog")]
    [Arguments("FCALL", "interpolated")]
    [Arguments("FCALL_RO", "raw")]
    [Arguments("FCALL_RO", "catalog")]
    [Arguments("FCALL_RO", "interpolated")]
    public async Task RawProcedureOwnershipIncludesDisposal(string operation, string path)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await client.DisposeAsync();
        using var capture = new Capture(throwOnMeasurement: true);
        await Assert.That(async () => { using var result = await ExecuteRawProcedure(client, operation, path); })
            .Throws<ObjectDisposedException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(capture.Items.Single().Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith(operation + " ", StringComparison.Ordinal)))
            .IsFalse();
    }

    [Test]
    [Arguments("raw", false)]
    [Arguments("catalog", false)]
    [Arguments("interpolated", false)]
    [Arguments("raw", true)]
    [Arguments("catalog", true)]
    [Arguments("interpolated", true)]
    public async Task RawProcedureOwnershipDoesNotReportSuccess(string path, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(4, ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        foreach (var operation in new[] { "EVALSHA", "FCALL", "FCALL_RO" })
        {
            using var result = await ExecuteRawProcedure(client, operation, path);
            await Assert.That(result.AsInteger()).IsEqualTo(1);
        }
        await Assert.That(capture.Items.Count).IsEqualTo(0);
    }

    private static ValueTask<RespireResult> ExecuteRawProcedure(RespireClient client, string operation, string path)
    {
        if (path == "raw") return client.ExecuteAsync(operation, "procedure", 0);
        if (path == "catalog") return client.ExecuteAsync(operation switch
        {
            "EVALSHA" => RespireCommands.Scripting.EVALSHA,
            "FCALL" => RespireCommands.Scripting.FCALL,
            _ => RespireCommands.Scripting.FCALL_RO,
        }, "procedure", 0);
        return operation switch
        {
            "EVALSHA" => client.ExecuteAsync($"EVALSHA {"procedure"} {0}"),
            "FCALL" => client.ExecuteAsync($"FCALL {"procedure"} {0}"),
            _ => client.ExecuteAsync($"FCALL_RO {"procedure"} {0}"),
        };
    }

    [Test]
    [Arguments("DBSIZE", "disposed", false)]
    [Arguments("FLUSHDB", "disposed", false)]
    [Arguments("FLUSHALL", "disposed", false)]
    [Arguments("DBSIZE", "topology", false)]
    [Arguments("FLUSHDB", "topology", false)]
    [Arguments("FLUSHALL", "topology", false)]
    [Arguments("DBSIZE", "overflow", false)]
    [Arguments("FLUSHDB", "malformed", false)]
    [Arguments("FLUSHALL", "malformed", false)]
    [Arguments("DBSIZE", "server", false)]
    [Arguments("FLUSHDB", "server", false)]
    [Arguments("FLUSHALL", "server", false)]
    [Arguments("DBSIZE", "cancel", false)]
    [Arguments("FLUSHDB", "cancel", false)]
    [Arguments("FLUSHALL", "cancel", false)]
    [Arguments("DBSIZE", "success", false)]
    [Arguments("FLUSHDB", "success", false)]
    [Arguments("FLUSHALL", "success", false)]
    [Arguments("DBSIZE", "disposed", true)]
    [Arguments("FLUSHDB", "disposed", true)]
    [Arguments("FLUSHALL", "disposed", true)]
    [Arguments("DBSIZE", "topology", true)]
    [Arguments("FLUSHDB", "topology", true)]
    [Arguments("FLUSHALL", "topology", true)]
    [Arguments("DBSIZE", "overflow", true)]
    [Arguments("FLUSHDB", "malformed", true)]
    [Arguments("FLUSHALL", "malformed", true)]
    [Arguments("DBSIZE", "server", true)]
    [Arguments("FLUSHDB", "server", true)]
    [Arguments("FLUSHALL", "server", true)]
    [Arguments("DBSIZE", "cancel", true)]
    [Arguments("FLUSHDB", "cancel", true)]
    [Arguments("FLUSHALL", "cancel", true)]
    [Arguments("DBSIZE", "success", true)]
    [Arguments("FLUSHDB", "success", true)]
    [Arguments("FLUSHALL", "success", true)]
    public async Task ServerFanOutOwnershipIncludesDiscoveryAndConversion(string operation, string outcome, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        var response = outcome == "server" ? "-NOPERM private-server\r\n"u8.ToArray()
            : outcome == "malformed" ? "*0\r\n"u8.ToArray()
            : outcome == "overflow" ? ":9223372036854775807\r\n"u8.ToArray()
            : operation == "DBSIZE" ? ":3\r\n"u8.ToArray() : FakeRespServer.OkReply;
        await using var first = new FakeRespServer(4, response);
        await using var second = new FakeRespServer(4, response);
        var topology = outcome == "topology" ? "*0\r\n"u8.ToArray() : Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        await using var seed = new FakeRespServer(4, topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, AllowAdmin = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        if (outcome == "disposed") await client.DisposeAsync();
        using var cancellation = new CancellationTokenSource();
        if (outcome == "cancel") cancellation.Cancel();
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        async Task Execute()
        {
            if (operation == "DBSIZE")
            {
                var size = await client.Server.DatabaseSizeAsync(cancellation.Token);
                if (outcome == "success") await Assert.That(size).IsEqualTo(6);
            }
            else if (operation == "FLUSHDB") await client.Server.FlushDatabaseAsync(ServerFlushMode.Sync, cancellation.Token);
            else await client.Server.FlushAllAsync(ServerFlushMode.Async, cancellation.Token);
        }
        Exception? error = null;
        if (outcome == "success") await Execute();
        else if (outcome == "disposed") error = await Assert.That(Execute).Throws<ObjectDisposedException>();
        else if (outcome == "cancel") error = await Assert.That(Execute).Throws<OperationCanceledException>();
        else if (outcome == "topology") error = await Assert.That(Execute).Throws<RespireConnectionException>();
        else if (outcome == "server") error = await Assert.That(Execute).Throws<RespireServerException>();
        else if (outcome == "overflow") error = await Assert.That(Execute).Throws<OverflowException>();
        else error = await Assert.That(Execute).Throws<RespireException>();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(error is null ? 0 : 1);
        if (error is not null)
        {
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[0].Tags["error.type"]).IsEqualTo(error.GetType().FullName);
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            if (error is OperationCanceledException cancelled)
                await Assert.That(cancelled.CancellationToken).IsEqualTo(cancellation.Token);
        }
        if (outcome == "success")
        {
            var expected = operation == "DBSIZE" ? "DBSIZE" : operation == "FLUSHDB" ? "FLUSHDB SYNC" : "FLUSHALL ASYNC";
            await Assert.That(first.ReceivedCommands.Count(command => command == expected)).IsEqualTo(1);
            await Assert.That(second.ReceivedCommands.Count(command => command == expected)).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments("lease", false)]
    [Arguments("script", false)]
    [Arguments("success", false)]
    [Arguments("lease", true)]
    [Arguments("script", true)]
    [Arguments("success", true)]
    public async Task RemovalOwnershipRetainsLeaseAndScriptRedirects(string outcome, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var target = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("SET ", StringComparison.Ordinal)
                ? outcome == "lease" ? "-NOPERM private-lease\r\n"u8.ToArray() : FakeRespServer.OkReply
                : command.StartsWith("EVAL ", StringComparison.Ordinal)
                    ? outcome == "script" ? "-WRONGTYPE private-removal\r\n"u8.ToArray() : ":1\r\n"u8.ToArray() : null,
        };
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(4, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n");
        seed.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology
            : command.StartsWith(outcome == "script" ? "EVAL " : "SET ", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n") : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        if (outcome == "success") await client.UnlinkGuardedAsync("key", default);
        else await Assert.That(async () => await client.UnlinkGuardedAsync("key", default)).Throws<RespireServerException>();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(outcome == "success" ? 1 : 2);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("MOVED");
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        if (outcome != "success")
        {
            await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
        if (outcome == "lease") await Assert.That(target.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("string", false, false)]
    [Arguments("bytes", false, false)]
    [Arguments("integer", false, false)]
    [Arguments("converter", false, false)]
    [Arguments("string", true, false)]
    [Arguments("bytes", true, false)]
    [Arguments("integer", true, false)]
    [Arguments("converter", true, false)]
    [Arguments("string", false, true)]
    [Arguments("bytes", false, true)]
    [Arguments("integer", false, true)]
    [Arguments("converter", false, true)]
    [Arguments("string", true, true)]
    [Arguments("bytes", true, true)]
    [Arguments("integer", true, true)]
    [Arguments("converter", true, true)]
    public async Task ReadyClusterRetriesHaveOneConversionOwner(string shape, bool retry, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        var reply = shape == "converter" ? "$5\r\nvalue\r\n"u8.ToArray() : "-WRONGTYPE private-key\r\n"u8.ToArray();
        await using var target = new FakeRespServer(8, reply);
        await using var seed = new FakeRespServer(8, reply);
        var slot = ClusterHash.GetSlot("key");
        var slots = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n");
        seed.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? slots
            : retry && (command.StartsWith("GET ", StringComparison.Ordinal) || command.StartsWith("STRLEN ", StringComparison.Ordinal))
                ? Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n") : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.That(client.Core.Cluster!.TryAcquireReadyConnection(slot, deadline.Token)).IsNotNull();
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        await Assert.That(RespireTelemetry.IsOperationEnabled("GET")).IsEqualTo(commandMetrics);
        var expected = new RespireServerException("MOVED private-converter");
        var error = await Assert.That(async () =>
        {
            switch (shape)
            {
                case "string": await client.GetStringAsync("key", deadline.Token); break;
                case "bytes": await client.GetBytesAsync("key", deadline.Token); break;
                case "integer": await client.Strings.LengthAsync("key", deadline.Token); break;
                case "converter":
                    await client.ConvertResponseAsync("GET", new Cmd1(Verbs.Get, "key"), deadline.Token, expected,
                        static (RespireServerException failure, in RespValue _) => ThrowConversion(failure));
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(shape));
            }
        }).Throws<RespireServerException>();
        if (shape == "converter") await Assert.That(ReferenceEquals(error, expected)).IsTrue();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(retry ? 2 : 1);
        var final = items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["db.response.status_code"]).IsEqualTo(shape == "converter" ? "MOVED" : "WRONGTYPE");
        await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(retry ? 1 : 0);
        await Assert.That(seed.ReceivedCommands.Count(command => command.StartsWith("GET ", StringComparison.Ordinal)
            || command.StartsWith("STRLEN ", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands.Count(command => command.StartsWith("GET ", StringComparison.Ordinal)
            || command.StartsWith("STRLEN ", StringComparison.Ordinal))).IsEqualTo(retry ? 1 : 0);
    }

    [Test]
    [Arguments("disposed", false)]
    [Arguments("disposed", true)]
    [Arguments("cancelled", false)]
    [Arguments("cancelled", true)]
    [Arguments("watch", false)]
    [Arguments("watch", true)]
    [Arguments("success", false)]
    [Arguments("success", true)]
    public async Task WatchedTransactionSetupHasOneFinalOwner(string outcome, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => outcome == "watch" && command.StartsWith("WATCH ", StringComparison.Ordinal)
                ? "-NOPERM private-key\r\n"u8.ToArray() : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (outcome == "disposed") await client.DisposeAsync();
        if (outcome == "cancelled") cancellation.Cancel();
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        async Task Execute() { await using var transaction = await client.CreateTransactionAsync(["key"], cancellation.Token); }
        Exception? error = null;
        if (outcome == "success") await Execute();
        else error = await Assert.That(Execute).Throws<Exception>();
        var final = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(outcome == "success" ? 0 : 1);
        if (error is not null)
        {
            await Assert.That(final[0].Tags["error.type"]).IsEqualTo(error.GetType().FullName);
            await Assert.That(final[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
            if (error is OperationCanceledException cancelled)
                await Assert.That(cancelled.CancellationToken).IsEqualTo(cancellation.Token);
        }
    }

    [Test]
    [Arguments("GET", false)]
    [Arguments("GET", true)]
    [Arguments("MGET", false)]
    [Arguments("MGET", true)]
    public async Task RawCoalescedReadViewFailureHasOneFinalOwner(string operation, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3"
                ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray()
                : command.StartsWith(operation + " ", StringComparison.Ordinal)
                    ? "-WRONGTYPE private-key\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)],
            ReplicaEndpoints = [new("127.0.0.1", server.Port)],
            ClientSideCache = new() { CoalesceConcurrentMisses = true },
        });
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var view = client.WithReadFrom(RespireReadFrom.PrimaryPreferred);
        await Assert.That(async () => { using var result = await view.ExecuteAsync(operation, "key"); })
            .Throws<RespireServerException>();
        var final = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(1);
        await Assert.That(final[0].Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
        await Assert.That(server.ReceivedCommands.Count(command => command == operation + " key")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task HedgeOriginalRetirementKeepsItsFinalAttempts(bool cluster, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var replica = new FakeRespServer(16,
            "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "GET key" ? "-WRONGTYPE private-key\r\n"u8.ToArray() : null,
        };
        await using var primary = new FakeRespServer(16, FakeRespServer.OkReply);
        var slots = Encoding.ASCII.GetBytes($"*1\r\n*4\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary.Port}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{replica.Port}\r\n");
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? slots
            : command == "GET key" ? "-WRONGTYPE private-key\r\n"u8.ToArray() : null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2, UseCluster = cluster, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = cluster ? [] : [new("127.0.0.1", replica.Port)],
            ReadFrom = RespireReadFrom.PrimaryPreferred,
            HedgedReads = new() { Delay = TimeSpan.FromSeconds(1), MaximumExtraLoadPercent = 100 },
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var slot = ClusterHash.GetSlot("key");
        if (cluster) await client.Core.Cluster!.GetReadConnectionAsync(slot, RespireReadFrom.Replica, deadline.Token);
        else await client.Core.ReadRouter.GetConnectionAsync(RespireReadFrom.Replica, deadline.Token);
        var selected = cluster
            ? await client.Core.Cluster!.GetReadConnectionAsync(slot, RespireReadFrom.PrimaryPreferred, deadline.Token)
            : await client.Core.ReadRouter.GetConnectionAsync(RespireReadFrom.PrimaryPreferred, deadline.Token);
        // Advance the two-socket round robin so the read selects this socket, then hands off
        // to the other live socket when snapshotting closes this socket's admission.
        _ = selected.Multiplexer!.GetConnection();
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var command = new RetireBeforeHedgeSnapshotCommand(selected);
        await Assert.That(async () => { using var result = await client.SendAsync("GET", command, deadline.Token); })
            .Throws<RespireServerException>();
        var items = capture.Items.ToArray();
        await Assert.That(items.Count(item => (bool)item.Tags["redis.client.errors.internal"]!
            && item.Tags["error.type"]?.ToString() == typeof(RespireConnectionRetiredException).FullName)).IsEqualTo(1);
        var final = items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
        await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    private readonly struct RetireBeforeHedgeSnapshotCommand(RespireConnection selected) : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.Read;
        public bool TryGetClusterSlot(out int slot) { slot = ClusterHash.GetSlot("key"); return true; }
        public void Write(ref RespWriter writer)
        {
            selected.StopAcceptingCommands();
            writer.WriteRaw("*2\r\n$3\r\nGET\r\n$3\r\nkey\r\n"u8);
        }
    }

    [Test]
    public async Task ConcurrentFinalBorrowersReportOncePerLease()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        var error = new RespireServerException("ERR private-data");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var round = 0; round < 100; round++)
        {
            using var observation = RespireTelemetry.ErrorObservation.Rent();
            observation.Handled(error);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var arrived = 0;
            var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref arrived) == 8) ready.SetResult();
                await release.Task;
                observation.Final(error);
            })).ToArray();
            await ready.Task.WaitAsync(deadline.Token);
            release.SetResult();
            await Task.WhenAll(workers).WaitAsync(deadline.Token);
        }
        var final = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(100);
        await Assert.That(final.All(item => Equals(item.Tags["redis.client.operation.retry_attempts"], 1))).IsTrue();
        await Assert.That(capture.Items.Count).IsEqualTo(200);
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(0, true)]
    [Arguments(1, false)]
    [Arguments(1, true)]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task ExplicitServerNodeOwnsAcquisitionAndParsingFailures(int outcome, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        var reply = outcome switch
        {
            1 => ":9\r\n"u8.ToArray(),
            2 => "-NOPERM private-node\r\n"u8.ToArray(),
            _ => "*1\r\n$4\r\nuser\r\n"u8.ToArray(),
        };
        await using var target = new FakeRespServer(4, reply);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
            Endpoints = [new("127.0.0.1", target.Port)],
        });
        var node = client.Server.OnNode(new("127.0.0.1", target.Port));
        if (outcome == 0) await client.DisposeAsync();
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute() => _ = await node.AclUsersAsync();
        if (outcome == 0) await Assert.That(Execute).ThrowsExactly<ObjectDisposedException>();
        else if (outcome == 1) await Assert.That(Execute).ThrowsExactly<RespireProtocolException>();
        else if (outcome == 2) await Assert.That(Execute).ThrowsExactly<RespireServerException>();
        else await Execute();
        await Assert.That(capture.Items.Count).IsEqualTo(outcome == 3 ? 0 : 1);
        foreach (var measurement in capture.Items)
        {
            await Assert.That(measurement.Tags["redis.client.errors.internal"]).IsEqualTo(false);
            await Assert.That(measurement.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments("xread", false)]
    [Arguments("xread", true)]
    [Arguments("group", false)]
    [Arguments("group", true)]
    [Arguments("replay", false)]
    [Arguments("group-loop", true)]
    [Arguments("list", true)]
    [Arguments("list-generic", true)]
    [Arguments("list-many", true)]
    [Arguments("list-multi", true)]
    [Arguments("sorted-many", true)]
    [Arguments("sorted-multi", true)]
    [Arguments("sorted-many-generic", true)]
    [Arguments("sorted-multi-generic", true)]
    [Arguments("keys", false)]
    [Arguments("hash", false)]
    [Arguments("set", false)]
    [Arguments("sorted", false)]
    [Arguments("function-generic", false)]
    [Arguments("script-generic", false)]
    [Arguments("ok", false)]
    public async Task ManualReplyReadersOwnTheirFinalFailure(string path, bool blocking)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(4, ":9\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("FCALL", StringComparison.Ordinal)
                || command.StartsWith("EVALSHA", StringComparison.Ordinal) ? "$3\r\nbad\r\n"u8.ToArray()
                : command.StartsWith("XREADGROUP", StringComparison.Ordinal) && path is "replay" or "group-loop"
                    ? "*1\r\n:9\r\n"u8.ToArray() : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute()
        {
            var options = new StreamReadOptions { WaitFor = blocking ? TimeSpan.FromSeconds(1) : null };
            switch (path)
            {
                case "xread": await client.Streams.ReadAsync(options, "events"); break;
                case "group": await client.Streams.ReadGroupOnceAsync("events", "group", "consumer", options); break;
                case "replay": await First(client.Streams.ReadGroupAsync("events", "group", "consumer", (RespireStreamId)"0-0")); break;
                case "group-loop": await First(client.Streams.ReadGroupAsync("events", "group", "consumer")); break;
                case "list": await client.Lists.LeftPopAsync("key", TimeSpan.FromSeconds(1)); break;
                case "list-generic": await client.Lists.LeftPopAsync<int>("key", TimeSpan.FromSeconds(1)); break;
                case "list-many": await client.Lists.PopManyAsync(["key"], waitFor: TimeSpan.FromSeconds(1)); break;
                case "list-multi": await client.Lists.PopAsync(["key"], TimeSpan.FromSeconds(1)); break;
                case "sorted-many": await client.SortedSets.PopManyAsync(["key"], waitFor: TimeSpan.FromSeconds(1)); break;
                case "sorted-multi": await client.SortedSets.PopAsync(["key"], TimeSpan.FromSeconds(1)); break;
                case "sorted-many-generic": await client.SortedSets.PopManyAsync<int>(["key"], waitFor: TimeSpan.FromSeconds(1)); break;
                case "sorted-multi-generic": await client.SortedSets.PopAsync<int>(["key"], TimeSpan.FromSeconds(1)); break;
                case "keys": await First(client.Keys.ScanAsync()); break;
                case "hash": await First(client.Hashes.ScanAsync("key")); break;
                case "set": await First(client.Sets.ScanAsync("key")); break;
                case "sorted": await First(client.SortedSets.ScanAsync("key")); break;
                case "function-generic": await client.Functions.ExecuteAsync<int>(RespireFunction.Create("function")); break;
                case "script-generic": await client.Scripts.ExecuteAsync<int>(RespireScript.Create("return 1")); break;
                case "ok": await client.OkAsync("PING", new RawCommand(FakeRespServer.PingFrame), default); break;
                default: throw new ArgumentOutOfRangeException(nameof(path));
            }
        }
        var error = await Assert.That(Execute).Throws<Exception>();
        var final = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(1);
        await Assert.That(final[0].Tags["error.type"]).IsEqualTo(error!.GetType().FullName);
        await Assert.That(final[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);

        static async Task First<T>(IAsyncEnumerable<T> source)
        {
            await using var enumerator = source.GetAsyncEnumerator();
            _ = await enumerator.MoveNextAsync();
        }
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    public async Task BlockingParserRetainsRedirectAttempts(bool commandMetrics, bool group, bool asking)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var target = new FakeRespServer(4, ":9\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "ASKING" ? FakeRespServer.OkReply : null,
        };
        var slot = ClusterHash.GetSlot("key");
        var code = asking ? "ASK" : "MOVED";
        await using var seed = new FakeRespServer(4, Encoding.ASCII.GetBytes($"-{code} {slot} 127.0.0.1:{target.Port}\r\n"))
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var options = new StreamReadOptions { WaitFor = TimeSpan.FromSeconds(1) };
        await Assert.That(async () =>
        {
            if (group) await client.Streams.ReadGroupOnceAsync("key", "group", "consumer", options, cancellationToken: deadline.Token);
            else await client.Streams.ReadAsync(options, "key", cancellationToken: deadline.Token);
        }).Throws<RespireProtocolException>();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo(code);
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands.Contains("ASKING")).IsEqualTo(asking);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HashImportOwnsWireAndConversionFailures(bool malformed)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var session = await client.Hashes.CreateImportSessionAsync();
        server.ReplyOverride = (_, command) => command.StartsWith("HIMPORT ", StringComparison.Ordinal)
            ? malformed ? ":9\r\n"u8.ToArray() : "-NOPERM private-field\r\n"u8.ToArray() : null;
        using var capture = new Capture(throwOnMeasurement: true);
        var error = await Assert.That(async () => await session.PrepareAsync("schema", "field")).Throws<Exception>();
        var final = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(1);
        await Assert.That(final[0].Tags["error.type"]).IsEqualTo(error!.GetType().FullName);
    }

    [Test]
    [MatrixDataSource]
    public async Task CancelledReroutedRepliesKeepCopiedAttemptsAfterCallerCompletion(
        [Matrix("string", "bytes", "typed", "raw", "prefixed", "stream")] string path,
        [Matrix(false, true)] bool retry)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply)
        {
            ReplyOverride = (connectionId, command) =>
            {
                if (!command.StartsWith("GET ", StringComparison.Ordinal)) return null;
                received.TrySetResult(connectionId);
                return [];
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var connection = client.Core.Multiplexer.GetConnection();
        if (retry) await connection.RetireAsync();
        var discarded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var capture = new Capture(throwOnMeasurement: true, onMeasurement: item =>
        {
            if (item.Tags.GetValueOrDefault("db.response.status_code") is "NOPERM") discarded.TrySetResult();
        });
        using var cancellation = new CancellationTokenSource();
        var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        var command = new Cmd1(Verbs.Get, "key");
        Task pending = path switch
        {
            "string" => RespireTelemetry.ObserveFinalError(connection.SendStringAsync(
                command, cancellation.Token, "GET", observation: observation), observation).AsTask(),
            "bytes" => RespireTelemetry.ObserveFinalError(connection.SendBytesAsync(
                command, cancellation.Token, "GET", observation: observation), observation).AsTask(),
            "typed" => RespireTelemetry.ObserveFinalError(connection.SendConvertedAsync(command, 0,
                static (int _, in RespValue value) => ResponseReader.String(in value), false,
                cancellation.Token, "GET", observation: observation), observation).AsTask(),
            "prefixed" => RespireTelemetry.ObserveFinalError(connection.SendPrefixedCheckedAsync(
                new ClientCachingCommand(), command, cancellation.Token, "GET", observation: observation), observation).AsTask(),
            "stream" => RespireTelemetry.ObserveFinalError(connection.SendBulkStreamAsync(
                command, cancellation.Token, "GET", observation: observation), observation).AsTask(),
            _ => RespireTelemetry.ObserveFinalError(connection.SendAsync(
                command, cancellation.Token, commandName: "GET", observation: observation), observation).AsTask(),
        };
        var connectionId = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        // The final observer has returned its lease. Receive-side metadata must be a copy.
        using var nextObservation = RespireTelemetry.ErrorObservation.Rent(force: true);
        nextObservation.SetAttempts(7);
        await server.SendRawAsync("-NOPERM private-key\r\n"u8.ToArray(), connectionId);
        await discarded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(retry ? 3 : 2);
        var final = items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        var late = items.Single(item => item.Tags.GetValueOrDefault("db.response.status_code") is "NOPERM");
        await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(retry ? 1 : 0);
        await Assert.That(late.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(retry ? 1 : 0);
        await Assert.That((bool)late.Tags["redis.client.errors.internal"]!).IsTrue();
    }

    [Test]
    [Arguments("string", false)]
    [Arguments("bytes", false)]
    [Arguments("integer", false)]
    [Arguments("raw", false)]
    [Arguments("string", true)]
    public async Task ReadySelectionFailureHasOneFinalOwner(string shape, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("HELLO", StringComparison.Ordinal)
                ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp3, Endpoints = [new("127.0.0.1", server.Port)], ClientSideCache = new(),
        });
        var retirement = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new RetireDuringMutationCommand(client.Core.Multiplexer, retirement);
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        await Assert.That(RespireTelemetry.IsOperationEnabled("CONTROL")).IsEqualTo(commandMetrics);
        async Task Execute()
        {
            switch (shape)
            {
                case "string": await client.StringOrNullAsync("CONTROL", command, default); break;
                case "bytes": await client.BytesOrNullAsync("CONTROL", command, default); break;
                case "integer": await client.IntegerAsync("CONTROL", command, default); break;
                case "raw": using (await client.SendAsync("CONTROL", command, default)) { } break;
                default: throw new ArgumentOutOfRangeException(nameof(shape));
            }
        }
        var error = await Assert.That(Execute).Throws<RespireConnectionRetiredException>();
        await await retirement.Task;
        var final = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(1);
        await Assert.That(final[0].Tags["error.type"]).IsEqualTo(error!.GetType().FullName);
    }

    private readonly struct RetireDuringMutationCommand(
        Respire.Infrastructure.RespireConnectionMultiplexer multiplexer, TaskCompletionSource<Task> retirement) : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public RespireCacheMutation GetCacheMutation(string operation)
        {
            retirement.TrySetResult(multiplexer.RetireAsync());
            return RespireCacheMutation.ReadOnly;
        }
        public void Write(ref RespWriter writer) => throw new InvalidOperationException("Selection must fail before writing.");
    }

    [Test]
    public async Task FunctionScalarReadersKeepTheirPermissiveReplyContract()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer("*0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture();
        var function = RespireFunction.Create("function");
        await Assert.That(await client.Functions.ExecuteIntegerAsync(function)).IsEqualTo(0L);
        await Assert.That(await client.Functions.ExecuteStringAsync(function)).IsEqualTo(string.Empty);
        await Assert.That(capture.Items).IsEmpty();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ProcedureConversionRetainsRecoveryAttempts(bool commandMetrics, bool script)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        const string source = "#!lua name=sample\nreturn 1";
        var replies = script
            ? new[] { "-NOSCRIPT private-source\r\n"u8.ToArray(), "$3\r\nbad\r\n"u8.ToArray() }
            : new[] { "-ERR Function not found\r\n"u8.ToArray(), "*0\r\n"u8.ToArray(),
                "-ERR Library 'sample' already exists\r\n"u8.ToArray(), FunctionLibraryMetadata(source), "$3\r\nbad\r\n"u8.ToArray() };
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var error = await Assert.That(async () =>
        {
            if (script) await client.Scripts.ExecuteAsync<int>(RespireScript.Create("return 1"));
            else await client.Functions.ExecuteAsync<int>(RespireFunctionLibrary.Create(source).Function("function"));
        }).Throws<Exception>();
        var items = capture.Items.ToArray();
        var handled = script ? 1 : 2;
        await Assert.That(items.Length).IsEqualTo(handled + 1);
        await Assert.That(items.Take(handled).All(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsTrue();
        await Assert.That((bool)items[^1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[^1].Tags["error.type"]).IsEqualTo(error!.GetType().FullName);
        await Assert.That(items[^1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(handled);
    }

    [Test]
    public async Task ListenerOutOfMemoryCannotReplaceApplicationError()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer("-WRONGTYPE private-key\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(onMeasurement: _ => throw new OutOfMemoryException("listener failure"));
        var error = await Assert.That(async () => await client.GetStringAsync("key")).Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("WRONGTYPE");
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task RedirectedConversionRetainsAttempts(bool commandMetrics, bool streamed)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var target = new FakeRespServer(4, "$5\r\nvalue\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(4, Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n"))
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var expected = new RespireProtocolException("private-conversion");
        using var payload = new MemoryStream("value"u8.ToArray());
        using var capture = new Capture(throwOnMeasurement: true);
        var error = await Assert.That(async () =>
        {
            if (streamed)
                await client.ConvertResponseAsync("SET", new StreamedSetCommand((RespireValue)"key", payload,
                    payload.Length, default, SetWhen.Always), default, expected,
                    static (RespireProtocolException failure, in RespValue _) => ThrowConversion(failure));
            else
                await client.ConvertResponseAsync("GET", new Cmd1(Verbs.Get, "key"), default, expected,
                    static (RespireProtocolException failure, in RespValue _) => ThrowConversion(failure));
        }).Throws<RespireProtocolException>();
        await Assert.That(ReferenceEquals(error, expected)).IsTrue();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
    }

    [Test]
    [Arguments("nodes")]
    [Arguments("info")]
    [Arguments("scan")]
    [Arguments("clusterscan")]
    public async Task ClusterScanParsingOwnsFinalErrors(string path)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        byte[] Bulk(string value) => Encoding.ASCII.GetBytes($"${Encoding.ASCII.GetByteCount(value)}\r\n{value}\r\n");
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n"),
            "CLUSTER NODES" => path == "nodes" ? ":1\r\n"u8.ToArray()
                : Bulk($"node 127.0.0.1:{server.Port}@17000 myself,master - 0 0 1 connected 0-16383\n"),
            "INFO server" => path == "info" ? ":1\r\n"u8.ToArray() : Bulk("run_id:one\r\n"),
            "COMMAND INFO CLUSTERSCAN" => path == "clusterscan"
                ? "*1\r\n*1\r\n$11\r\nclusterscan\r\n"u8.ToArray() : "*1\r\n$-1\r\n"u8.ToArray(),
            _ when command.StartsWith("SCAN ", StringComparison.Ordinal) => ":1\r\n"u8.ToArray(),
            _ when command.StartsWith("CLUSTERSCAN ", StringComparison.Ordinal) => ":1\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        await Assert.That(async () => await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start))
            .Throws<RespireProtocolException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
    }

    [Test]
    [Arguments("metadata-denied", false)]
    [Arguments("metadata-denied", true)]
    [Arguments("metadata-unknown", false)]
    [Arguments("metadata-unknown", true)]
    [Arguments("scan-denied", false)]
    [Arguments("scan-denied", true)]
    [Arguments("scan-unknown", false)]
    [Arguments("scan-unknown", true)]
    public async Task ClusterScanFallbackRetainsAttemptsThroughFinalParsing(string path, bool fail)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        byte[] Bulk(string value) => Encoding.ASCII.GetBytes($"${Encoding.ASCII.GetByteCount(value)}\r\n{value}\r\n");
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n"),
            "CLUSTER NODES" => Bulk($"node 127.0.0.1:{server.Port}@17000 myself,master - 0 0 1 connected 0-16383\n"),
            "INFO server" => Bulk("run_id:one\r\n"),
            "COMMAND INFO CLUSTERSCAN" => path switch
            {
                "metadata-denied" => "-NOPERM metadata denied\r\n"u8.ToArray(),
                "metadata-unknown" => "-ERR unknown command 'COMMAND'\r\n"u8.ToArray(),
                _ => "*1\r\n*1\r\n$11\r\nclusterscan\r\n"u8.ToArray(),
            },
            _ when command.StartsWith("CLUSTERSCAN ", StringComparison.Ordinal) => path == "scan-denied"
                ? "-NOPERM scan denied\r\n"u8.ToArray() : "-ERR unknown command 'CLUSTERSCAN'\r\n"u8.ToArray(),
            _ when command.StartsWith("SCAN ", StringComparison.Ordinal) => fail
                ? ":1\r\n"u8.ToArray() : "*2\r\n$1\r\n0\r\n*0\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        if (fail)
            await Assert.That(async () => await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start))
                .Throws<RespireProtocolException>();
        else
            await Assert.That((await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start)).Cursor.IsComplete).IsTrue();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(fail ? 2 : 1);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        if (fail)
        {
            await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[1].Tags["error.type"]).IsEqualTo(typeof(RespireProtocolException).FullName);
            await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConversionSourcesOwnAttemptsAcrossCompletionModes(bool pending)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        observation.Handled(new IOException("private-retirement"));
        var expected = new RespireProtocolException("private-conversion");
        var completion = new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Execute()
        {
            var result = PooledResponseSource<Exception, int>.Create(pending ? new(completion.Task) : new(RespValue.Integer(1)),
                expected, static (Exception error, in RespValue _) => ThrowConversion(error), observation: observation);
            if (pending) completion.SetResult(RespValue.Integer(1));
            _ = await result;
        }
        var error = await Assert.That(Execute).Throws<RespireProtocolException>();
        await Assert.That(ReferenceEquals(error, expected)).IsTrue();
        await Assert.That(capture.Items.Count).IsEqualTo(2);
        await Assert.That(capture.Items.Last().Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        // Successful reuse starts clean and returns the lease before publishing the caller result.
        var reused = RespireTelemetry.ErrorObservation.Rent(force: true);
        await Assert.That(reused.Attempts).IsEqualTo(0);
        await Assert.That(await PooledResponseSource<int, long>.Create(new(RespValue.Integer(42)), 0,
            static (int _, in RespValue value) => value.AsInteger(), observation: reused)).IsEqualTo(42L);
        await Assert.That(capture.Items.Count).IsEqualTo(2);
    }

    private static int ThrowConversion(Exception failure) => throw failure;

    [Test]
    [Arguments("id")]
    [Arguments("unblock")]
    [Arguments("info")]
    [Arguments("tracking")]
    [Arguments("echo")]
    [Arguments("ok")]
    [Arguments("hotkeys")]
    public async Task PinnedReplyParsingOwnsFinalErrors(string path)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(":123\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "CLIENT ID" && path != "id" ? ":123\r\n"u8.ToArray()
                : ":9\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, AllowAdmin = true, Endpoints = [new("127.0.0.1", server.Port)],
        });
        var pinned = path is "id" or "hotkeys" ? null : await client.Server.GetClientConnectionAsync();
        var hotkeys = path == "hotkeys" ? await client.Server.GetHotKeysTrackerAsync() : null;
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute()
        {
            if (hotkeys is not null) await hotkeys.GetAsync();
            else if (pinned is null) await client.Server.GetClientConnectionAsync();
            else if (path == "unblock") await pinned.UnblockClientAsync(123);
            else if (path == "info") await pinned.InfoAsync();
            else if (path == "tracking") await pinned.TrackingInfoAsync();
            else if (path == "echo") await pinned.EchoAsync("value"u8.ToArray());
            else await pinned.SetNoTouchAsync(true);
        }
        // A nonpositive CLIENT ID is invalid; other parsers reject the integer shape/value.
        if (path == "id") server.ReplyOverride = (_, _) => ":0\r\n"u8.ToArray();
        if (path == "ok") await Assert.That(Execute).Throws<RespireException>();
        else await Assert.That(Execute).Throws<RespireProtocolException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StandaloneScriptTransportRetriesReachTheirFinalOwner(bool noScript)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var heldWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "PING hold") return false;
                heldWritten.TrySetResult();
                return true;
            },
            ReplyOverride = (_, command) => noScript && command.StartsWith("EVALSHA ", StringComparison.Ordinal)
                ? "-NOSCRIPT private-script\r\n"u8.ToArray()
                : command.StartsWith("EVAL", StringComparison.Ordinal) ? "-WRONGTYPE private-key\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2, MaxInflightCommands = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var original = client.Core.Multiplexer.GetConnection();
        _ = client.Core.Multiplexer.GetConnection();
        var held = original.SendCheckedAsync(new RawCommand("*2\r\n$4\r\nPING\r\n$4\r\nhold\r\n"u8.ToArray())).AsTask();
        await heldWritten.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var capture = new Capture(throwOnMeasurement: true);
        var pending = client.Scripts.ExecuteAsync(RespireScript.Create("return 1")).AsTask();
        await Assert.That(pending.IsCompleted).IsFalse();
        var retirement = original.RetireAsync();
        try
        {
            await Assert.That(async () => { using var result = await pending.WaitAsync(TimeSpan.FromSeconds(5)); })
                .Throws<RespireServerException>();
        }
        finally
        {
            var index = server.ReceivedCommands.ToList().IndexOf("PING hold");
            await server.SendRawAsync(FakeRespServer.PongReply, server.ReceivedConnectionIds[index]);
            using var reply = await held.WaitAsync(TimeSpan.FromSeconds(5));
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var items = capture.Items.ToArray();
        // Both script sends initially address the retired physical handle. NOSCRIPT fallback
        // therefore handles a second transport retirement before EVAL reaches the live peer.
        await Assert.That(items.Length).IsEqualTo(noScript ? 4 : 2);
        for (var i = 0; i < items.Length; i++)
        {
            await Assert.That((bool)items[i].Tags["redis.client.errors.internal"]!).IsEqualTo(i < items.Length - 1);
            await Assert.That(items[i].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(i);
        }
        if (noScript)
        {
            await Assert.That(items[1].Tags["db.response.status_code"]).IsEqualTo("NOSCRIPT");
            await Assert.That(items[2].Tags["error.type"]).IsEqualTo(items[0].Tags["error.type"]);
        }
        await Assert.That(items[^1].Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConnectAnyCancellationReportsCompletedCandidateFailures(bool afterFailure)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer("-WRONGPASS private-secret\r\n"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        if (!afterFailure) cancellation.Cancel();
        using var capture = new Capture(throwOnMeasurement: true, onMeasurement: item =>
        {
            if ((bool)item.Tags["redis.client.errors.internal"]!) cancellation.Cancel();
        });
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Password = "private-secret", Endpoints = [new("127.0.0.1", server.Port)],
        };
        var error = await Assert.That(async () =>
            await RespireClient.ConnectAnyAsync([options, options], cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(afterFailure ? 2 : 1);
        if (afterFailure)
        {
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That((bool)items[^1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[^1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(afterFailure ? 1 : 0);
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(4, false)]
    [Arguments(5, false)]
    [Arguments(6, false)]
    [Arguments(7, false)]
    [Arguments(0, true)]
    [Arguments(1, true)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    [Arguments(4, true)]
    [Arguments(5, true)]
    [Arguments(6, true)]
    [Arguments(7, true)]
    public async Task ScriptFanOutOwnsDiscoveryConversionAndConsistencyFailures(int outcome, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        var firstReply = outcome switch
        {
            1 => "$5\r\nfirst\r\n"u8.ToArray(),
            2 => "*1\r\n:1\r\n"u8.ToArray(),
            3 => ":9\r\n"u8.ToArray(),
            4 or 5 => "-NOPERM private-script\r\n"u8.ToArray(),
            _ => FakeRespServer.OkReply,
        };
        var secondReply = outcome switch
        {
            1 => "$6\r\nsecond\r\n"u8.ToArray(),
            2 => "*2\r\n:1\r\n:0\r\n"u8.ToArray(),
            5 => "-NOPERM private-script\r\n"u8.ToArray(),
            _ => FakeRespServer.OkReply,
        };
        await using var first = new FakeRespServer(4, firstReply);
        await using var second = new FakeRespServer(4, secondReply);
        var topology = outcome == 7 ? "*0\r\n"u8.ToArray() : Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        await using var seed = new FakeRespServer(4, topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        if (outcome == 0) await client.DisposeAsync();
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        async Task Execute()
        {
            if (outcome is 0 or 1) await client.Scripts.LoadAsync(RespireScript.Create("return 1"));
            else if (outcome == 2) await client.Scripts.ExistsAsync("digest");
            else await client.Scripts.FlushAsync();
        }
        Exception? error;
        if (outcome == 0) error = await Assert.That(Execute).Throws<ObjectDisposedException>();
        else if (outcome is 1 or 2) error = await Assert.That(Execute).Throws<RespireProtocolException>();
        else if (outcome is 4 or 5) error = await Assert.That(Execute).Throws<RespireServerException>();
        else if (outcome == 7) error = await Assert.That(Execute).Throws<RespireConnectionException>();
        else if (outcome == 3) error = await Assert.That(Execute).Throws<RespireException>();
        else { await Execute(); error = null; }
        var items = capture.Items.ToArray();
        var expectedMeasurements = outcome == 6 ? 0 : 1;
        await Assert.That(items.Length).IsEqualTo(expectedMeasurements);
        foreach (var item in items)
        {
            await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(item.Tags["error.type"]).IsEqualTo(error!.GetType().FullName);
            await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task FunctionFanOutOwnsDiscoveryAndConsistencyFailures(int outcome)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var first = new FakeRespServer(outcome == 2
            ? "-NOPERM private-function\r\n"u8.ToArray() : "$5\r\nfirst\r\n"u8.ToArray());
        await using var second = new FakeRespServer("$6\r\nsecond\r\n"u8.ToArray());
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        if (outcome == 0) await client.DisposeAsync();
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute() => _ = await client.Functions.LoadAsync("#!lua name=sample\nreturn 1");
        if (outcome == 0) await Assert.That(Execute).Throws<ObjectDisposedException>();
        else if (outcome == 1) await Assert.That(Execute).Throws<RespireProtocolException>();
        else await Assert.That(Execute).Throws<RespireServerException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task HotKeysFanOutOwnsDiscoveryFailuresWithoutDuplicatingWireFailures(int outcome)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer("*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) =>
            {
                if (command != "CLUSTER NODES") return null;
                return outcome == 2 ? "-NOPERM private-topology\r\n"u8.ToArray() : ":1\r\n"u8.ToArray();
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        if (outcome == 0) await client.DisposeAsync();
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute() => _ = await client.Server.GetHotKeysOnAllNodesAsync();
        if (outcome == 0) await Assert.That(Execute).Throws<ObjectDisposedException>();
        else if (outcome == 1) await Assert.That(Execute).Throws<RespireProtocolException>();
        else await Assert.That(Execute).Throws<RespireServerException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task TrackedNativeLockFallbacksRetainTheirFinalOwner(bool retry, bool correction)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(retry
            ? ["-ERR unknown command 'DELEX', private-native\r\n"u8.ToArray(),
                "-ERR unknown command 'DELIFEQ', private-native\r\n"u8.ToArray(),
                "-NOSCRIPT private-script\r\n"u8.ToArray(), "-WRONGTYPE private-key\r\n"u8.ToArray()]
            : ["-WRONGTYPE private-key\r\n"u8.ToArray()]);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        var execution = await client.StartLockExecutionAsync("key", new RespireLockToken("owner"), null,
            requireIdentity: false, allowUnfencedFallback: false, CancellationToken.None);
        await Assert.That(async () =>
        {
            if (correction)
                await client.ExecuteWithCorrectionAsync(execution, RespireClient.CorrectionOrdering.NotifyOnly);
            else
                await execution.ConsumeResponseAsync();
        }).Throws<RespireServerException>();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(retry ? 4 : 1);
        for (var i = 0; i < items.Length; i++)
        {
            await Assert.That((bool)items[i].Tags["redis.client.errors.internal"]!).IsEqualTo(i < items.Length - 1);
            await Assert.That(items[i].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(i);
        }
        await Assert.That(items[^1].Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TrackedRedirectAndNoScriptShareContinuousAttempts(bool fail)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var target = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) =>
            {
                if (command == "CLIENT ID") return ":123\r\n"u8.ToArray();
                if (command.StartsWith("CLIENT KILL ", StringComparison.Ordinal)) return ":0\r\n"u8.ToArray();
                if (command.StartsWith("EVALSHA ", StringComparison.Ordinal))
                    return "-NOSCRIPT private-script\r\n"u8.ToArray();
                if (command.StartsWith("EVAL ", StringComparison.Ordinal))
                    return fail ? "-WRONGTYPE private-key\r\n"u8.ToArray() : ":42\r\n"u8.ToArray();
                return null;
            },
        };
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer("*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) =>
            {
                if (command == "CLIENT ID") return ":124\r\n"u8.ToArray();
                if (command.StartsWith("CLIENT KILL ", StringComparison.Ordinal)) return ":0\r\n"u8.ToArray();
                return command.StartsWith("EVALSHA ", StringComparison.Ordinal)
                    ? Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n") : null;
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            CommandTimeout = null, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        var execution = await client.StartTrackedScriptExecutionAsync(
            RespireScript.Create("return 42"), ["key"], [], CancellationToken.None);
        async Task Execute()
        {
            using var result = await client.ExecuteWithCorrectionAsync(execution, RespireClient.CorrectionOrdering.OrderedCorrection);
            await Assert.That(result.AsInteger()).IsEqualTo(42);
        }
        if (fail) await Assert.That(Execute).Throws<RespireServerException>();
        else await Execute();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(fail ? 3 : 2);
        await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("MOVED");
        await Assert.That(items[1].Tags["db.response.status_code"]).IsEqualTo("NOSCRIPT");
        for (var i = 0; i < items.Length; i++)
        {
            await Assert.That((bool)items[i].Tags["redis.client.errors.internal"]!).IsEqualTo(i < 2);
            await Assert.That(items[i].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(i);
        }
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    [Arguments(true, true, true)]
    public async Task TrackedScriptsRetainOneFinalOwner(bool cluster, bool retry, bool correction)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var target = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) =>
            {
                if (command == "CLIENT ID") return ":123\r\n"u8.ToArray();
                if (command.StartsWith("CLIENT KILL ", StringComparison.Ordinal)) return ":0\r\n"u8.ToArray();
                if (retry && command.StartsWith("EVALSHA ", StringComparison.Ordinal))
                    return "-NOSCRIPT private-script\r\n"u8.ToArray();
                return command.StartsWith("EVAL", StringComparison.Ordinal)
                    ? "-WRONGTYPE private-key\r\n"u8.ToArray() : null;
            },
        };
        var slot = ClusterHash.GetSlot("key");
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = cluster, ClusterTopologyRefreshInterval = null,
            CommandTimeout = null,
            Endpoints = [new("127.0.0.1", cluster ? seed.Port : target.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        var execution = await client.StartTrackedScriptExecutionAsync(
            RespireScript.Create("return 1"), ["key"], [], CancellationToken.None);
        await Assert.That(async () =>
        {
            using var result = correction
                ? await client.ExecuteWithCorrectionAsync(execution, RespireClient.CorrectionOrdering.OrderedCorrection)
                : await execution.ConsumeResponseAsync();
        }).Throws<RespireServerException>();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(retry ? 2 : 1);
        if (retry)
        {
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("NOSCRIPT");
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That((bool)items[^1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[^1].Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
        await Assert.That(items[^1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(retry ? 1 : 0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TrackedFinalErrorWaitsForCorrectionAndReportsItsPublishedError(bool replace)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("EVALSHA ", StringComparison.Ordinal)) return false;
                written.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        using var cancellation = new CancellationTokenSource();
        var execution = await client.StartTrackedScriptExecutionAsync(
            RespireScript.Create("return 1"), ["key"], [], cancellation.Token, captureSendTimestampOnly: true);
        var correcting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var correctionError = new RespireServerException("NOPERM private-correction");
        var response = client.ExecuteWithCorrectionAsync(execution, RespireClient.CorrectionOrdering.OrderedCorrection,
            state: false, correct: async (_, _) =>
            {
                correcting.TrySetResult();
                await release.Task.ConfigureAwait(false);
                if (replace) throw correctionError;
            }).AsTask();
        try
        {
            await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await correcting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(capture.Items.Count).IsEqualTo(0);
            await Assert.That(response.IsCompleted).IsFalse();
        }
        finally { release.TrySetResult(); }
        if (replace)
        {
            var error = await Assert.That(async () => await response).Throws<RespireServerException>();
            await Assert.That(ReferenceEquals(error, correctionError)).IsTrue();
        }
        else
        {
            var error = await Assert.That(async () => await response).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        var item = capture.Items.Single();
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(item.Tags["error.type"]).IsEqualTo(
            replace ? typeof(RespireServerException).FullName : typeof(OperationCanceledException).FullName);
        await server.SendRawAsync(":1\r\n"u8.ToArray());
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(0, true)]
    [Arguments(1, false)]
    [Arguments(1, true)]
    [Arguments(2, false)]
    [Arguments(2, true)]
    public async Task FunctionRecoveryHasOneFinalOwner(int outcome, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : 0) });
        const string source = "#!lua name=sample\nreturn 1";
        var metadata = outcome == 2 ? "*0\r\n"u8.ToArray()
            : FunctionLibraryMetadata(outcome == 1 ? source + " -- changed" : source);
        await using var server = new FakeRespServer("-ERR Function not found\r\n"u8.ToArray(),
            "*0\r\n"u8.ToArray(), "-ERR Library 'sample' already exists\r\n"u8.ToArray(),
            metadata, ":42\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        var function = RespireFunctionLibrary.Create(source).Function("function");
        if (outcome == 0)
            await Assert.That(await client.Functions.ExecuteIntegerAsync(function)).IsEqualTo(42);
        else
        {
            var error = await Assert.That(async () => await client.Functions.ExecuteIntegerAsync(function))
                .Throws<RespireServerException>();
            await Assert.That(error!.Message).IsEqualTo("ERR Library 'sample' already exists");
        }
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(outcome == 0 ? 2 : 3);
        for (var i = 0; i < items.Length; i++)
        {
            await Assert.That((bool)items[i].Tags["redis.client.errors.internal"]!).IsEqualTo(i < 2);
            await Assert.That(items[i].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(i);
        }
    }

    [Test]
    public async Task FunctionPrivateReloadFailureIsFinalOnce()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer("-ERR Function not found\r\n"u8.ToArray(),
            "-NOPERM private-library-inspection\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        var function = RespireFunctionLibrary.Create("#!lua name=sample\nreturn 1").Function("function");
        var error = await Assert.That(async () => await client.Functions.ExecuteIntegerAsync(function))
            .Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("NOPERM");
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
    }

    [Test]
    public async Task FunctionRecoveryReportsCancellationAfterCleanup()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer("-ERR Function not found\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var library = RespireFunctionLibrary.Create("#!lua name=sample\nreturn 1");
        var gate = library.ReloadState(client.Core).Gate;
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        using var capture = new Capture(throwOnMeasurement: true);
        try
        {
            var pending = client.Functions.ExecuteIntegerAsync(library.Function("function"),
                cancellationToken: cancellation.Token).AsTask();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!capture.Items.Any()) await Task.Delay(1, deadline.Token);
            cancellation.Cancel();
            var error = await Assert.That(async () => await pending.WaitAsync(deadline.Token))
                .Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        finally { gate.Release(); }
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task ClusterFunctionReloadFailureJoinsOtherBorrowers()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        const string source = "#!lua name=sample\nreturn 1";
        await using var first = new FakeRespServer("*0\r\n"u8.ToArray(), "-NOPERM private-library-load\r\n"u8.ToArray());
        await using var second = new FakeRespServer("-ERR Function not found\r\n"u8.ToArray(),
            "*0\r\n"u8.ToArray(), FunctionLibraryMetadata(source))
        {
            SuppressReply = command => command.StartsWith("FUNCTION LOAD ", StringComparison.Ordinal),
        };
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = client.Functions.ExecuteIntegerAsync(RespireFunctionLibrary.Create(source).Function("function"),
            ["{foo}:key"], cancellationToken: deadline.Token).AsTask();
        while (!second.ReceivedCommands.Any(command => command.StartsWith("FUNCTION LOAD ", StringComparison.Ordinal)))
            await Task.Delay(1, deadline.Token);
        var completedBeforeJoin = pending.IsCompleted;
        var finalsBeforeJoin = capture.Items.Count(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await second.SendRawAsync("$6\r\nsample\r\n"u8.ToArray());
        var error = await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("NOPERM");
        await Assert.That(completedBeforeJoin).IsFalse();
        await Assert.That(finalsBeforeJoin).IsEqualTo(0);
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(second.ReceivedCommands.Count(command => command.StartsWith("FUNCTION LIST ", StringComparison.Ordinal))).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PlainFunctionFailuresAreObservedOnce(bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : 0) });
        await using var server = new FakeRespServer("-NOPERM private-function\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        var error = await Assert.That(async () => await client.Functions.ExecuteIntegerAsync(RespireFunction.Create("function")))
            .Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("NOPERM");
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(capture.Items.Single().Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    public async Task JoinedObservationBorrowersRetainEveryAttempt()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        var error = new IOException("private-borrower");
        Parallel.For(0, 32, _ => observation.Handled(error));
        observation.Final(error);
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(33);
        await Assert.That(items.Take(32).All(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsTrue();
        await Assert.That(items.Take(32).Select(item => (int)item.Tags["redis.client.operation.retry_attempts"]!)
            .Order().SequenceEqual(Enumerable.Range(0, 32))).IsTrue();
        await Assert.That((bool)items[^1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[^1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(32);
    }

    private static byte[] FunctionLibraryMetadata(string source) => Encoding.UTF8.GetBytes(
        "*1\r\n*8\r\n+library_name\r\n+sample\r\n+engine\r\n+LUA\r\n+functions\r\n*1\r\n*6\r\n" +
        "+name\r\n+function\r\n+description\r\n$-1\r\n+flags\r\n*0\r\n+library_code\r\n$" +
        Encoding.UTF8.GetByteCount(source) + "\r\n" + source + "\r\n");

    [Test]
    public async Task RecoveredPrivateFunctionLoadDoesNotPublishAFinalError()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        const string source = "#!lua name=sample\nreturn 1";
        var metadata = FunctionLibraryMetadata(source);
        await using var server = new FakeRespServer("-ERR Function not found\r\n"u8.ToArray(),
            "*0\r\n"u8.ToArray(), "-ERR Library 'sample' already exists\r\n"u8.ToArray(),
            metadata, ":42\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) =>
            {
                // Start collection after FCALL fails, isolating the private reload's boundary.
                if (command.StartsWith("FUNCTION LOAD ", StringComparison.Ordinal))
                    RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
                return null;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        var function = RespireFunctionLibrary.Create(source).Function("function");
        await Assert.That(await client.Functions.ExecuteIntegerAsync(function)).IsEqualTo(42);
        await Assert.That(capture.Items.Count(item => !(bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("FUNCTION LOAD ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    [Arguments("hotkeys", false)]
    [Arguments("hotkeys", true)]
    [Arguments("pinned", false)]
    [Arguments("pinned", true)]
    [Arguments("function", false)]
    [Arguments("function", true)]
    public async Task DirectPublicRoutesObserveTheirFinalError(string path, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : 0),
        });
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n");
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => topology,
            "CLIENT ID" => ":7\r\n"u8.ToArray(),
            _ => "-NOPERM private-operation\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = path == "function",
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", server.Port)],
        });
        var hotkeys = path == "hotkeys" ? await client.Server.GetHotKeysTrackerAsync() : null;
        var pinned = path == "pinned" ? await client.Server.GetClientConnectionAsync() : null;
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute()
        {
            if (hotkeys is not null) await hotkeys.GetAsync();
            else if (pinned is not null) await pinned.EchoAsync("value"u8.ToArray());
            else await client.Functions.LoadAsync("#!lua name=private_library");
        }
        var error = await Assert.That(Execute).Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("NOPERM");
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        var item = capture.Items.Single();
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task DisposedBlockingAndScriptCallsObserveTheirFinalError(bool script, bool cluster)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray() : null,
        };
        var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = cluster, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        await client.DisposeAsync();
        var commands = server.CommandsSeen;
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute()
        {
            if (script) { using var result = await client.Scripts.ExecuteAsync(RespireScript.Create("return 1")); }
            else { using var result = await client.ExecuteAsync("BLPOP", ["key", 0]); }
        }
        await Assert.That(Execute).Throws<ObjectDisposedException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(server.CommandsSeen).IsEqualTo(commands);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task SubmissionReroutesCarryAttemptsToFinalFailure(bool capacityWait, bool lateActivation)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Command | (lateActivation ? 0 : RespireMetricGroups.Resiliency) });
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command => command == "PING hold",
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2, MaxInflightCommands = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        // Two selections leave the next public send on the first connection again.
        var connection = client.Core.Multiplexer.GetConnection();
        _ = client.Core.Multiplexer.GetConnection();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<RespValue>? held = null;
        if (capacityWait)
        {
            held = connection.SendCheckedAsync(new RawCommand("*2\r\n$4\r\nPING\r\n$4\r\nhold\r\n"u8.ToArray())).AsTask();
            while (!server.ReceivedCommands.Contains("PING hold")) await Task.Delay(1, deadline.Token);
        }
        Task? retirement = null;
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (!capacityWait && retirement is null && activity.GetTagItem("db.operation.name")?.ToString() == "SET")
                    retirement = connection.RetireAsync();
            },
        };
        ActivitySource.AddActivityListener(activityListener);
        using var payload = new RejectedSubmissionPayload();
        var bytes = payload.Memory;
        payload.RejectReads = true;
        if (lateActivation)
            payload.BeforeFailure = () => RespireMetrics.Configure(new()
                { Groups = RespireMetricGroups.Command | RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        var pending = client.ExecuteFireAndForgetAsync("SET", ["key", bytes], deadline.Token).AsTask();
        if (capacityWait)
        {
            await Assert.That(pending.IsCompleted).IsFalse();
            retirement = connection.RetireAsync();
        }
        var error = await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<IOException>();
        await Assert.That(ReferenceEquals(error, payload.Error)).IsTrue();
        if (held is not null)
        {
            var index = server.ReceivedCommands.ToList().IndexOf("PING hold");
            await server.SendRawAsync(FakeRespServer.PongReply, server.ReceivedConnectionIds[index]);
            using var reply = await held.WaitAsync(deadline.Token);
        }
        await Assert.That(retirement).IsNotNull();
        await retirement!.WaitAsync(deadline.Token);
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(lateActivation ? 1 : 2);
        if (!lateActivation)
        {
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That((bool)items[^1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[^1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("SET ", StringComparison.Ordinal))).IsFalse();
    }

    private sealed class RejectedSubmissionPayload : System.Buffers.MemoryManager<byte>
    {
        private readonly byte[] _bytes = [1];
        internal readonly IOException Error = new("The replacement submission failed.");
        internal bool RejectReads;
        internal Action? BeforeFailure;
        public override Span<byte> GetSpan()
        {
            if (!RejectReads) return _bytes;
            BeforeFailure?.Invoke();
            throw Error;
        }
        public override System.Buffers.MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    [Test]
    public async Task SuppressedBlockingDisposalLeavesReportingToItsOwner()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var client = RespireClient.Create("localhost");
        await client.DisposeAsync();
        using var capture = new Capture(throwOnMeasurement: true);
        await Assert.That(async () =>
        {
            using var reply = await client.SendBlockingAsync("BLPOP", new Cmd2(Verbs.BLPop, "key", 0),
                default, observeErrors: false);
        }).Throws<ObjectDisposedException>();
        await Assert.That(capture.Items.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NodeFanOutObservesFailureBeforeReturningItsResult(bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : 0) });
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HOTKEYS GET" ? "-NOPERM private-operation\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        var results = await client.Server.GetHotKeysOnAllNodesAsync();
        await Assert.That(results.Length).IsEqualTo(1);
        await Assert.That(results[0].IsSuccess).IsFalse();
        await Assert.That(results[0].Error is RespireServerException { Code: "NOPERM" }).IsTrue();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
    }

    [Test]
    [Arguments("raw", false)]
    [Arguments("raw", true)]
    [Arguments("typed", false)]
    [Arguments("typed", true)]
    [Arguments("script", false)]
    [Arguments("script", true)]
    public async Task ClusterTransportReroutesCarryAttempts(string path, bool redirect)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Resiliency | RespireMetricGroups.Command });
        await using var target = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray()
                : "-WRONGTYPE private-key\r\n"u8.ToArray(),
        };
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(4, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{seed.Port}\r\n");
        seed.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology
            : redirect ? Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n")
                : "-WRONGTYPE private-key\r\n"u8.ToArray();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var original = await client.Core.Cluster!.GetConnectionAsync(slot, default, discovery: null);
        Task? retirement = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (retirement is null && activity.GetTagItem("db.operation.name")?.ToString()
                    == (path == "script" ? "EVALSHA" : "GET"))
                    retirement = original.RetireAsync();
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute()
        {
            if (path == "raw") { using var result = await client.ExecuteAsync("GET", "key"); }
            else if (path == "script") { using var result = await client.Scripts.ExecuteAsync(RespireScript.Create("return 1"), ["key"]); }
            else await client.GetStringAsync("key");
        }
        var error = await Assert.That(Execute).Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("WRONGTYPE");
        await Assert.That(retirement).IsNotNull();
        await retirement!;
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(redirect ? 3 : 2);
        var handled = items.Where(item => (bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(handled[0].Tags["error.type"]).IsEqualTo(typeof(RespireConnectionRetiredException).FullName);
        await Assert.That(handled[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        if (redirect)
        {
            await Assert.That(handled[1].Tags["db.response.status_code"]).IsEqualTo("MOVED");
            await Assert.That(handled[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
        var final = items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(redirect ? 2 : 1);
    }

    [Test]
    [MatrixDataSource]
    public async Task StandaloneReplicaScansObserveTheirFinalError(
        [Matrix("SCAN", "HSCAN", "SSCAN", "ZSCAN")] string operation,
        [Matrix(RespireReadFrom.Replica, RespireReadFrom.ReplicaPreferred)] RespireReadFrom policy,
        [Matrix("server", "cursor", "items")] string failure,
        [Matrix(false, true)] bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency
            | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        var pages = 0;
        await using var replica = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) =>
            {
                if (command == "ROLE")
                    return "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray();
                if (!command.StartsWith(operation + " ", StringComparison.Ordinal)) return null;
                return Interlocked.Increment(ref pages) == 1
                    ? "*2\r\n$1\r\n7\r\n*0\r\n"u8.ToArray()
                    : failure switch
                    {
                        "cursor" => "*2\r\n$3\r\nbad\r\n*0\r\n"u8.ToArray(),
                        "items" => "*2\r\n$1\r\n0\r\n:1\r\n"u8.ToArray(),
                        _ => "-WRONGTYPE private-key\r\n"u8.ToArray(),
                    };
            },
        };
        await using var owner = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ReadFrom = policy,
            Endpoints = [new("127.0.0.1", primary.Port)], ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
        });
        var client = owner.WithKeyPrefix("tenant:");
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task Enumerate() => operation switch
        {
            "SCAN" => Consume(client.Keys.ScanAsync(cancellationToken: deadline.Token)),
            "HSCAN" => Consume(client.Hashes.ScanAsync("key", cancellationToken: deadline.Token)),
            "SSCAN" => Consume(client.Sets.ScanAsync("key", cancellationToken: deadline.Token)),
            _ => Consume(client.SortedSets.ScanAsync("key", cancellationToken: deadline.Token)),
        };
        static async Task Consume<T>(IAsyncEnumerable<T> entries)
        {
            await foreach (var entry in entries) { }
        }
        var error = await Assert.That(Enumerate).Throws<Exception>();
        if (failure == "server") await Assert.That(((RespireServerException)error!).Code).IsEqualTo("WRONGTYPE");
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        var item = capture.Items.Single();
        await Assert.That(item.Tags["error.type"]).IsEqualTo(error!.GetType().FullName);
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        var commands = replica.ReceivedCommands;
        var indices = Enumerable.Range(0, commands.Count)
            .Where(index => commands[index].StartsWith(operation + " ", StringComparison.Ordinal)).ToArray();
        await Assert.That(indices.Length).IsEqualTo(2);
        await Assert.That(commands[indices[1]].Contains(operation == "SCAN" ? "SCAN 7 " : "tenant:key 7 ")).IsTrue();
        var connections = replica.ReceivedConnectionIds;
        await Assert.That(indices.Select(index => connections[index]).Distinct().Count()).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith(operation + " ", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task PendingClusterScriptsHonorLateErrorMetricActivation(bool lateListener, bool fallback)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = lateListener ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        using var earlyCapture = lateListener ? null : new Capture(throwOnMeasurement: true);
        var pendingReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray()
                : command.StartsWith("EVALSHA ", StringComparison.Ordinal) && fallback
                    ? "-NOSCRIPT private-script\r\n"u8.ToArray() : null,
            SuppressReply = command =>
            {
                if (!command.StartsWith(fallback ? "EVAL " : "EVALSHA ", StringComparison.Ordinal)) return false;
                pendingReply.TrySetResult();
                return true;
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = client.Scripts.ExecuteAsync(RespireScript.Create("return 1"), cancellationToken: deadline.Token).AsTask();
        await pendingReply.Task.WaitAsync(deadline.Token);
        using var lateCapture = lateListener ? new Capture(throwOnMeasurement: true) : null;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        await server.SendRawAsync("-WRONGTYPE private-key\r\n"u8.ToArray());
        var error = await Assert.That(async () => { using var result = await pending; }).Throws<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("WRONGTYPE");
        var items = (earlyCapture ?? lateCapture)!.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(1);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(fallback ? 1 : 0);
    }

    [Test]
    public async Task DuplicateObservationDisposalCannotShareAnActiveLease()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        var held = new HashSet<RespireTelemetry.ErrorObservation>();
        try
        {
            // Exhaust the bounded shared pool so both returns are the only available leases.
            for (var i = 0; i < 4096; i++) held.Add(RespireTelemetry.ErrorObservation.Rent());
            var returned = held.First();
            held.Remove(returned);
            returned.Dispose();
            returned.Dispose();
            var first = RespireTelemetry.ErrorObservation.Rent();
            var second = RespireTelemetry.ErrorObservation.Rent();
            held.Add(first);
            held.Add(second);
            await Assert.That(ReferenceEquals(ObservationStorage(first), ObservationStorage(second))).IsFalse();
            first.Handled(new RespireServerException("ERR first"));
            second.Final(new RespireServerException("ERR second"));
            await Assert.That(capture.Items.Last().Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        finally
        {
            foreach (var observation in held) observation.Dispose();
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task PendingFinalErrorUsesConfigurationAtCompletion(bool lateListener, bool generic)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = lateListener ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        using var earlyCapture = lateListener ? null : new Capture();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task pending = generic
            ? RespireTelemetry.ObserveFinalError(new ValueTask<int>(completion.Task)).AsTask()
            : RespireTelemetry.ObserveFinalError(new ValueTask(completion.Task)).AsTask();
        using var lateCapture = lateListener ? new Capture() : null;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        var error = new RespireServerException("WRONGTYPE private-key");
        completion.SetException(error);
        var observed = await Assert.That(async () => await pending).Throws<RespireServerException>();
        await Assert.That(ReferenceEquals(observed, error)).IsTrue();
        await Assert.That((earlyCapture ?? lateCapture)!.Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationBeforeReplyReservationReportsDiscardedServerErrorOnce()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        var source = new PendingResponsePool(1).Rent();
        var pending = RespireTelemetry.ObserveFinalError(source.Task).AsTask();
        // CompleteResponse can see this unfinished state before cancellation wins the CAS.
        await Assert.That(PendingResponse.IsCompleted(source.State)).IsFalse();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(source.TrySetCanceled(cancellation.Token)).IsTrue();
        var scheduler = new CompletionScheduler();
        scheduler.Add(source, RespValue.Error("WRONGTYPE private-key"u8.ToArray()));
        var error = await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That(items.Count(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(1);
        await Assert.That(items.Single(item => (bool)item.Tags["redis.client.errors.internal"]!)
            .Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
    }

    [Test]
    [MatrixDataSource]
    public async Task CancelledPrefixedRepliesReportTheirRetainedErrorOnce(
        [Matrix(false, true)] bool stream, [Matrix(false, true)] bool cancelBeforePrefix,
        [Matrix(0, 3)] int errorAttempts)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture(throwOnMeasurement: true);
        var streaming = stream ? new BulkStreamPendingResponseSource("GET", true, null) : null;
        var multi = stream ? null : MultiReplyPendingResponseSource.Rent(2, 0, "MULTI/EXEC");
        PendingResponse source = streaming is null ? multi! : streaming;
        source.ErrorAttempts = errorAttempts;
        Task pending = streaming is null ? RespireTelemetry.ObserveFinalError(multi!.Task).AsTask()
            : RespireTelemetry.ObserveFinalError(streaming.Task).AsTask();
        using var cancellation = new CancellationTokenSource();
        var scheduler = new CompletionScheduler();
        if (cancelBeforePrefix) { cancellation.Cancel(); source.TrySetCanceled(cancellation.Token); }
        var prefix = RespValue.Error("WRONGTYPE private-key"u8.ToArray());
        streaming?.ObservePrefix(in prefix);
        scheduler.Add(source, in prefix);
        scheduler.Flush();
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (!cancelBeforePrefix) { cancellation.Cancel(); source.TrySetCanceled(cancellation.Token); }
        scheduler.Add(source, RespValue.Integer(42));
        scheduler.Flush();
        await scheduler.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That(items.Count(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(1);
        await Assert.That(items.Single(item => (bool)item.Tags["redis.client.errors.internal"]!)
            .Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
        await Assert.That(items.Single(item => (bool)item.Tags["redis.client.errors.internal"]!)
            .Tags["redis.client.operation.retry_attempts"]).IsEqualTo(errorAttempts);
    }

    [Test]
    [Arguments("lease")]
    [Arguments("script")]
    [Arguments("timeout")]
    [Arguments("cancel")]
    [Arguments("cancel-revoke")]
    public async Task GuardedRemovalReportsItsFinalFailureAfterCorrection(string failure)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var scriptSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var correctionSawNoFinalFailure = false;
        var finalWasSafe = false;
        var leasePlaced = 0L;
        var leaseTtl = TimeSpan.FromSeconds(1);
        using var capture = new Capture(throwOnMeasurement: true, onMeasurement: item =>
        {
            if (!(bool)item.Tags["redis.client.errors.internal"]! && failure is "timeout" or "cancel" or "cancel-revoke")
                finalWasSafe = correctionSawNoFinalFailure
                    // The server lease's expiry is the safety boundary. The additional
                    // client margin does not extend the server lease's authority.
                    || Stopwatch.GetElapsedTime(Volatile.Read(ref leasePlaced)) >= leaseTtl;
        });
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) =>
            {
                if (command.StartsWith("SET ", StringComparison.Ordinal))
                    Volatile.Write(ref leasePlaced, Stopwatch.GetTimestamp());
                if (command.StartsWith("SET ", StringComparison.Ordinal) && failure == "lease")
                    return "-NOPERM lease rejected\r\n"u8.ToArray();
                if (command.StartsWith("EVAL ", StringComparison.Ordinal))
                    return "-WRONGTYPE script rejected\r\n"u8.ToArray();
                if (command.StartsWith("UNLINK ", StringComparison.Ordinal))
                {
                    if (failure == "cancel-revoke") return "-ERR revocation rejected\r\n"u8.ToArray();
                    correctionSawNoFinalFailure = !capture.Items.Any(item => !(bool)item.Tags["redis.client.errors.internal"]!);
                }
                return null;
            },
            SuppressReply = command =>
            {
                if (!command.StartsWith("EVAL ", StringComparison.Ordinal)) return false;
                scriptSeen.TrySetResult();
                return failure is "timeout" or "cancel" or "cancel-revoke";
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, CommandTimeout = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        client.RemovalLeaseTtl = leaseTtl;
        using var cancellation = new CancellationTokenSource();
        var removal = client.UnlinkGuardedAsync("private-key", cancellation.Token).AsTask();
        if (failure is "cancel" or "cancel-revoke")
        {
            await scriptSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
        }
        var error = await Assert.That(async () => await removal.WaitAsync(TimeSpan.FromSeconds(6))).Throws<Exception>();
        var final = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(final.Length).IsEqualTo(1);
        await Assert.That(final[0].Tags["error.type"]).IsEqualTo(error!.GetType().FullName);
        if (failure is "timeout" or "cancel" or "cancel-revoke")
        {
            await Assert.That(finalWasSafe).IsTrue();
            if (failure == "timeout")
                await Assert.That(error is RespireTimeoutException { CommandName: "UNLINK" }).IsTrue();
            else
                await Assert.That(((OperationCanceledException)error).CancellationToken.IsCancellationRequested).IsTrue();
        }
        if (failure == "lease")
            await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("EVAL ", StringComparison.Ordinal))).IsFalse();
        if (failure == "cancel-revoke")
            await Assert.That(capture.Items.Count(item => (bool)item.Tags["redis.client.errors.internal"]!
                && Equals(item.Tags.GetValueOrDefault("db.response.status_code"), "ERR"))).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task InFlightRawErrorsHonorLateMetricActivation(bool cache, bool lateListener)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = lateListener ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : null,
            SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
            ClientSideCache = cache ? new() { CoalesceConcurrentMisses = true } : null,
        });
        using var earlyCapture = lateListener ? null : new Capture();
        var pending = client.ExecuteAsync("GET", "private-key").AsTask();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.ReceivedCommands.Any(command => command.StartsWith("GET ", StringComparison.Ordinal)))
            await Task.Delay(1, deadline.Token);
        using var lateCapture = lateListener ? new Capture() : null;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Resiliency });
        var getIndex = server.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("GET ", StringComparison.Ordinal));
        await server.SendRawAsync("-WRONGTYPE private-key\r\n"u8.ToArray(), server.ReceivedConnectionIds[getIndex]);
        await Assert.That(async () => { using var reply = await pending.WaitAsync(deadline.Token); }).Throws<RespireServerException>();
        await Assert.That((earlyCapture ?? lateCapture)!.Items.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task StreamingTransportReroutesCarryAttempts(bool capacityWait, bool payloadFailure)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
                ? payloadFailure ? "$100\r\npartial"u8.ToArray() : "-WRONGTYPE private-key\r\n"u8.ToArray() : null,
            SuppressReply = command => command == "PING hold",
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2, MaxInflightCommands = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var connection = client.Core.Multiplexer.GetConnection();
        Task<RespValue>? held = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (capacityWait)
        {
            held = connection.SendCheckedAsync(new RawCommand("*2\r\n$4\r\nPING\r\n$4\r\nhold\r\n"u8.ToArray())).AsTask();
            while (!server.ReceivedCommands.Contains("PING hold")) await Task.Delay(1, deadline.Token);
        }
        else await connection.RetireAsync();
        using var capture = new Capture(throwOnMeasurement: true);
        var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        var pending = RespireTelemetry.ObserveStreamError(connection.SendBulkStreamAsync(
            new Cmd1(Verbs.Get, "private-key"), deadline.Token, "GET", observation: observation), observation).AsTask();
        Task? retirement = null;
        if (capacityWait)
        {
            await Assert.That(pending.IsCompleted).IsFalse();
            retirement = connection.RetireAsync();
        }
        if (payloadFailure)
        {
            using var stream = (await pending.WaitAsync(deadline.Token))!;
            var replacement = client.Core.Multiplexer.GetConnection();
            var getIndex = server.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("GET ", StringComparison.Ordinal));
            // End only the replacement's payload while preserving the original accepted command.
            server.CloseConnection(server.ReceivedConnectionIds[getIndex]);
            await Assert.That(async () => await stream.CopyToAsync(Stream.Null, deadline.Token)).Throws<Exception>();
            // Payload failure can reach the reader before the receive loop records its abort.
            await replacement.Closed.WaitAsync(deadline.Token);
        }
        else await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<RespireServerException>();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(payloadFailure ? 3 : 2);
        var retirementError = items.Single(item => item.Tags["error.type"]?.ToString()
            == typeof(RespireConnectionRetiredException).FullName);
        await Assert.That((bool)retirementError.Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(retirementError.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        var final = items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        if (held is not null)
        {
            var holdIndex = server.ReceivedCommands.ToList().IndexOf("PING hold");
            await server.SendRawAsync(FakeRespServer.PongReply, server.ReceivedConnectionIds[holdIndex]);
            using var reply = await held.WaitAsync(deadline.Token);
            await retirement!.WaitAsync(deadline.Token);
        }
    }

    [Test]
    public async Task ObservationLeaseReportsFinalOnceAndResetsAttempts()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        var error = new RespireServerException("ERR private-data");
        using (var observation = RespireTelemetry.ErrorObservation.Rent())
        {
            observation.Handled(error);
            observation.Final(error);
            observation.Final(error);
        }
        using (var observation = RespireTelemetry.ErrorObservation.Rent()) observation.Final(error);
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(3);
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        await Assert.That(items[2].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SuccessfulObservationLeasesAllocateNothingAfterWarmup(bool generic)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        for (var i = 0; i < 5; i++) { MeasureObservation(false, generic); MeasureObservation(true, generic); }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Bytes: MeasureObservation(false, generic), Control: MeasureObservation(true, generic)));
        await Assert.That(measured.Bytes).IsEqualTo(0L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000L);
        await Assert.That(capture.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ErrorTypeNamesDoNotKeepCollectibleExceptionTypesAlive()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        var type = RecordCollectibleError();
        // Complete a fixed collection sequence, including loader-allocator finalization.
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        await Assert.That(type.IsAlive).IsFalse();
        await Assert.That(capture.Items.Single().Tags["error.type"]).IsEqualTo("CollectibleMetricError");
    }

    [Test]
    [Arguments("string", false)]
    [Arguments("bytes", false)]
    [Arguments("typed", false)]
    [Arguments("string", true)]
    [Arguments("bytes", true)]
    [Arguments("typed", true)]
    public async Task TypedDisposedAdmissionHasOneFinalBoundary(string path, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await client.DisposeAsync();
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        await Assert.That(async () =>
        {
            if (path == "string") await client.GetStringAsync("key");
            else if (path == "bytes") await client.GetBytesAsync("key");
            else await client.PublishAsync("key", "value");
        }).Throws<ObjectDisposedException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CheckedTransportRetirementCarriesAttemptsToItsFinalOwner(bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(2, "-WRONGTYPE private-key\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var retired = client.Core.Multiplexer.GetConnection();
        await retired.RetireAsync();
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var observation = RespireTelemetry.ErrorObservation.Rent();
        await Assert.That(async () =>
        {
            using var reply = await RespireTelemetry.ObserveFinalError(client.SendOnConnectionAsync(
                "GET", retired, new Cmd1(Verbs.Get, "key"), default, observation: observation), observation);
        }).Throws<RespireServerException>();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(2);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancelledReadDoesNotHideALaterPayloadFailure(bool legacyRead)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer("$100\r\npartial"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var stream = (await client.Strings.GetStreamAsync("key"))!;
        var buffer = new byte[7];
        await stream.ReadExactlyAsync(buffer);
        using var capture = new Capture();
        using var cancellation = new CancellationTokenSource();
        var read = legacyRead ? stream.ReadAsync(buffer, 0, buffer.Length, cancellation.Token)
            : stream.ReadAsync(buffer.AsMemory(), cancellation.Token).AsTask();
        await cancellation.CancelAsync();
        await Assert.That(async () => await read).Throws<OperationCanceledException>();
        await server.SendRawAsync("next"u8.ToArray());
        await Assert.That(await stream.ReadAsync(buffer)).IsEqualTo(4);
        await server.DisposeAsync();
        await Assert.That(async () => await stream.ReadAsync(buffer)).Throws<Exception>();
        await Assert.That(async () => await stream.ReadAsync(buffer)).Throws<Exception>();
        var failures = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(failures.Length).IsEqualTo(2);
        await Assert.That(failures[0].Tags["redis.client.errors.category"]).IsEqualTo("cancelled");
        await Assert.That(failures[1].Tags["redis.client.errors.category"]).IsEqualTo("network");
    }

    [Test]
    [Arguments("original")]
    [Arguments("hedge")]
    [Arguments("late")]
    [Arguments("both")]
    public async Task DiscardedHedgeLegFailuresHaveOneInternalOwner(string failure)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var primary = new FakeRespServer(FakeRespServer.OkReply)
            { SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal) };
        await using var replica = new FakeRespServer("*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray())
            { SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal) };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)], ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            ReadFrom = RespireReadFrom.ReplicaPreferred,
            HedgedReads = new() { Delay = TimeSpan.FromMilliseconds(1), MaximumExtraLoadPercent = 100 },
        });
        using var capture = new Capture(throwOnMeasurement: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = client.GetStringAsync("key", deadline.Token).AsTask();
        while (!primary.ReceivedCommands.Contains("GET key")) await Task.Delay(1, deadline.Token);
        var failed = failure == "original" ? replica : primary;
        var succeeded = ReferenceEquals(failed, primary) ? replica : primary;
        if (failure == "late")
        {
            await succeeded.SendRawAsync("$2\r\nok\r\n"u8.ToArray());
            await Assert.That(await read.WaitAsync(deadline.Token)).IsEqualTo("ok");
        }
        await failed.SendRawAsync("-WRONGTYPE private-key\r\n"u8.ToArray());
        if (failure != "late")
        {
            await succeeded.SendRawAsync(failure == "both" ? "-ERR original-error\r\n"u8.ToArray() : "$2\r\nok\r\n"u8.ToArray());
            if (failure == "both") await Assert.That(async () => await read.WaitAsync(deadline.Token)).Throws<RespireServerException>();
            else await Assert.That(await read.WaitAsync(deadline.Token)).IsEqualTo("ok");
        }
        var expected = failure == "both" ? 3 : 1;
        while (capture.Items.Count < expected) await Task.Delay(1, deadline.Token);
        await Assert.That(capture.Items.Count).IsEqualTo(expected);
        await Assert.That(capture.Items.Count(item => (bool)item.Tags["redis.client.errors.internal"]!))
            .IsEqualTo(failure == "both" ? 2 : 1);
    }

    [Test]
    public async Task CancellationTimeoutAndDeepWrappersKeepDocumentedCategories()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new OperationCanceledException(cancellation.Token);
        RespireTelemetry.RecordError(cancelled, internallyHandled: false);
        RespireTelemetry.RecordError(new RespireTimeoutException("GET", TimeSpan.FromSeconds(1)), internallyHandled: false);
        Exception wrapped = new RespireServerException("WRONGTYPE private-key");
        for (var depth = 0; depth < 17; depth++) wrapped = new RespireConnectionException("wrapper", wrapped);
        RespireTelemetry.RecordError(wrapped, internallyHandled: false);
        var items = capture.Items.ToArray();
        await Assert.That(items[0].Tags["redis.client.errors.category"]).IsEqualTo("cancelled");
        await Assert.That(items[0].Tags["error.type"]).IsEqualTo(typeof(OperationCanceledException).FullName);
        await Assert.That(cancelled.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(items[1].Tags["redis.client.errors.category"]).IsEqualTo("network");
        await Assert.That(items[1].Tags["error.type"]).IsEqualTo(typeof(RespireTimeoutException).FullName);
        await Assert.That(items[2].Tags["redis.client.errors.category"]).IsEqualTo("network");
        await Assert.That(items[2].Tags["error.type"]).IsEqualTo(typeof(RespireConnectionException).FullName);
        await Assert.That(items[2].Tags.ContainsKey("db.response.status_code")).IsFalse();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(0)]
    [Arguments(16)]
    [Arguments(17)]
    [Arguments(int.MaxValue)]
    public async Task RetryTagsPreserveCountsBeyondTheBoxCache(int attempts)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        RespireTelemetry.RecordError(new RespireServerException("ERR private-data"), false, attempts);
        await Assert.That(capture.Items.Single().Tags["redis.client.operation.retry_attempts"])
            .IsEqualTo(Math.Max(0, attempts));
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(16, false)]
    [Arguments(0, true)]
    [Arguments(1, true)]
    [Arguments(16, true)]
    public async Task EnabledErrorReportsReuseTypeNamesAndCommonRetryTags(int attempts, bool fullCollection)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, observer) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "redis.client.errors")
                observer.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        listener.Start();
        var error = new RespireServerException("WRONGTYPE private-key");
        for (var i = 0; i < 5; i++) { Measure(error, false, attempts); Measure(error, true, attempts); }
        if (fullCollection) GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
        {
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var bytes = Measure(error, false, attempts);
            var collections = (Gen0: GC.CollectionCount(0) - gen0,
                Gen1: GC.CollectionCount(1) - gen1, Gen2: GC.CollectionCount(2) - gen2);
            return (Bytes: bytes, Control: Measure(error, true, attempts), Collections: collections);
        });
        Console.WriteLine($"Error allocation: attempts={attempts}, full collection={fullCollection}, bytes={measured.Bytes}, control={measured.Control}, GC deltas={measured.Collections}");
        await Assert.That(measured.Bytes).IsEqualTo(0L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000L);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CachedMultiKeyAndHashReadsHaveOneFinalBoundary(bool cluster, bool coalesce)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "CLUSTER SLOTS" => "*0\r\n"u8.ToArray(),
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                _ when command.StartsWith("MGET ", StringComparison.Ordinal)
                    || command.StartsWith("HMGET ", StringComparison.Ordinal) => "-WRONGTYPE private-key\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = cluster, ClusterTopologyRefreshInterval = null,
            ClientSideCache = new() { CoalesceConcurrentMisses = coalesce, ReuseHashFields = true },
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        foreach (var path in new[] { "typed", "raw", "hash" })
        {
            using var capture = new Capture(throwOnMeasurement: true);
            await Assert.That(async () =>
            {
                if (path == "typed") await client.Strings.GetManyAsync("{same}:one", "{same}:two");
                else if (path == "raw") { using var reply = await client.ExecuteAsync("MGET", "{same}:one", "{same}:two"); }
                else { using var reply = await client.ExecuteAsync("HMGET", "key", "one", "two"); }
            }).Throws<RespireServerException>();
            await Assert.That(capture.Items.Count).IsEqualTo(1);
            await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
        }
    }

    [Test]
    public async Task KnownWrappersRetainServerCodesAndAuthenticationMeaning()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        RespireTelemetry.RecordError(new RespireTransactionRetryException(
            new RespireServerException("MOVED 1 private-host:6379")), internallyHandled: false);
        RespireTelemetry.RecordError(new RespireAuthenticationException("private-credential",
            new RespireServerException("WRONGPASS private-credential")), internallyHandled: false);
        RespireTelemetry.RecordError(new RespireScriptingEngineUnavailableException(
            "lua", new("127.0.0.1", 6379), new RespireServerException("NOPERM private-script")), internallyHandled: true);
        var items = capture.Items.ToArray();
        await Assert.That(items[0].Tags["redis.client.errors.category"]).IsEqualTo("server");
        await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("MOVED");
        await Assert.That(items[1].Tags["redis.client.errors.category"]).IsEqualTo("auth");
        await Assert.That(items[1].Tags["db.response.status_code"]).IsEqualTo("WRONGPASS");
        await Assert.That(items[2].Tags["redis.client.errors.category"]).IsEqualTo("auth");
        await Assert.That(items[2].Tags["db.response.status_code"]).IsEqualTo("NOPERM");
        await Assert.That(items.SelectMany(item => item.Tags.Values)
            .Any(value => value?.ToString()?.Contains("private-", StringComparison.Ordinal) == true)).IsFalse();
    }

    [Test]
    public async Task EnabledErrorListenerPreservesSuccessfulTypedSourceAllocations()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        for (var i = 0; i < 5; i++) { MeasureSuccessfulSource(false); MeasureSuccessfulSource(true); }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Bytes: MeasureSuccessfulSource(false), Control: MeasureSuccessfulSource(true)));
        await Assert.That(measured.Bytes).IsEqualTo(0L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000L);
        await Assert.That(capture.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StreamPayloadFailureIsCountedWhenReadAndOnlyOnce()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer("$100\r\npartial"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var stream = await client.Strings.GetStreamAsync("key", deadline.Token);
        await Assert.That(stream).IsNotNull();
        await server.DisposeAsync();
        await Assert.That(async () => await stream!.CopyToAsync(Stream.Null, deadline.Token)).Throws<Exception>();
        var userErrors = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(userErrors.Length).IsEqualTo(1);
        await Assert.That(userErrors[0].Tags["redis.client.errors.category"]).IsEqualTo("network");
        await Assert.That(async () => await stream!.ReadAsync(new byte[1], deadline.Token)).Throws<Exception>();
        await Assert.That(capture.Items.Count(item => !(bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    [Arguments(true, true, true)]
    public async Task CachedReadFailuresAreObservedOncePerCaller(bool cluster, bool coalesce, bool raw)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "CLUSTER SLOTS" => "*0\r\n"u8.ToArray(),
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                _ when command.StartsWith("GET ", StringComparison.Ordinal) => "-WRONGTYPE private-key\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = cluster, ClusterTopologyRefreshInterval = null,
            ClientSideCache = new() { CoalesceConcurrentMisses = coalesce },
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        await Assert.That(async () =>
        {
            if (raw) { using var response = await client.ExecuteAsync("GET", "key"); }
            else await client.GetStringAsync("key");
        }).Throws<RespireServerException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
    }

    [Test]
    public async Task CoalescedFailureCountsEachCallerWithoutAddingAProducerFailure()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3"
                ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : FakeRespServer.OkReply,
            SuppressReply = command =>
            {
                if (!command.StartsWith("GET ", StringComparison.Ordinal)) return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, ClientSideCache = new() { CoalesceConcurrentMisses = true },
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        var first = client.GetStringAsync("key").AsTask();
        var second = client.GetStringAsync("key").AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.SendRawAsync("-WRONGTYPE private-key\r\n"u8.ToArray());
        await Assert.That(async () => await first).Throws<RespireServerException>();
        await Assert.That(async () => await second).Throws<RespireServerException>();
        await Assert.That(capture.Items.Count).IsEqualTo(2);
        await Assert.That(capture.Items.All(item => !(bool)item.Tags["redis.client.errors.internal"]!)).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("GET ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConversionFailuresHaveOneOwner(bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        await Assert.That(async () => await client.ConvertResponseAsync<Cmd1, int, int>(
                "GET", new Cmd1(Verbs.Get, "key"), CancellationToken.None, 0,
                static (int _, in RespValue value) => throw new InvalidOperationException("private-value"),
                transferOwnership: false))
            .Throws<InvalidOperationException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That(capture.Items.Single().Tags["redis.client.errors.category"]).IsEqualTo("other");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeferredFailuresAreCountedOnceAfterExecution(bool transaction)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var replies = transaction
            ? new[] { FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(),
                "*2\r\n-WRONGTYPE private-key\r\n+OK\r\n"u8.ToArray() }
            : new[] { "-WRONGTYPE private-key\r\n"u8.ToArray(), FakeRespServer.OkReply };
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        RespirePending<bool> failed;
        RespirePending<bool> succeeded;
        if (transaction)
        {
            await using var pending = client.CreateTransaction();
            failed = pending.Set("one", "value");
            succeeded = pending.Set("two", "value");
            await pending.CommitAsync();
        }
        else
        {
            var batch = client.CreateBatch();
            failed = batch.Set("one", "value");
            succeeded = batch.Set("two", "value");
            var result = await batch.TryExecuteAsync();
            await Assert.That(result.FailureCount).IsEqualTo(1);
            await Assert.That(result.ThrowIfAnyFailed).Throws<RespireServerException>();
        }
        await Assert.That(succeeded.Result).IsTrue();
        await Assert.That(() => failed.Result).Throws<RespireServerException>();
        await Assert.That(() => failed.Result).Throws<RespireServerException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        var item = capture.Items.Single();
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(item.Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task FireAndForgetDiscardsServerErrorsAcrossRoutes(bool cluster, bool cache)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "CLUSTER SLOTS" => "*0\r\n"u8.ToArray(),
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                "PING" => FakeRespServer.PongReply,
                _ when command.StartsWith("SET ", StringComparison.Ordinal) => "-NOPERM private-key\r\n"u8.ToArray(),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = cluster, ClusterTopologyRefreshInterval = null,
            ClientSideCache = cache ? new() : null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        await client.ExecuteFireAndForgetAsync("SET", "private-key", "value");
        using var barrier = await client.ExecuteAsync("PING");
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(capture.Items.Single().Tags["db.response.status_code"]).IsEqualTo("NOPERM");
    }

    [Test]
    public async Task FireAndForgetAdmissionFailureIsUserVisible()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await client.DisposeAsync();
        using var capture = new Capture(throwOnMeasurement: true);
        await Assert.That(async () => await client.ExecuteFireAndForgetAsync("SET", "key", "value"))
            .Throws<ObjectDisposedException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FencedClusterFireAndForgetRetainsRedirectOwner(bool cancel)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var targetSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : null,
            SuppressReply = command =>
            {
                if (!command.StartsWith("SET ", StringComparison.Ordinal)) return false;
                targetSeen.TrySetResult();
                return cancel;
            },
        };
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "CLUSTER SLOTS" => "*0\r\n"u8.ToArray(),
                "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
                _ when command.StartsWith("SET ", StringComparison.Ordinal)
                    => Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n"),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            ClientSideCache = new(), Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        using var cancellation = new CancellationTokenSource();
        var pending = client.ExecuteFireAndForgetAsync("SET", ["key", "value"], cancellation.Token).AsTask();
        await targetSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel)
        {
            cancellation.Cancel();
            var error = await Assert.That(async () => await pending).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        else await pending;
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(cancel ? 2 : 1);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("MOVED");
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        if (cancel)
        {
            await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments("string", false)]
    [Arguments("bytes", false)]
    [Arguments("typed", false)]
    [Arguments("string", true)]
    [Arguments("bytes", true)]
    [Arguments("typed", true)]
    public async Task NativeAdmissionErrorsKeepTheirRetryCount(string path, bool retry)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(3, "-WRONGTYPE private-key\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 2,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        await using var isolated = retry ? null : await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var connection = isolated ?? client.Core.Multiplexer.GetConnection();
        await connection.RetireAsync();
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute()
        {
            var command = new Cmd1(Verbs.Get, "key");
            switch (path)
            {
                case "string": await connection.SendStringAsync(command, commandName: "GET"); break;
                case "bytes": await connection.SendBytesAsync(command, commandName: "GET"); break;
                default:
                    await connection.SendConvertedAsync(command, 0,
                        static (int _, in RespValue value) => ResponseReader.String(in value),
                        transferOwnership: false, commandName: "GET");
                    break;
            }
        }
        if (retry) await Assert.That(Execute).Throws<RespireServerException>();
        else await Assert.That(Execute).Throws<RespireConnectionRetiredException>();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(retry ? 2 : 1);
        if (retry)
        {
            await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
            await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        }
        await Assert.That((bool)items[^1].Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(items[^1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(retry ? 1 : 0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RawScriptsAndBlockingCommandsReportOneFinalError(bool cluster, bool blocking)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var target = new FakeRespServer(2, "-WRONGTYPE private-key\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("key");
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = cluster, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", cluster ? seed.Port : target.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.That(async () =>
        {
            using var result = blocking
                ? await client.ExecuteAsync("BLPOP", ["key", 0], cancellationToken: deadline.Token)
                : await client.ExecuteAsync("EVALSHA", ["private-script", 1, "key"], cancellationToken: deadline.Token);
        }).Throws<RespireServerException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        var item = capture.Items.Single();
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(item.Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ScriptFallbackSeparatesRecoveredAndFinalErrors(bool cluster, bool fail)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var replies = new List<byte[]>();
        if (cluster) replies.Add("*0\r\n"u8.ToArray());
        replies.Add("-NOSCRIPT private-script\r\n"u8.ToArray());
        replies.Add(fail ? "-ERR private-script\r\n"u8.ToArray() : ":1\r\n"u8.ToArray());
        await using var server = new FakeRespServer(replies.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = cluster, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute()
        {
            using var result = await client.Scripts.ExecuteAsync(RespireScript.Create("return 1"));
        }
        if (fail) await Assert.That(Execute).Throws<RespireServerException>();
        else await Execute();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(fail ? 2 : 1);
        await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("NOSCRIPT");
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        if (fail)
        {
            await Assert.That(items[1].Tags["db.response.status_code"]).IsEqualTo("ERR");
            await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments("string", false)]
    [Arguments("bytes", false)]
    [Arguments("typed", false)]
    [Arguments("raw", false)]
    [Arguments("stream", false)]
    [Arguments("upload", false)]
    [Arguments("string", true)]
    [Arguments("bytes", true)]
    [Arguments("typed", true)]
    [Arguments("raw", true)]
    [Arguments("stream", true)]
    [Arguments("upload", true)]
    public async Task FinalServerFailureIsObservedOnce(string path, bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None) });
        await using var server = new FakeRespServer(2, "-WRONGTYPE private-key secret-value\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true, commandMetrics: commandMetrics);
        var error = await Assert.ThrowsAsync<RespireServerException>(async () =>
        {
            switch (path)
            {
                case "string": await client.GetStringAsync("private-key"); break;
                case "bytes": await client.GetBytesAsync("private-key"); break;
                case "typed": await client.PublishAsync("private-key", "secret-value"); break;
                case "stream": using (await client.Strings.GetStreamAsync("private-key")) { } break;
                case "upload":
                    using (var source = new MemoryStream("value"u8.ToArray()))
                        await client.Strings.SetAsync("private-key", source, source.Length);
                    break;
                default: using (await client.ExecuteAsync("GET", "private-key")) { } break;
            }
        });
        await Assert.That(error!.Code).IsEqualTo("WRONGTYPE");
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        var item = capture.Items.Single();
        await Assert.That(item.Value).IsEqualTo(1L);
        await Assert.That(item.Unit).IsEqualTo("{error}");
        await Assert.That(item.Tags["redis.client.errors.category"]).IsEqualTo("server");
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        await Assert.That(item.Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
        await Assert.That(item.Tags["db.system.name"]).IsEqualTo("redis");
        await Assert.That(item.Tags.ContainsKey("redis.client.library")).IsTrue();
        await Assert.That(item.Tags.ContainsKey("error.type")).IsTrue();
        await Assert.That(item.Tags.Values.Any(value => value?.ToString()?.Contains("private-key") == true
            || value?.ToString()?.Contains("secret-value") == true)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CallerCancellationPreservesTokenAndCountsOnlyItsBoundary(bool typed)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer("+PONG\r\n"u8.ToArray())
            { SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal) };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture();
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        async Task Execute()
        {
            if (typed) await client.GetStringAsync("private-key", cancellation.Token);
            else using (await client.ExecuteAsync("GET", ["private-key"], cancellationToken: cancellation.Token)) { }
        }
        var pending = Execute();
        while (server.CommandsSeen == 0) await Task.Delay(1, deadline.Token);
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsFalse();
        await server.SendRawAsync("$-1\r\n"u8.ToArray());
        using var barrier = await client.ExecuteAsync("PING");
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DiscardedServerErrorIsInternalAndDoesNotFailTheCaller()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer("-NOPERM private-key\r\n"u8.ToArray(), "+PONG\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture(throwOnMeasurement: true);
        await client.ExecuteFireAndForgetAsync("GET", "private-key");
        using var barrier = await client.ExecuteAsync("PING");
        await Assert.That(capture.Items.Count).IsEqualTo(1);
        await Assert.That((bool)capture.Items.Single().Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(capture.Items.Single().Tags["redis.client.errors.category"]).IsEqualTo("auth");
    }

    [Test]
    [Arguments("WRONGTYPE", "server", true)]
    [Arguments("NOAUTH", "auth", true)]
    [Arguments("NOPERM", "auth", true)]
    [Arguments("WRONGPASS", "auth", true)]
    [Arguments("private-key", "server", false)]
    public async Task ServerPrefixesAreClassifiedWithoutExposingArbitraryData(string code, string category, bool standard)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        RespireTelemetry.RecordError(new RespireServerException(code + " secret-value"), internallyHandled: true, retryAttempts: 2);
        var item = capture.Items.Single();
        await Assert.That(item.Tags["redis.client.errors.category"]).IsEqualTo(category);
        await Assert.That(item.Tags.ContainsKey("db.response.status_code")).IsEqualTo(standard);
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(item.Tags["redis.client.operation.retry_attempts"]).IsEqualTo(2);
        await Assert.That(item.Tags.Values.Any(value => value?.ToString()?.Contains("private-key") == true
            || value?.ToString()?.Contains("secret-value") == true)).IsFalse();
    }

    [Test]
    [Arguments(false, "typed")]
    [Arguments(false, "raw")]
    [Arguments(false, "batch")]
    [Arguments(false, "stream")]
    [Arguments(true, "typed")]
    [Arguments(true, "raw")]
    [Arguments(true, "batch")]
    [Arguments(true, "stream")]
    public async Task ClusterRedirectsDistinguishRecoveryFromFinalFailure(bool fail, string path)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var target = new FakeRespServer(fail ? "-WRONGTYPE private-key\r\n"u8.ToArray() : "$5\r\nvalue\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer("*0\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, ClusterTopologyRefreshInterval = null,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var capture = new Capture(throwOnMeasurement: true);
        async Task Execute()
        {
            if (path == "raw") { using var result = await client.ExecuteAsync("GET", "key"); }
            else if (path == "stream")
            {
                await using var stream = await client.Strings.GetStreamAsync("key");
                await stream!.CopyToAsync(Stream.Null);
            }
            else if (path == "batch")
            {
                var batch = client.CreateBatch();
                var pending = batch.GetString("key");
                (await batch.ExecuteAsync()).ThrowIfAnyFailed();
                await Assert.That(pending.Result).IsEqualTo("value");
            }
            else await client.GetStringAsync("key");
        }
        if (fail) await Assert.That(Execute).Throws<RespireServerException>();
        else await Execute();
        var items = capture.Items.ToArray();
        await Assert.That(items.Length).IsEqualTo(fail ? 2 : 1);
        await Assert.That((bool)items[0].Tags["redis.client.errors.internal"]!).IsTrue();
        await Assert.That(items[0].Tags["db.response.status_code"]).IsEqualTo("MOVED");
        await Assert.That(items[0].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(0);
        if (fail)
        {
            await Assert.That((bool)items[1].Tags["redis.client.errors.internal"]!).IsFalse();
            await Assert.That(items[1].Tags["db.response.status_code"]).IsEqualTo("WRONGTYPE");
            await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
    }

    [Test]
    public async Task ConnectionFailureAndCommandFailureAreDistinctBoundaries()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.OkReply) { CloseConnectionAfterCommand = 1 };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var capture = new Capture();
        await Assert.That(async () => await client.GetStringAsync("key")).Throws<RespireConnectionException>();
        await Assert.That(capture.Items.Count).IsEqualTo(2);
        await Assert.That(capture.Items.Count(item => (bool)item.Tags["redis.client.errors.internal"]!)).IsEqualTo(1);
        await Assert.That(capture.Items.All(item => (string)item.Tags["redis.client.errors.category"]! == "network")).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisabledGroupsOrListenersAllocateNothing(bool listenerEnabled)
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = listenerEnabled ? RespireMetricGroups.None : RespireMetricGroups.Resiliency });
        using var capture = listenerEnabled ? new Capture() : null;
        var error = new RespireServerException("WRONGTYPE private-key");
        for (var i = 0; i < 5; i++) { Measure(error, false); Measure(error, true); }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => (Bytes: Measure(error, false), Control: Measure(error, true)));
        await Assert.That(measured.Bytes).IsEqualTo(0L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000L);
        if (capture is not null) await Assert.That(capture.Items.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RejectedAuthenticationIsOneUserFailureWithoutCredentials()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer("-WRONGPASS private-user private-password\r\n"u8.ToArray());
        using var capture = new Capture(throwOnMeasurement: true);
        await Assert.That(async () =>
        {
            await using var client = await RespireClient.ConnectAsync(new RespireOptions
            {
                Protocol = RespProtocol.Resp2, Username = "private-user", Password = "private-password",
                Endpoints = [new("127.0.0.1", server.Port)],
            });
        }).Throws<RespireException>();
        var item = capture.Items.Single();
        await Assert.That(item.Tags["redis.client.errors.category"]).IsEqualTo("auth");
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(item.Tags.Values.Any(value => value?.ToString()?.Contains("private-") == true)).IsFalse();
    }

    [Test]
    public async Task CompletedTransportStillObservesErrorTranslatedByGetResult()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        var source = new PendingResponseSource();
        source.Configure(throwOnError: true, commandName: "GET");
        source.PrepareForUse();
        var reply = RespValue.Error("WRONGTYPE private-key");
        source.TrySetResult(in reply);
        source.ReleaseRef();
        var response = source.Task;
        await Assert.That(response.IsCompletedSuccessfully).IsTrue();
        await Assert.That(async () =>
        {
            using var result = await RespireTelemetry.ObserveFinalError(response);
        }).Throws<RespireServerException>();
        await Assert.That(capture.Items.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RejectedTlsCertificateIsAUserTlsFailure()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var certificate = TlsTests.CreateCertificate();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeAsync();
        using var capture = new Capture();
        try
        {
            await Assert.That(async () =>
            {
                await using var client = await RespireClient.ConnectAsync(new RespireOptions
                {
                    Protocol = RespProtocol.Resp2, UseTls = true, Endpoints = [new("127.0.0.1", port)],
                    TlsOptions = new SslClientAuthenticationOptions
                    { RemoteCertificateValidationCallback = (_, _, _, _) => false },
                }, deadline.Token);
            }).Throws<AuthenticationException>();
            await server.WaitAsync(deadline.Token);
            var item = capture.Items.Single();
            await Assert.That(item.Tags["redis.client.errors.category"]).IsEqualTo("tls");
            await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        }
        finally
        {
            deadline.Cancel();
            listener.Stop();
            try { await server; }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }

        async Task ServeAsync()
        {
            using var socket = await listener.AcceptSocketAsync(deadline.Token);
            await using var stream = new SslStream(new NetworkStream(socket));
            try
            {
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 }, deadline.Token);
            }
            catch (AuthenticationException) { }
            catch (IOException) { }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RecordCollectibleError()
    {
        var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new System.Reflection.AssemblyName("Respire.ErrorMetricCollectibility"),
            System.Reflection.Emit.AssemblyBuilderAccess.RunAndCollect);
        var builder = assembly.DefineDynamicModule("errors").DefineType("CollectibleMetricError",
            System.Reflection.TypeAttributes.Public, typeof(Exception));
        builder.DefineDefaultConstructor(System.Reflection.MethodAttributes.Public);
        var type = builder.CreateType()!;
        RespireTelemetry.RecordError((Exception)Activator.CreateInstance(type)!, internallyHandled: false);
        return new WeakReference(type);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureObservation(bool control, bool generic)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var observation = RespireTelemetry.ErrorObservation.Rent();
            if (observation.IsEmpty) throw new InvalidOperationException("Error observation must be enabled.");
            if (generic)
            {
                var result = RespireTelemetry.ObserveFinalError(new ValueTask<int>(1), observation).GetAwaiter().GetResult();
                if (result != 1) throw new InvalidOperationException("Unexpected result.");
            }
            else RespireTelemetry.ObserveFinalError(ValueTask.CompletedTask, observation).GetAwaiter().GetResult();
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureSuccessfulSource(bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            var source = ConvertedPendingResponseSource<int, long>.Rent(0,
                static (int _, in RespValue value) => value.AsInteger(), false, "PING");
            var response = RespValue.Integer(1);
            source.TrySetResult(in response);
            source.ReleaseRef();
            if (source.Task.GetAwaiter().GetResult() != 1) throw new InvalidOperationException("Unexpected reply.");
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(Exception error, bool control, int retryAttempts = 0)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            RespireTelemetry.RecordError(error, internallyHandled: false, retryAttempts);
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private sealed record Item(long Value, string? Unit, Dictionary<string, object?> Tags);

    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        internal ConcurrentQueue<Item> Items { get; } = new();

        internal Capture(bool throwOnMeasurement = false, bool commandMetrics = false, Action<Item>? onMeasurement = null)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Respire" && (instrument.Name == "redis.client.errors"
                    || commandMetrics && instrument.Name == "db.client.operation.duration"))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var item = new Item(value, instrument.Unit, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value));
                Items.Enqueue(item);
                onMeasurement?.Invoke(item);
                if (throwOnMeasurement) throw new InvalidOperationException("Listener failure must remain isolated.");
            });
            _listener.SetMeasurementEventCallback<double>((_, _, _, _) => { });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
