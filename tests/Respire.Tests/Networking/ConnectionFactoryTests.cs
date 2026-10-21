using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using Respire.Networking;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ConnectionFactoryTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConnectedLoggingFailureDisposesFactoryStream(bool disposalFails)
    {
        using var stream = new DisposalStream(disposalFails);
        var expected = new InvalidOperationException("Connected logging failed.");
        var options = new RespireOptions { ConnectionFactory = (_, _) => ValueTask.FromResult<Stream>(stream) };

        var error = await Assert.That(async () => await RespireConnection.ConnectAsync("custom", 6379,
            options.ToConnectionOptions(), new ConnectedFailureLogger(expected))).Throws<InvalidOperationException>();

        await Assert.That(error).IsSameReferenceAs(expected);
        await Assert.That(stream.DisposedAsync).IsTrue();
        await Assert.That(stream.CanRead).IsFalse();
    }

    [Test]
    public async Task WriteThroughFactoryStreamSendsSetupAndCommandsWithoutFlushing()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        var options = new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, Protocol = RespireProtocol.Resp2,
            ClientName = "write-through",
            ConnectionFactory = async (endpoint, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(endpoint.Host, endpoint.Port, token);
                    return new WriteThroughStream(socket);
                }
                catch { socket.Dispose(); throw; }
            },
        };
        using var cancellation = new CancellationTokenSource(Limit);
#pragma warning disable RESP010 // Test-owned client exercises write-through factory streams without a flush.
        await using var client = await RespireClient.ConnectAsync(options, cancellation.Token);
#pragma warning restore RESP010
        await client.PingAsync(cancellation.Token);
        await Assert.That(server.ReceivedCommands).Contains("CLIENT SETNAME write-through");
        await Assert.That(server.ReceivedCommands).Contains("PING");
    }

    private sealed class WriteThroughStream(Socket socket) : NetworkStream(socket, ownsSocket: true)
    {
        public override void Flush() => throw new InvalidOperationException("Write-through streams need no flush.");
        public override Task FlushAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("Write-through streams need no flush.");
    }

    private sealed class DisposalStream(bool disposalFails) : MemoryStream
    {
        internal bool DisposedAsync { get; private set; }

        public override async ValueTask DisposeAsync()
        {
            DisposedAsync = true;
            await base.DisposeAsync();
            if (disposalFails) throw new IOException("Stream cleanup failed.");
        }
    }

    private sealed class ConnectedFailureLogger(Exception error) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "ConnectionConnected") throw error;
        }
    }

    [Test]
    [NotInParallel]
    public async Task FactoryConnectionsPublishLiveConnectionMetrics()
    {
        using var configuration = new MetricConfigurationScope();
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
#pragma warning disable RESP010 // Test-owned client makes live connection metric registration and disposal observable.
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, Protocol = RespireProtocol.Resp2,
            ConnectionFactory = async (endpoint, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(endpoint.Host, endpoint.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        });
#pragma warning restore RESP010
        long connections = 0;
        var poolPrefix = $"127.0.0.1:{server.Port}/";
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, capture) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.connection.count")
                capture.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "db.client.connection.pool.name" && tag.Value is string name
                    && (name.StartsWith(poolPrefix, StringComparison.Ordinal) || name.StartsWith("overflow/", StringComparison.Ordinal)))
                    connections += value;
        });
        listener.Start();
        listener.RecordObservableInstruments();
        await Assert.That(connections).IsEqualTo(1L);
        await client.DisposeAsync();
        connections = 0;
        listener.RecordObservableInstruments();
        await Assert.That(connections).IsEqualTo(0L);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PublicFactoryHandlesCommandsAndPubSub(int protocol)
    {
        await using var server = new RespireFakeServer();
        var options = server.CreateOptions() with { Connections = 1, Protocol = (RespireProtocol)protocol };
        var factory = options.ConnectionFactory!;
        var opened = 0;
        using var cancellation = new CancellationTokenSource(Limit);
#pragma warning disable RESP010 // Test-owned client isolates factory acquisition for command and subscription sockets.
        await using var client = await RespireClient.ConnectAsync(options with
        {
            ConnectionFactory = (endpoint, token) =>
            {
                if (endpoint != options.Endpoints[0] || !token.CanBeCanceled)
                    throw new InvalidOperationException("Factory lost the destination or deadline.");
                Interlocked.Increment(ref opened);
                return factory(endpoint, token);
            },
        }, cancellation.Token);
#pragma warning restore RESP010
        await client.SetAsync("key", "value", cancellationToken: cancellation.Token);
        await Assert.That(await client.GetStringAsync("key", cancellation.Token)).IsEqualTo("value");
        await using var subscription = await client.SubscribeAsync("factory-channel", cancellationToken: cancellation.Token);
        await client.PublishAsync("factory-channel", "message", cancellation.Token);
        await using var reader = subscription.GetAsyncEnumerator(cancellation.Token);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(opened >= 2).IsTrue();
    }

    [Test]
    [Arguments("null")]
    [Arguments("read-only")]
    [Arguments("closed")]
    public async Task InvalidFactoryStreamIsRejectedAndDisposed(string kind)
    {
        var stream = kind == "null" ? null : new MemoryStream([], writable: false);
        if (kind == "closed") stream!.Dispose();
        var options = new RespireOptions { ConnectionFactory = (_, _) => ValueTask.FromResult<Stream>(stream!) };
        await Assert.That(async () => await RespireConnection.ConnectAsync("custom", 6379, options.ToConnectionOptions()))
            .Throws<InvalidOperationException>();
        if (stream is not null) await Assert.That(stream.CanRead).IsFalse();
    }

    [Test]
    public async Task PreCanceledAttemptDoesNotInvokeFactory()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        var options = new RespireOptions
        {
            ConnectionFactory = (_, _) => { calls++; throw new IOException("Must not run."); },
        };
        var error = await Assert.That(async () => await RespireConnection.ConnectAsync("custom", 6379,
            options.ToConnectionOptions(), cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TlsUsesConnectDeadlineAndDisposesFactoryStream(bool callerCancellation)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancellation = new CancellationTokenSource(Limit);
        NetworkStream? stream = null;
        var options = new RespireOptions
        {
            UseTls = true,
            ConnectTimeout = callerCancellation ? Limit : TimeSpan.FromMilliseconds(500),
            ConnectionFactory = async (endpoint, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token);
                    return stream = new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        };
        var connecting = RespireConnection.ConnectAsync("redis.internal", 6379,
            options.ToConnectionOptions(), cancellationToken: cancellation.Token);
        using var peer = await listener.AcceptSocketAsync(cancellation.Token);
        // A TLS ClientHello proves factory acquisition finished before we cancel or expire TLS.
        await peer.ReceiveAsync(new byte[4096], SocketFlags.None, cancellation.Token);
        if (callerCancellation)
        {
            cancellation.Cancel();
            var error = await Assert.That(async () => await connecting.WaitAsync(Limit)).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        else
        {
            var error = await Assert.That(async () => await connecting.WaitAsync(Limit)).Throws<RespireTimeoutException>();
            await Assert.That(error!.CommandName).IsEqualTo("CONNECT");
            await Assert.That(error.Diagnostics.Endpoint).IsEqualTo(new RespireEndpoint("redis.internal", 6379));
        }
        await Assert.That(stream!.CanRead).IsFalse();
    }

    [Test]
    public async Task CertificateRejectionDisposesFactoryStream()
    {
        using var certificate = TestTlsCertificate.Create();
        using var cancellation = new CancellationTokenSource(Limit);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string? targetHost = null;
        var serving = ServeAsync();
        NetworkStream? stream = null;
        var options = new RespireOptions
        {
            UseTls = true, TlsOptions = new() { RemoteCertificateValidationCallback = (_, _, _, _) => false },
            ConnectionFactory = async (_, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token);
                    return stream = new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        };
        await Assert.That(async () => await RespireConnection.ConnectAsync("redis.internal", 6379,
            options.ToConnectionOptions(), cancellationToken: cancellation.Token)).Throws<AuthenticationException>();
        await Assert.That(stream!.CanRead).IsFalse();
        await serving.WaitAsync(Limit);
        await Assert.That(targetHost).IsEqualTo("redis.internal");

        async Task ServeAsync()
        {
            using var socket = await listener.AcceptSocketAsync(cancellation.Token);
            await using var tls = new SslStream(new NetworkStream(socket, ownsSocket: false));
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, host) => { targetHost = host; return certificate; },
                }, cancellation.Token);
                await Assert.That(await tls.ReadAsync(new byte[1], cancellation.Token)).IsEqualTo(0);
            }
            catch (IOException) { }
            catch (AuthenticationException) { }
        }
    }
}
