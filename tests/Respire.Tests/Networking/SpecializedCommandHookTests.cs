using System.Collections.Concurrent;
using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SpecializedCommandHookTests
{
    [Test]
    [Arguments("pinned")]
    [Arguments("handle")]
    [Arguments("node")]
    [Arguments("fanout")]
    [Arguments("acl")]
    [Arguments("metadata")]
    [Arguments("commandlog")]
    [Arguments("server")]
    [Arguments("dbsize")]
    [Arguments("flush")]
    [Arguments("exists")]
    [Arguments("load")]
    [Arguments("script-flush")]
    [Arguments("scan")]
    [Arguments("modern-scan")]
    [Arguments("valkey")]
    public async Task SpecializedOwnersRejectCancelAndPublishOneFinalOutcome(string route)
    {
        await using var server = new FakeRespServer(64, FakeRespServer.OkReply);
        var hook = new RecordingHook();
        var malformed = false;
        var applicationCommand = Operation(route);
        server.ReplyOverride = (_, command) =>
        {
            if (command == "CLUSTER SLOTS")
                return Bytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*3\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n$4\r\nnode\r\n");
            if (command == "CLUSTER NODES")
                return Bulk($"node 127.0.0.1:{server.Port}@17000 myself,master - 0 0 1 connected 0-16383\n");
            if (command == "INFO server") return Bulk("run_id:run\r\n");
            if (command == "COMMAND INFO CLUSTERSCAN")
                return route == "modern-scan" ? Bytes("*1\r\n*1\r\n$11\r\nclusterscan\r\n") : Bytes("*1\r\n$-1\r\n");
            if (malformed && IsApplication(command, applicationCommand))
                return route switch
                {
                    "node" => Bytes(":1\r\n"),
                    "server" => Bytes("*2\r\n:9223372036854775807\r\n:1\r\n"),
                    "exists" or "load" or "dbsize" => Bytes("-ERR failed\r\n"),
                    _ => Bytes("*0\r\n"),
                };
            if (command == "CLIENT ID") return Bytes(":42\r\n");
            if (command.StartsWith("ECHO ", StringComparison.Ordinal)) return Bulk("echo");
            if (command == "ACL USERS") return Bytes("*1\r\n$4\r\nuser\r\n");
            if (command == "TIME") return Bytes("*2\r\n$1\r\n0\r\n$1\r\n0\r\n");
            if (command == "DBSIZE" || command.StartsWith("COMMANDLOG LEN", StringComparison.Ordinal)) return Bytes(":1\r\n");
            if (command.StartsWith("SCRIPT EXISTS", StringComparison.Ordinal)) return Bytes("*1\r\n:1\r\n");
            if (command.StartsWith("SCRIPT LOAD", StringComparison.Ordinal)) return Bulk("digest");
            if (command.StartsWith("SCAN ", StringComparison.Ordinal) || command.StartsWith("CLUSTERSCAN ", StringComparison.Ordinal))
                return Bytes("*2\r\n$1\r\n0\r\n*0\r\n");
            return null;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespProtocol.Resp2,
            UseCluster = true, Connections = 1, ThreadPoolMonitoring = false,
            CommandTimeout = TimeSpan.FromSeconds(5), AllowAdmin = true, CommandHook = hook,
        });
        var pinned = route == "pinned" ? await client.Server.GetClientConnectionAsync() : null;
        hook.Reset();
        var baseline = server.CommandsSeen;
        var rejection = new InvalidOperationException("denied");
        hook.Before = _ => throw rejection;
        var error = await Failure(() => Execute(CancellationToken.None));
        await Assert.That(error).IsSameReferenceAs(rejection);
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Check(RespireCommandOutcome.Rejected, error);

        hook.Reset();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        error = await Failure(() => Execute(canceled.Token));
        await Assert.That(error is OperationCanceledException).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Check(RespireCommandOutcome.Canceled, error);

        hook.Reset();
        await Execute(CancellationToken.None);
        await Check(RespireCommandOutcome.Succeeded, null);

        hook.Reset();
        malformed = true;
        var original = server.ReplyOverride;
        if (ReturnsErrorsAsData(route))
        {
            await Execute(CancellationToken.None);
            await Check(RespireCommandOutcome.Succeeded, null);
        }
        else
        {
            error = await Failure(() => Execute(CancellationToken.None));
            await Check(RespireCommandOutcome.Failed, error);
        }

        hook.Reset();
        malformed = false;
        server.ReplyOverride = original;
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.SuppressReply = command =>
        {
            if (!IsApplication(command, applicationCommand)) return false;
            received.TrySetResult();
            return true;
        };
        using var pendingCancellation = new CancellationTokenSource();
        var pending = Execute(pendingCancellation.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pendingCancellation.Cancel();
        if (ReturnsErrorsAsData(route))
        {
            await pending;
            await Check(RespireCommandOutcome.Succeeded, null);
        }
        else
        {
            error = await Failure(async () => await pending);
            await Assert.That(error is OperationCanceledException).IsTrue();
            await Check(RespireCommandOutcome.Canceled, error);
        }

        async Task Check(RespireCommandOutcome outcome, Exception? expected)
        {
            await Assert.That(hook.Entered.Count).IsEqualTo(1);
            var result = hook.Completed.Single();
            await Assert.That(result.Outcome).IsEqualTo(outcome);
            await Assert.That(result.Error).IsSameReferenceAs(expected);
            await Assert.That(result.Context).IsSameReferenceAs(hook.Entered.Single());
            await Assert.That(result.Context.Command).IsEqualTo(route == "modern-scan" ? "SCAN" : applicationCommand);
        }

        async Task Execute(CancellationToken token)
        {
            switch (route)
            {
                case "pinned": await pinned!.EchoAsync("echo"u8.ToArray(), token); break;
                case "handle": await client.Server.GetClientConnectionAsync(token); break;
                case "node": await client.Server.OnNode(new("127.0.0.1", server.Port)).AclUsersAsync(token); break;
                case "fanout": await CheckRows(await client.Server.CommandLogLengthOnAllNodesAsync(RespireCommandLogType.Slow, token)); break;
                case "acl": await CheckRows(await client.Server.AclSetUserOnAllNodesAsync("user", ["on"], token)); break;
                case "metadata": await CheckRows(await client.Server.ConfigResetStatisticsOnAllNodesAsync(token)); break;
                case "commandlog": await CheckRows(await client.Server.ResetCommandLogOnAllNodesAsync(RespireCommandLogType.Slow, token)); break;
                case "server": await client.Server.TimeAsync(token); break;
                case "dbsize": await client.Server.DatabaseSizeAsync(token); break;
                case "flush": await client.Server.FlushDatabaseAsync(ServerFlushMode.Default, token); break;
                case "exists": await client.Scripts.ExistsAsync("digest", token); break;
                case "load": await client.Scripts.LoadAsync(RespireScript.Create("return 1"), token); break;
                case "script-flush": await client.Scripts.FlushAsync(cancellationToken: token); break;
                case "scan": case "modern-scan": await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Start, cancellationToken: token); break;
                case "valkey": await client.Keys.ScanValkeyClusterPageAsync(cancellationToken: token); break;
            }
        }

        async Task CheckRows<T>(RespireServerResult<T>[] rows)
        {
            await Assert.That(rows.Length).IsEqualTo(1);
            await Assert.That(rows[0].IsSuccess).IsEqualTo(!malformed && server.SuppressReply is null);
        }
    }

    [Test]
    [Arguments("commandlog-get", "COMMANDLOG GET")]
    [Arguments("commandlog-length", "COMMANDLOG LEN")]
    [Arguments("commandlog-reset", "COMMANDLOG RESET")]
    [Arguments("diagnostic", "SLOWLOG LEN")]
    [Arguments("diagnostic-fanout", "SLOWLOG LEN")]
    [Arguments("hotkeys-start", "HOTKEYS START")]
    [Arguments("hotkeys-stop", "HOTKEYS STOP")]
    [Arguments("hotkeys-reset", "HOTKEYS RESET")]
    [Arguments("acl", "ACL LIST")]
    [Arguments("metadata", "MODULE LIST")]
    [Arguments("cluster", "CLUSTER MYID")]
    public async Task ServerPreCanceledCallsAdmitBeforeCancellation(string route, string operation)
    {
        await using var server = new FakeRespServer(64, FakeRespServer.OkReply);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        foreach (var enabled in new[] { false, true })
        {
            var hook = new RecordingHook();
            await using var client = await RespireClient.ConnectAsync(new RespireOptions
            {
                Endpoints = [new("127.0.0.1", server.Port)], Protocol = RespProtocol.Resp2,
                Connections = 1, ThreadPoolMonitoring = false, AllowAdmin = true,
                CommandHook = enabled ? hook : null,
            });
            hook.Reset();
            var baseline = server.CommandsSeen;
            var error = await Failure(Execute);
            await Assert.That(error is OperationCanceledException).IsTrue();
            await Assert.That(((OperationCanceledException)error).CancellationToken).IsEqualTo(canceled.Token);
            await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
            await Assert.That(hook.Entered.Count).IsEqualTo(enabled ? 1 : 0);
            await Assert.That(hook.Completed.Count).IsEqualTo(enabled ? 1 : 0);
            if (enabled)
            {
                var result = hook.Completed.Single();
                await Assert.That(result.Context.Command).IsEqualTo(operation);
                await Assert.That(result.Context).IsSameReferenceAs(hook.Entered.Single());
                await Assert.That(result.Outcome).IsEqualTo(RespireCommandOutcome.Canceled);
                await Assert.That(result.Error).IsSameReferenceAs(error);

                hook.Reset();
                var rejection = new InvalidOperationException("denied before cancellation");
                hook.Before = _ => throw rejection;
                error = await Failure(Execute);
                await Assert.That(error).IsSameReferenceAs(rejection);
                await Assert.That(hook.Entered.Count).IsEqualTo(1);
                await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Rejected);
                await Assert.That(hook.Completed.Single().Error).IsSameReferenceAs(rejection);
                await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
            }

            async Task Execute()
            {
                switch (route)
                {
                    case "commandlog-get": await client.Server.CommandLogAsync(RespireCommandLogType.Slow, cancellationToken: canceled.Token); break;
                    case "commandlog-length": await client.Server.CommandLogLengthAsync(RespireCommandLogType.Slow, canceled.Token); break;
                    case "commandlog-reset": await client.Server.ResetCommandLogAsync(RespireCommandLogType.Slow, canceled.Token); break;
                    case "diagnostic": await client.Server.SlowLogLengthAsync(canceled.Token); break;
                    case "diagnostic-fanout": await client.Server.SlowLogLengthOnAllNodesAsync(canceled.Token); break;
                    case "hotkeys-start": await client.Server.StartHotKeysOnAllNodesAsync(new RespireHotKeysOptions(), canceled.Token); break;
                    case "hotkeys-stop": await client.Server.StopHotKeysOnAllNodesAsync(canceled.Token); break;
                    case "hotkeys-reset": await client.Server.ResetHotKeysOnAllNodesAsync(canceled.Token); break;
                    case "acl": await client.Server.AclListAsync(canceled.Token); break;
                    case "metadata": await client.Server.ModuleListAsync(canceled.Token); break;
                    case "cluster": await client.Server.ClusterMyIdAsync(canceled.Token); break;
                    default: throw new ArgumentOutOfRangeException(nameof(route));
                }
            }
        }
    }

    private static bool ReturnsErrorsAsData(string route) => route is "fanout" or "acl" or "metadata" or "commandlog";
    private static bool IsApplication(string command, string operation)
        => command == operation || command.StartsWith(operation + " ", StringComparison.Ordinal);
    private static string Operation(string route) => route switch
    {
        "pinned" => "ECHO", "handle" => "CLIENT ID", "node" => "ACL USERS",
        "fanout" => "COMMANDLOG LEN", "acl" => "ACL SETUSER", "metadata" => "CONFIG RESETSTAT",
        "commandlog" => "COMMANDLOG RESET", "server" => "TIME", "dbsize" => "DBSIZE", "flush" => "FLUSHDB",
        "exists" => "SCRIPT EXISTS", "load" => "SCRIPT LOAD", "script-flush" => "SCRIPT FLUSH",
        "scan" => "SCAN", "modern-scan" or "valkey" => "CLUSTERSCAN", _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static byte[] Bulk(string text) => Bytes($"${Encoding.UTF8.GetByteCount(text)}\r\n{text}\r\n");
    private static async Task<Exception> Failure(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected failure.");
    }

    internal sealed class RecordingHook : RespireCommandHook
    {
        internal Action<RespireCommandContext>? Before;
        internal readonly ConcurrentQueue<RespireCommandContext> Entered = new();
        internal readonly ConcurrentQueue<(RespireCommandContext Context, RespireCommandOutcome Outcome, Exception? Error)> Completed = new();
        public override object? OnExecuting(RespireCommandContext context)
        {
            Entered.Enqueue(context);
            Before?.Invoke(context);
            return context;
        }
        public override void OnCompleted(RespireCommandContext context, object? state, RespireCommandOutcome outcome, Exception? error)
        {
            Completed.Enqueue((context, outcome, error));
            throw new InvalidOperationException("Observer must not affect the result.");
        }
        internal void Reset() { Before = null; Entered.Clear(); Completed.Clear(); }
    }
}
