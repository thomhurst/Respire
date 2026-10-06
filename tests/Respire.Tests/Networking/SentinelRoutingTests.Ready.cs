using System.Reflection;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class SentinelRoutingTests
{
    [Test, NotInParallel]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("integer")]
    [Arguments("raw")]
    public async Task ReadySentinelUsesTheConnectionReplySource(string shape)
    {
        await using var primary = Primary();
        primary.SuppressReply = command => command.StartsWith("GET ") || command.StartsWith("INCR");
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);

        var generation = client.Core.Sentinel!.Current!;
        await Assert.That(generation.IsRetired).IsFalse();
        await Assert.That(generation.Multiplexer.IsInitialized && generation.Multiplexer.IsConnected).IsTrue();
        if (Environment.GetEnvironmentVariable("TUNIT_DISABLE_HTML_REPORTER") == "true")
            await Assert.That(RespireTelemetry.IsOperationEnabled("GET")).IsFalse();

        switch (shape)
        {
            case "string":
                await VerifySourceAsync(client.Strings.GetStringAsync("ready"), "StringPendingResponseSource",
                    "$5\r\nvalue\r\n"u8.ToArray(), static value => value == "value");
                break;
            case "bytes":
                await VerifySourceAsync(client.Strings.GetBytesAsync("ready"), "BytesPendingResponseSource",
                    "$5\r\nvalue\r\n"u8.ToArray(), static value => value is not null && value.AsSpan().SequenceEqual("value"u8));
                break;
            case "integer":
                await VerifySourceAsync(client.Strings.IncrementAsync("ready"), "ConvertedPendingResponseSource",
                    ":7\r\n"u8.ToArray(), static value => value == 7);
                break;
            case "raw":
                await VerifySourceAsync(client.SendAsync("GET", new Cmd1(Verbs.Get, "ready"), default),
                    "PendingResponseSource", "$5\r\nvalue\r\n"u8.ToArray(), static value =>
                    {
                        using (value) return value.AsSpan().SequenceEqual("value"u8);
                    });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }

        async Task VerifySourceAsync<T>(ValueTask<T> response, string expectedSource, byte[] reply, Func<T, bool> valid)
        {
            await WaitForCommandAsync(primary, shape == "integer" ? "INCR" : "GET ");
            var commands = primary.ReceivedCommands;
            var index = Enumerable.Range(0, commands.Count).Single(i =>
                commands[i].StartsWith(shape == "integer" ? "INCR" : "GET "));
            // Hold the reply so a completed ValueTask cannot hide its original source.
            try
            {
                // This control pins the BCL ValueTask source representation. If the runtime
                // changes that representation, update the control rather than silently skipping it.
                var sourceField = typeof(ValueTask<T>).GetField("_obj", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("ValueTask<T>._obj is unavailable; update the reply-source control for this runtime.");
                var source = sourceField.GetValue(response);
                // TUnit's default HTML reporter attaches a tracing listener. The dedicated
                // uninstrumented CI lane disables that reporter to verify direct sources;
                // ordinary test runs must still retain their instrumented outer wrappers.
                if (RespireTelemetry.IsOperationEnabled(shape == "integer" ? "INCR" : "GET"))
                    expectedSource = shape == "raw" ? "StateMachineBox" : "PooledResponseSource";
                await Assert.That(source?.GetType().Name.Split('`')[0]).IsEqualTo(expectedSource);
            }
            finally
            {
                await primary.SendRawAsync(reply, primary.ReceivedConnectionIds[index]);
                await Assert.That(valid(await response)).IsTrue();
            }
        }
    }

    [Test, NotInParallel]
    [Arguments("string", false)]
    [Arguments("bytes", false)]
    [Arguments("integer", false)]
    [Arguments("raw", false)]
    [Arguments("string", true)]
    [Arguments("bytes", true)]
    [Arguments("integer", true)]
    [Arguments("raw", true)]
    public async Task ReadySentinelKeepsPreAdmissionFailuresAsAsyncResults(string shape, bool cancellation)
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        using var caller = new CancellationTokenSource();
        // An uncanceled token proves cancellation classification must follow the exception,
        // not manufacture another token or require the supplied token to be canceled.
        Exception failure = cancellation ? new OperationCanceledException(caller.Token) : new InvalidOperationException("write control");
        var command = new ReadyWriteFailureCommand(failure);
        switch (shape)
        {
            case "string":
                await VerifyFailureAsync(() => client.StringOrNullAsync("GET", command, default));
                break;
            case "bytes":
                await VerifyFailureAsync(() => client.BytesOrNullAsync("GET", command, default));
                break;
            case "integer":
                await VerifyFailureAsync(() => client.IntegerAsync("INCR", command, default));
                break;
            case "raw":
                await VerifyFailureAsync(() => client.SendAsync("CONTROL", command, default));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
        await Assert.That(primary.ReceivedCommands.All(command => command == "ROLE")).IsTrue();

        async Task VerifyFailureAsync<T>(Func<ValueTask<T>> send)
        {
            // A synchronous throw escapes this helper and fails the test.
            var task = send().AsTask();
            Exception? observed = null;
            try { await task; }
            catch (Exception error) { observed = error; }
            await Assert.That(observed).IsSameReferenceAs(failure);
            await Assert.That(task.IsCanceled).IsEqualTo(cancellation);
        }
    }

    [Test, NotInParallel]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("integer")]
    [Arguments("raw")]
    public async Task ReadySentinelErrorRetiresThePrimaryWithoutReplayingAcceptedWork(string shape)
    {
        await using var oldPrimary = Primary((_, command) => command.StartsWith("GET ") || command.StartsWith("INCR ")
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var promoted = Primary((_, command) => command.StartsWith("GET ")
            ? "$8\r\npromoted\r\n"u8.ToArray() : null);
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var generation = client.Core.Sentinel!.Current!;
        Volatile.Write(ref primaryPort, promoted.Port);

        await Assert.That(async () =>
        {
            switch (shape)
            {
                case "string": await client.Strings.GetStringAsync("accepted").AsTask().WaitAsync(Limit); break;
                case "bytes": await client.Strings.GetBytesAsync("accepted").AsTask().WaitAsync(Limit); break;
                case "integer": await client.Strings.IncrementAsync("accepted").AsTask().WaitAsync(Limit); break;
                case "raw": using (await client.SendAsync("GET", new Cmd1(Verbs.Get, "accepted"), default).AsTask().WaitAsync(Limit)) { } break;
                default: throw new ArgumentOutOfRangeException(nameof(shape));
            }
        }).Throws<RespireServerException>();

        await Assert.That(generation.IsRetired).IsTrue();
        await Assert.That(await client.Strings.GetStringAsync("next").AsTask().WaitAsync(Limit)).IsEqualTo("promoted");
        await Assert.That(oldPrimary.ReceivedCommands.Count(command => command.Contains("accepted", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(promoted.ReceivedCommands.Any(command => command.Contains("accepted", StringComparison.Ordinal))).IsFalse();
        await Assert.That(client.Endpoint.Port).IsEqualTo(promoted.Port);
    }

    private readonly struct ReadyWriteFailureCommand(Exception failure) : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void Write(ref RespWriter writer) => throw failure;
    }
}
