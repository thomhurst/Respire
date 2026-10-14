using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

[NotInParallel]
public class PhysicalExceptionEndpointTests
{
    private static readonly RespireEndpoint Origin = new("origin.example", 6380);
    private static readonly byte[] ErrorReply = "-WRONGTYPE invalid value\r\n"u8.ToArray();

    [Test]
    [Arguments("raw")]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("converted")]
    [Arguments("stream")]
    public async Task NativeServerErrorsCarryOriginWithoutTelemetry(string shape)
    {
        var (source, consume) = Rent(shape);
        source.ErrorEndpoint = Origin;
        source.TrySetResult(RespValue.Error("MOVED 1 destination:6379"u8.ToArray()));
        source.ReleaseRef();
        var error = await Assert.ThrowsAsync<RespireServerException>(consume);
        await Assert.That(error!.Endpoint).IsEqualTo((RespireEndpoint?)Origin);
        await Assert.That(error.CommandName).IsEqualTo("GET");
        await Assert.That(error.Message).IsEqualTo("MOVED 1 destination:6379");
    }

    [Test]
    [Arguments("converted")]
    [Arguments("stream")]
    public async Task NativeProtocolErrorsCarryOrigin(string shape)
    {
        var (source, consume) = Rent(shape);
        source.ErrorEndpoint = Origin;
        source.TrySetResult(RespValue.Array(System.Array.Empty<RespValue>()));
        source.ReleaseRef();
        var error = await Assert.ThrowsAsync<RespireProtocolException>(consume);
        await Assert.That(error!.Endpoint).IsEqualTo((RespireEndpoint?)Origin);
    }

    [Test]
    public async Task RawPoolReuseDoesNotRetainPreviousEndpoint()
    {
        var pool = new PendingResponsePool(1);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var source = pool.Rent(throwOnError: true, commandName: "GET");
            await Assert.That(source.ErrorEndpoint).IsNull();
            RespireEndpoint? expected = attempt == 1 ? null : new("node", 6380 + attempt);
            source.ErrorEndpoint = expected;
            var task = source.Task;
            source.TrySetResult(RespValue.Error("ERR failed"u8.ToArray()));
            source.ReleaseRef();
            var error = await Assert.ThrowsAsync<RespireServerException>(async () => { using var value = await task; });
            await Assert.That(error!.Endpoint).IsEqualTo(expected);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PooledConversionCarriesEndpointForSyncAndAsyncReplies(bool asynchronous)
    {
        var owner = DispatchResponseSource<long>.Start();
        owner.Observation.UseEndpoint(Origin);
        var completion = new TaskCompletionSource<RespValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new RespireProtocolException("bad conversion");
        Exception? actual = null;
        try
        {
            var converted = PooledResponseSource<Exception, long>.Create(
                asynchronous ? new(completion.Task) : new(RespValue.Integer(1)), expected,
                static (Exception failure, in RespValue _) => throw failure);
            var result = owner.Attach(converted);
            if (asynchronous) completion.SetResult(RespValue.Integer(1));
            await result;
        }
        catch (Exception error) { actual = error; }
        // Synchronous conversion throws before Attach; its caller owns Fail in that case.
        if (!asynchronous) owner.Fail(actual!);
        await Assert.That(actual).IsSameReferenceAs(expected);
        await Assert.That(expected.Endpoint).IsEqualTo((RespireEndpoint?)Origin);
    }

    [Test]
    public async Task LaterRetrySelectionCannotReplaceAnEarlierErrorOrigin()
    {
        var owner = DispatchResponseSource<long>.Start();
        var observation = owner.Observation;
        observation.UseEndpoint(Origin);
        var error = new RespireServerException("MOVED 1 destination:6379", "GET");
        observation.Handled(error);
        observation.UseEndpoint(new("destination", 6379));
        owner.Fail(error);
        await Assert.That(error.Endpoint).IsEqualTo((RespireEndpoint?)Origin);
        var next = DispatchResponseSource<long>.Start();
        var unrelated = new RespireProtocolException("no server selected");
        next.Fail(unrelated);
        await Assert.That(unrelated.Endpoint).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BatchAndExecElementErrorsCarryPhysicalEndpoint(bool transaction)
    {
        await using var server = transaction
            ? new FakeRespServer(FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n-WRONGTYPE invalid value\r\n"u8.ToArray())
            : new FakeRespServer(ErrorReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        RespirePending<string?> pending;
        if (transaction)
        {
            await using var tx = client.CreateTransaction();
            pending = tx.GetString("key");
            await tx.CommitAsync();
        }
        else
        {
            await using var batch = client.CreateBatch();
            pending = batch.GetString("key");
            await batch.TryExecuteAsync();
        }
        var error = Assert.Throws<RespireServerException>(() => _ = pending.Result);
        await Assert.That(error.Endpoint).IsEqualTo((RespireEndpoint?)new RespireEndpoint("127.0.0.1", server.Port));
        await Assert.That(error.CommandName).IsEqualTo("GET");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BatchAndExecConversionErrorsCarryPhysicalEndpoint(bool transaction)
    {
        await using var server = transaction
            ? new FakeRespServer(FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n*0\r\n"u8.ToArray())
            : new FakeRespServer("*0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        RespirePending<double> pending;
        if (transaction)
        {
            await using var tx = client.CreateTransaction();
            pending = tx.Strings.Increment("key", 1.0);
            await tx.CommitAsync();
        }
        else
        {
            await using var batch = client.CreateBatch();
            pending = batch.Strings.Increment("key", 1.0);
            await batch.TryExecuteAsync();
        }
        var error = Assert.Throws<RespireProtocolException>(() => _ = pending.Result);
        await Assert.That(error.Endpoint).IsEqualTo((RespireEndpoint?)new RespireEndpoint("127.0.0.1", server.Port));
    }

    [Test]
    public async Task RetainedQueueErrorCarriesOriginAndStillDrainsExec()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply,
            "-ERR rejected queue command\r\n"u8.ToArray(),
            "-EXECABORT Transaction discarded because of previous errors.\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var tx = client.CreateTransaction();
        var pending = tx.Strings.Increment("key", 1.0);
        var error = await Assert.ThrowsAsync<RespireServerException>(async () => await tx.CommitAsync());
        await Assert.That(error!.Endpoint).IsEqualTo((RespireEndpoint?)new RespireEndpoint("127.0.0.1", server.Port));
        await Assert.That(error.Code).IsEqualTo("ERR");
        await Assert.That(pending.Error).IsSameReferenceAs(error);
        await Assert.That(server.ReceivedCommands.Last()).IsEqualTo("EXEC");
    }

    [Test]
    public async Task ReceiveFailureAndPreAdmissionCopyPreserveEndpointAndUnsentMarker()
    {
        await using var server = new FakeRespServer("?invalid\r\n"u8.ToArray());
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var first = await Assert.ThrowsAsync<RespireProtocolException>(async () =>
        { using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)); });
        var next = await Assert.ThrowsAsync<RespireProtocolException>(async () =>
        { using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)); });
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        await Assert.That(first!.Endpoint).IsEqualTo((RespireEndpoint?)endpoint);
        await Assert.That(next!.Endpoint).IsEqualTo((RespireEndpoint?)endpoint);
        await Assert.That(first.IsCommandNotSubmitted).IsFalse();
        await Assert.That(next.IsCommandNotSubmitted).IsTrue();
    }

    [Test]
    public async Task HandshakeFailureAndItsServerCauseCarryEndpoint()
    {
        await using var server = new FakeRespServer("-ERR invalid password\r\n"u8.ToArray());
        var error = await Assert.ThrowsAsync<RespireConnectionException>(async () =>
        {
            await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
                new RespireConnectionOptions { Password = "wrong" });
        });
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        await Assert.That(error!.Endpoint).IsEqualTo((RespireEndpoint?)endpoint);
        await Assert.That(error.InnerException).IsTypeOf<RespireServerException>();
        await Assert.That(((RespireServerException)error.InnerException!).Endpoint).IsEqualTo((RespireEndpoint?)endpoint);
    }

    [Test]
    public async Task CredentialAcquisitionCarriesEndpointWithoutExposingProviderSecrets()
    {
        var error = await Assert.ThrowsAsync<RespireAuthenticationException>(async () =>
        {
            await using var connection = await RespireConnection.ConnectAsync(Origin.Host, Origin.Port,
                new RespireConnectionOptions { CredentialProvider = new FailingCredentials() });
        });
        await Assert.That(error!.Endpoint).IsEqualTo((RespireEndpoint?)Origin);
        await Assert.That(error.InnerException).IsNull();
        await Assert.That(error.Message.Contains("secret-provider-token")).IsFalse();
    }

    private sealed class FailingCredentials : IRespireCredentialProvider
    {
        public ValueTask<RespireCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<RespireCredentials>(new IOException("secret-provider-token"));
    }

    [Test]
    public async Task StreamReadFailureRetainsEndpointAfterGetCompleted()
    {
        await using var server = new FakeRespServer("$3\r\nabc!!"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = await client.Strings.GetStreamAsync("key", deadline.Token);
        await Assert.That(stream).IsNotNull();
        var error = await Assert.ThrowsAsync<RespireProtocolException>(async () =>
            await stream!.CopyToAsync(Stream.Null, deadline.Token));
        await Assert.That(error!.Endpoint).IsEqualTo((RespireEndpoint?)new RespireEndpoint("127.0.0.1", server.Port));
    }

    [Test]
    [Arguments("raw")]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("converted")]
    [Arguments("stream")]
    public async Task NativeTransportFailurePreservesTypeCauseAndEndpoint(string shape)
    {
        var (source, consume) = Rent(shape);
        source.ErrorEndpoint = Origin;
        var cause = new IOException("connection closed");
        var expected = new RespireConnectionException("receive failed", cause);
        source.TrySetException(expected);
        source.ReleaseRef();
        var error = await Assert.ThrowsAsync<RespireConnectionException>(consume);
        await Assert.That(error).IsSameReferenceAs(expected);
        await Assert.That(error!.InnerException).IsSameReferenceAs(cause);
        await Assert.That(error.Endpoint).IsEqualTo((RespireEndpoint?)Origin);
    }

    private static (PendingResponse Source, Func<Task> Consume) Rent(string shape)
    {
        switch (shape)
        {
            case "raw":
                var raw = new PendingResponsePool(1).Rent(throwOnError: true, commandName: "GET");
                return (raw, async () => { using var reply = await raw.Task; });
            case "string":
                var text = StringPendingResponseSource.Rent("GET", observeErrors: false);
                return (text, async () => { _ = await text.Task; });
            case "bytes":
                var bytes = BytesPendingResponseSource.Rent("GET", observeErrors: false);
                return (bytes, async () => { _ = await bytes.Task; });
            case "stream":
                var stream = new BulkStreamPendingResponseSource("GET", false, null);
                return (stream, async () => { using var result = await stream.Task; });
            default:
                var converted = ConvertedPendingResponseSource<int, long>.Rent(0,
                    static (int _, in RespValue value) => (long)ResponseReader.Double(in value), false, "GET", observeErrors: false);
                return (converted, async () => { _ = await converted.Task; });
        }
    }
}
