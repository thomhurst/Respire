using System.Reflection;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Respire.HealthChecks;
using Respire.Testing;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RespireHealthProbeContractTests
{
    private static HealthCheckContext Context()
        => new() { Registration = new("respire", _ => throw new NotSupportedException(), HealthStatus.Unhealthy, null) };

    public interface IProbeClient : IRespireClient, IRespireHealthProbe { }

    public class ProbeClientProxy : RespireHealthCheckTests.HealthClientProxy
    {
        public Func<RespireHealthProbeOptions, CancellationToken, ValueTask<RespireHealthProbeResult[]>>? Probe { get; set; }
        public Func<CancellationToken, ValueTask<TimeSpan>>? Ping { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IRespireHealthProbe.ProbeHealthAsync))
                return Probe!((RespireHealthProbeOptions)args![0]!, (CancellationToken)args[1]!);
            if (targetMethod?.Name == nameof(IRespireClient.PingAsync) && Ping is not null)
                return Ping((CancellationToken)args![0]!);
            return base.Invoke(targetMethod, args);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InvalidProviderResultsHaveClearDiagnostics(bool nullElement)
    {
        var client = DispatchProxy.Create<IProbeClient, ProbeClientProxy>();
        ((ProbeClientProxy)client).Probe = (_, _) => ValueTask.FromResult<RespireHealthProbeResult[]>(nullElement ? [null!] : null!);
        var result = await new RespireHealthCheck(client).CheckHealthAsync(Context());
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
        await Assert.That(result.Exception is InvalidOperationException).IsTrue();
        await Assert.That(result.Exception!.Message).Contains("provider returned null");
    }

    [Test]
    public async Task CooperativeTimeoutRetainsNodeDiagnostics()
    {
        var client = DispatchProxy.Create<IProbeClient, ProbeClientProxy>();
        var timeout = new TimeoutException("node timed out");
        ((ProbeClientProxy)client).Probe = async (options, token) =>
        {
            await Task.Delay(options.Timeout, token);
            return [new(new("slow.example"), true, null, timeout), new(new("fast.example"), true, TimeSpan.Zero)];
        };
        var result = await new RespireHealthCheck(client, new() { ProbeTimeout = TimeSpan.FromMilliseconds(30) })
            .CheckHealthAsync(Context());
        await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
        await Assert.That(((RespireNodeHealth[])result.Data["nodes"]).Length).IsEqualTo(2);
        await Assert.That((int)result.Data["failedNodes"]).IsEqualTo(1);
        await Assert.That(((AggregateException)result.Exception!).InnerExceptions[0]).IsSameReferenceAs(timeout);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CustomContractReportsEndpointsFailuresAndReceivesOptions(bool allNodes, bool failure)
    {
        var client = DispatchProxy.Create<IProbeClient, ProbeClientProxy>();
        var proxy = (ProbeClientProxy)client;
        var error = new InvalidOperationException("custom probe failure");
        var endpoint = new RespireEndpoint("data.example", 6380);
        RespireHealthProbeOptions? seen = null;
        CancellationToken seenToken = default;
        proxy.Probe = (options, token) =>
        {
            seen = options;
            seenToken = token;
            RespireHealthProbeResult first = failure ? new(endpoint, false, null, error)
                : new(endpoint, true, TimeSpan.FromMilliseconds(5));
            return ValueTask.FromResult<RespireHealthProbeResult[]>(options.ProbeAllNodes
                ? [first, new(new("replica.example"), true, TimeSpan.Zero)] : [first]);
        };
        var result = await new RespireHealthCheck(client, new()
        {
            ProbeAllNodes = allNodes, MaxConcurrentProbes = 3, ProbeTimeout = TimeSpan.FromSeconds(4),
        }).CheckHealthAsync(Context());
        await Assert.That(result.Status).IsEqualTo(failure ? HealthStatus.Unhealthy : HealthStatus.Healthy);
        await Assert.That(proxy.PingCalls).IsEqualTo(0);
        await Assert.That(seen!.ProbeAllNodes).IsEqualTo(allNodes);
        await Assert.That(seen.MaxConcurrentProbes).IsEqualTo(3);
        await Assert.That(seen.Timeout).IsEqualTo(TimeSpan.FromSeconds(4));
        await Assert.That(seenToken.CanBeCanceled).IsTrue();
        var nodes = (RespireNodeHealth[])result.Data["nodes"];
        await Assert.That(nodes.Length).IsEqualTo(allNodes ? 2 : 1);
        await Assert.That(nodes[0].Endpoint).IsEqualTo(endpoint);
        await Assert.That((int)result.Data["failedNodes"]).IsEqualTo(failure ? 1 : 0);
        if (failure) await Assert.That(((AggregateException)result.Exception!).InnerExceptions[0]).IsSameReferenceAs(error);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task AsyncCustomWorkCannotExtendHealthCheckDeadline(bool contract, bool callerCancellation)
    {
        IRespireClient client = contract ? DispatchProxy.Create<IProbeClient, ProbeClientProxy>()
            : DispatchProxy.Create<IRespireClient, ProbeClientProxy>();
        var proxy = (ProbeClientProxy)client;
        proxy.Connected = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource<RespireHealthProbeResult[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedPing = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Probe = (_, _) => { entered.TrySetResult(); return new(blocked.Task); };
        proxy.Ping = _ => { entered.TrySetResult(); return new(blockedPing.Task); };
        using var cancel = new CancellationTokenSource();
        var check = new RespireHealthCheck(client, new()
        {
            ProbeTimeout = callerCancellation ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(30),
        });
        var pending = check.CheckHealthAsync(Context(), cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (callerCancellation)
            {
                cancel.Cancel();
                await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
            }
            else
            {
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.That(result.Status).IsEqualTo(HealthStatus.Unhealthy);
                await Assert.That(result.Exception is OperationCanceledException).IsTrue();
            }
        }
        finally
        {
            // A caller cannot forcibly stop a non-cooperative provider; allow its work to finish.
            blocked.TrySetResult([]);
            blockedPing.TrySetResult(TimeSpan.Zero);
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task ConcreteViewsReturnOwnedObservationsWithoutNewConnections(int viewKind)
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions();
        var factory = options.TestingStreamFactory!;
        var connections = 0;
        await using var client = await RespireClient.ConnectAsync(options with
        {
            Connections = 1,
            ReplicaEndpoints = viewKind == 2 ? [new("unused-replica")] : [],
            TestingStreamFactory = (host, port, token) =>
            {
                Interlocked.Increment(ref connections);
                return factory(host, port, token);
            },
        });
        IRespireClient view = viewKind switch
        {
            1 => client.WithKeyPrefix("tenant:"),
            2 => client.WithReadFrom(RespireReadFrom.Replica),
            _ => client,
        };
        var probe = (IRespireHealthProbe)view;
        var first = await probe.ProbeHealthAsync();
        await Assert.That(first.Length).IsEqualTo(1);
        await Assert.That(first[0].Error).IsNull();
        first[0] = new(new("caller-owned.example"), false, null, new InvalidOperationException());
        var next = await probe.ProbeHealthAsync(new() { ProbeAllNodes = true });
        await Assert.That(next.Length).IsEqualTo(viewKind == 2 ? 2 : 1);
        await Assert.That(next[0].Endpoint).IsEqualTo(client.Endpoint);
        await Assert.That(next[0].Error).IsNull();
        await Assert.That(connections).IsEqualTo(1);
        await Assert.That(await client.PingAsync()).IsGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnavailableOrRetiredConnectionsAreReportedWithoutReopening(bool retired)
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions();
        var factory = options.TestingStreamFactory!;
        var connections = 0;
        await using var client = RespireClient.Create(options with
        {
            Connections = 1,
            TestingStreamFactory = (host, port, token) =>
            {
                Interlocked.Increment(ref connections);
                return factory(host, port, token);
            },
        });
        if (retired)
        {
            await client.PingAsync();
            await client.Core.Multiplexer.GetExistingHealthConnection()!.RetireAsync();
        }
        var results = await client.ProbeHealthAsync(new() { ProbeAllNodes = true });
        await Assert.That(results.Length).IsEqualTo(1);
        await Assert.That(results[0].IsConnected).IsFalse();
        await Assert.That(results[0].Error is RespireConnectionException).IsTrue();
        await Assert.That(connections).IsEqualTo(retired ? 1 : 0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CapturedClusterConnectionsStayPinnedWhileWaitingForAdmission(bool retireSecond)
    {
        await using var first = new FakeRespServer(FakeRespServer.PongReply);
        await using var second = new FakeRespServer(FakeRespServer.PongReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n"
            + $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        foreach (var server in new[] { first, second })
            server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : FakeRespServer.PongReply;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = true,
            Endpoints = [new("127.0.0.1", first.Port)],
        });
        await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null);
        var captured = client.CaptureHealthConnections(true);
        var beforeConnections = first.ReceivedConnectionIds.Concat(second.ReceivedConnectionIds).ToArray();
        var blockedServer = captured[0].Endpoint.Port == first.Port ? first : second;
        var waitingServer = captured[1].Endpoint.Port == first.Port ? first : second;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        blockedServer.SuppressReply = command =>
        {
            if (command != "PING") return false;
            started.TrySetResult();
            return true;
        };
        var waitingCommands = waitingServer.CommandsSeen;
        var pending = client.ProbeHealthAsync(new()
        {
            ProbeAllNodes = true, MaxConcurrentProbes = 1,
            Timeout = retireSecond ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100),
        }).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(waitingServer.CommandsSeen).IsEqualTo(waitingCommands);
        if (retireSecond)
        {
            await captured[1].Connection!.RetireAsync();
            await blockedServer.SendRawAsync(FakeRespServer.PongReply);
        }
        var results = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        if (!retireSecond) await blockedServer.SendRawAsync(FakeRespServer.PongReply);
        await Assert.That(results.Select(result => result.Endpoint)).IsEquivalentTo(captured.Select(target => target.Endpoint));
        await Assert.That(results[1].IsConnected).IsTrue();
        await Assert.That(results[1].Error).IsNotNull();
        await Assert.That(results.Count(result => result.Error is null)).IsEqualTo(retireSecond ? 1 : 0);
        await Assert.That(waitingServer.CommandsSeen).IsEqualTo(waitingCommands);
        await Assert.That(first.ReceivedConnectionIds.Concat(second.ReceivedConnectionIds).Except(beforeConnections).Count()).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidContractSettingsAndResultsAreRejected()
    {
        new RespireHealthProbeOptions().Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new RespireHealthProbeOptions { Timeout = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RespireHealthProbeOptions { MaxConcurrentProbes = 0 }.Validate());
        await using var server = new RespireFakeServer();
        await using var client = RespireClient.Create(server.CreateOptions());
        await Assert.That(async () => await client.ProbeHealthAsync(new() { MaxConcurrentProbes = 0 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.ProbeHealthAsync(new() { Timeout = TimeSpan.Zero })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.ProbeHealthAsync(new() { Timeout = TimeSpan.MaxValue })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.ProbeHealthAsync(cancellationToken: new(true))).Throws<OperationCanceledException>();
        await Assert.That(() => new RespireHealthProbeResult(new("node"), false, TimeSpan.Zero)).Throws<ArgumentException>();
        await Assert.That(() => new RespireHealthProbeResult(new("node"), true, null)).Throws<ArgumentException>();
        await Assert.That(() => new RespireHealthProbeResult(new("node"), true, TimeSpan.Zero, new Exception())).Throws<ArgumentException>();
        await Assert.That(() => new RespireHealthProbeResult(new("node"), true, TimeSpan.FromTicks(-1))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(client.IsConnected).IsFalse();
    }
}
