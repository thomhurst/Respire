using System.Reflection;
using System.Runtime.ExceptionServices;
using Respire.Json;
using Respire.TimeSeries;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class CommandHookTests
{
    [Test]
    [Arguments("array", "ARSCAN")]
    [Arguments("typed-array", "ARSCAN")]
    [Arguments("hash-fields", "HSCAN")]
    [Arguments("integer-increment", "INCREX")]
    [Arguments("float-increment", "INCREX")]
    [Arguments("lcs", "LCS")]
    [Arguments("vector", "VCARD")]
    [Arguments("vector-attributes", "VSETATTR")]
    [Arguments("stream-config", "XCFGSET")]
    [Arguments("stream-nack", "XNACK")]
    [Arguments("stream-consumer", "XGROUP CREATECONSUMER")]
    [Arguments("stream-id", "XSETID")]
    [Arguments("timeseries-follow", "TS.READ")]
    [Arguments("timeseries-follow-new", "TS.READ")]
    public async Task CustomFacetCancellationAdmitsBeforeReportingTheFinalOutcome(string route, string operation)
    {
        await using var server = Server();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        foreach (var policy in new[] { "absent", "observe", "reject" })
        {
            var rejected = new InvalidOperationException("policy denied");
            var hook = new RecordingHook { Before = policy == "reject" ? _ => throw rejected : null };
            await using var client = await RespireClient.ConnectAsync(Options(server, hook) with
            {
                CommandHook = policy == "absent" ? null : hook,
            });
            var baseline = server.CommandsSeen;
            var error = await Failure(async () =>
            {
                var token = canceled.Token;
                switch (route)
                {
                    case "array":
                        await using (var iterator = client.Arrays.ScanAsync("key", 0, 10, cancellationToken: token).GetAsyncEnumerator())
                            await iterator.MoveNextAsync();
                        break;
                    case "typed-array":
                        await using (var iterator = client.Arrays.ScanAsync<int>("key", 0, 10, cancellationToken: token).GetAsyncEnumerator())
                            await iterator.MoveNextAsync();
                        break;
                    case "hash-fields": await client.Hashes.ScanFieldsPageAsync("key", cancellationToken: token); break;
                    case "integer-increment": await client.Strings.IncrementExtendedAsync("key", cancellationToken: token); break;
                    case "float-increment": await client.Strings.IncrementExtendedAsync("key", 1.5, cancellationToken: token); break;
                    case "lcs": await client.Strings.LcsIndexAsync("key", "other", cancellationToken: token); break;
                    case "vector": await client.VectorSets.CountAsync("key", token); break;
                    case "vector-attributes": await client.VectorSets.SetAttributesAsync("key", "member", 42, token); break;
                    case "stream-config": await client.Streams.ConfigureAsync("key", new() { IdempotencyMaxSize = 10 }, token); break;
                    case "stream-nack": await client.Streams.NegativeAcknowledgeAsync("key", "group", RespireStreamNackMode.Silent, (RespireStreamId)"1-0", token); break;
                    case "stream-consumer": await client.Streams.CreateConsumerAsync("key", "group", "consumer", token); break;
                    case "stream-id": await client.Streams.SetLastIdAsync("key", "1-0", cancellationToken: token); break;
                    case "timeseries-follow":
                    case "timeseries-follow-new":
                        await using (var iterator = new RespireTimeSeriesClient(client).FollowAsync("key",
                            route == "timeseries-follow" ? 0 : RespireTimeSeriesTimestamp.New,
                            cancellationToken: token).GetAsyncEnumerator())
                            await iterator.MoveNextAsync();
                        break;
                }
            });
            await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
            if (policy == "reject") await Assert.That(error).IsSameReferenceAs(rejected);
            else
            {
                await Assert.That(error is OperationCanceledException).IsTrue();
                await Assert.That(((OperationCanceledException)error).CancellationToken).IsEqualTo(canceled.Token);
            }
            await Assert.That(hook.Entered.Count).IsEqualTo(policy == "absent" ? 0 : 1);
            await Assert.That(hook.Completed.Count).IsEqualTo(policy == "absent" ? 0 : 1);
            if (policy == "absent") continue;
            var completed = hook.Completed.Single();
            await Assert.That(completed.Context.Command).IsEqualTo(operation);
            await Assert.That(completed.Error).IsSameReferenceAs(error);
            await Assert.That(completed.Outcome).IsEqualTo(policy == "reject"
                ? RespireCommandOutcome.Rejected : RespireCommandOutcome.Canceled);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ArrayScanCancellationBetweenEntriesDoesNotReadmitTheCompletedPage(bool typed, bool configured)
    {
        await using var server = new FakeRespServer("*2\r\n*2\r\n:0\r\n$1\r\n1\r\n*2\r\n:1\r\n$1\r\n2\r\n"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with { CommandHook = configured ? hook : null });
        if (typed) await Check(client.Arrays.ScanAsync<int>("key", 0, 10, cancellationToken: cancellation.Token));
        else await Check(client.Arrays.ScanAsync("key", 0, 10, cancellationToken: cancellation.Token));

        async Task Check<T>(IAsyncEnumerable<T> sequence)
        {
            await using var iterator = sequence.GetAsyncEnumerator();
            await Assert.That(await iterator.MoveNextAsync()).IsTrue();
            var baseline = server.CommandsSeen;
            cancellation.Cancel();
            var error = await Failure(async () => await iterator.MoveNextAsync());
            await Assert.That(error is OperationCanceledException).IsTrue();
            await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
            await Assert.That(hook.Entered.Count).IsEqualTo(configured ? 1 : 0);
            await Assert.That(hook.Completed.Count).IsEqualTo(configured ? 1 : 0);
            if (configured) await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task TimeSeriesFollowCancellationAfterACompletedReadDoesNotReadmit(bool configured, bool betweenSamples)
    {
        await using var server = new FakeRespServer(4, "*2\r\n*2\r\n:1\r\n$1\r\n1\r\n*2\r\n:2\r\n$1\r\n2\r\n"u8.ToArray());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hook = new RecordingHook();
        await using var client = await RespireClient.ConnectAsync(Options(server, hook) with { CommandHook = configured ? hook : null });
        await using var iterator = new RespireTimeSeriesClient(client).FollowAsync("key", 0,
            cancellationToken: cancellation.Token).GetAsyncEnumerator();
        await Assert.That(await iterator.MoveNextAsync()).IsTrue();
        if (!betweenSamples) await Assert.That(await iterator.MoveNextAsync()).IsTrue();
        var baseline = server.CommandsSeen;
        cancellation.Cancel();
        var error = await Failure(async () => await iterator.MoveNextAsync());
        await Assert.That(error is OperationCanceledException).IsTrue();
        await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
        await Assert.That(hook.Entered.Count).IsEqualTo(configured ? 1 : 0);
        await Assert.That(hook.Completed.Count).IsEqualTo(configured ? 1 : 0);
        if (configured) await Assert.That(hook.Completed.Single().Outcome).IsEqualTo(RespireCommandOutcome.Succeeded);
    }

    [Test]
    [Arguments("facet", "GET")]
    [Arguments("raw", "GET")]
    [Arguments("json", "JSON.GET")]
    [Arguments("array", "ARSCAN")]
    public async Task ForwardedViewsKeepAdmissionAndOnceOnlyCompletion(string route, string operation)
    {
        await using var server = Server();
        server.ReplyOverride = (_, command) => command.StartsWith("ARSCAN ", StringComparison.Ordinal)
            ? "*0\r\n"u8.ToArray() : "$2\r\n42\r\n"u8.ToArray();
        var hook = new RecordingHook { ThrowOnCompleted = true };
        await using var client = await RespireClient.ConnectAsync(Options(server, hook));
        var view = route == "raw" ? client.WithoutClientCache() : client.WithKeyPrefix("tenant:").WithoutClientCache();
        var proxy = DispatchProxy.Create<IRespireClient, PolicyForwarder>();
        ((PolicyForwarder)proxy).Inner = view;
        var initialCommands = server.CommandsSeen;
        foreach (var target in new[] { view, proxy })
        {
            var baseline = server.CommandsSeen;
            var rejected = new InvalidOperationException("denied");
            hook.Before = _ => throw rejected;
            var error = await Failure(() => ExecuteTarget(target));
            await Assert.That(error).IsSameReferenceAs(rejected);
            await Assert.That(server.CommandsSeen).IsEqualTo(baseline);
            hook.Before = null;
            await ExecuteTarget(target);
            await Assert.That(server.CommandsSeen).IsEqualTo(baseline + 1);
        }
        await Assert.That(hook.Entered.Count).IsEqualTo(4);
        await Assert.That(hook.Completed.Count).IsEqualTo(4);
        await Assert.That(hook.Completed.Select(c => c.Outcome).SequenceEqual(new[]
        {
            RespireCommandOutcome.Rejected, RespireCommandOutcome.Succeeded,
            RespireCommandOutcome.Rejected, RespireCommandOutcome.Succeeded,
        })).IsTrue();
        await Assert.That(hook.Entered.All(c => c.Command == operation)).IsTrue();
        var wireKey = route == "raw" ? " key" : "tenant:key";
        await Assert.That(server.ReceivedCommands.Skip(initialCommands).All(c => c.Contains(wireKey, StringComparison.Ordinal))).IsTrue();

        async Task ExecuteTarget(IRespireClient target)
        {
            switch (route)
            {
                case "facet": await target.Strings.GetStringAsync("key"); break;
                case "raw": using (await target.ExecuteAsync(RespireCommands.String.GET, "key")) { } break;
                case "json": await new RespireJsonClient(target).GetJsonAsync("key"); break;
                case "array": await target.Arrays.ScanPageAsync("key", 0, 10); break;
            }
        }
    }

    public class PolicyForwarder : DispatchProxy
    {
        internal IRespireClient Inner = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            try { return method!.Invoke(Inner, arguments); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
