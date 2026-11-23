using System.Net.Sockets;
using System.Text;
using Respire.Internal;
using Respire.Tests.Networking;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

public class CommandOrderingTests
{
    private const string Set = "HSET key field value";
    private const string Expire = "PEXPIRE key 60001";
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_expiry = TimeSpan.FromMilliseconds(60001);

    [Test]
    public async Task UnwrittenHashWritePrecedesExpiryOnReplacementConnection()
    {
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        var hash = new HashExpiryScript(server);
        PausedWriteStream? firstTransport = null;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespireProtocol.Resp2,
            LibraryName = null,
            RetryPolicy = new(),
            ConnectionFactory = async (endpoint, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

                try
                {
                    await socket.ConnectAsync(endpoint.Host, endpoint.Port, token);

                    if (firstTransport is null)
                    {
                        firstTransport = new PausedWriteStream(socket);
                        return firstTransport;
                    }

                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        });
        await using var adapter = RespireConnectionMultiplexer.Wrap(client);
        var database = adapter.GetDatabase();
        var original = client.Core.Multiplexer.GetConnection();
        var transport = firstTransport!;
        transport.PauseWrites = true;
        var occupying = client.ExecuteAsync("ECHO", ["occupy"]).AsTask();

        try
        {
            await transport.WriteStarted.Task.WaitAsync(s_timeout);
            var set = database.HashSetAsync("key", "field", "value");
            var expire = database.KeyExpireAsync("key", s_expiry);

            // The native ECHO owns the active write. HSET is queued behind it and cannot reach this socket.
            using var admissionTimeout = new CancellationTokenSource(s_timeout);

            while (original.PendingResponseCount < 2)
            {
                await Task.Delay(1, admissionTimeout.Token);
            }

            Assert.Empty(server.ReceivedCommands);
            server.CloseConnections();
            transport.FailWrite.TrySetResult();
            await Assert.ThrowsAnyAsync<RespireConnectionException>(async () => await occupying.WaitAsync(s_timeout));
            await hash.AssertOrderedExpiryAsync(database, set, expire);
        }
        finally
        {
            transport.FailWrite.TrySetResult();
            await adapter.CloseAsync(allowCommandsToComplete: false);
        }
    }

    [Test]
    public async Task AskRedirectedHashWritePrecedesExpiryOnImportingNodeWithoutRetries()
    {
        await using var source = new FakeRespServer(4, FakeRespServer.OkReply);
        await using var target = new FakeRespServer(4, FakeRespServer.OkReply);
        var hash = new HashExpiryScript(target);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*3\r\n$9\r\n127.0.0.1\r\n:{source.Port}\r\n$7\r\nprimary\r\n");
        var redirect = Encoding.ASCII.GetBytes($"-ASK {ClusterHash.GetSlot("key")} 127.0.0.1:{target.Port}\r\n");
        source.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => topology,
            Set or Expire or "PTTL key" => redirect,
            _ => null,
        };
        await using var adapter = RespireConnectionMultiplexer.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", source.Port)],
            Protocol = RespireProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            RetryPolicy = null,
        });

        try
        {
            var database = adapter.GetDatabase();
            var set = database.HashSetAsync("key", "field", "value");
            var expire = database.KeyExpireAsync("key", s_expiry);
            await hash.AssertOrderedExpiryAsync(database, set, expire);

            Assert.Equal(new[] { "ASKING", Set, "ASKING", Expire, "ASKING", "PTTL key" }, target.ReceivedCommands);
            Assert.Equal(new[] { Set, Expire, "PTTL key" }, source.ReceivedCommands.Where(IsHashCommand));
        }
        finally
        {
            await adapter.CloseAsync(allowCommandsToComplete: false);
        }
    }

    private static bool IsHashCommand(string command) => command is Set or Expire or "PTTL key";

    private sealed class HashExpiryScript
    {
        private readonly FakeRespServer _server;
        private readonly TaskCompletionSource _setReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _earlyExpiry = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _releaseSet;
        private long _ttl = -2;

        internal HashExpiryScript(FakeRespServer server)
        {
            _server = server;
            server.SuppressReply = command =>
            {
                switch (command)
                {
                    case Set:
                        Volatile.Write(ref _ttl, -1);
                        _setReceived.TrySetResult();
                        return true;
                    case Expire when !Volatile.Read(ref _releaseSet):
                        _earlyExpiry.TrySetResult();
                        return true;
                    default:
                        return false;
                }
            };
            server.ReplyOverride = (_, command) => command switch
            {
                Expire when Volatile.Read(ref _ttl) == -1 => ApplyExpiry(),
                Expire => ":0\r\n"u8.ToArray(),
                "PTTL key" => Encoding.ASCII.GetBytes($":{Volatile.Read(ref _ttl)}\r\n"),
                _ => null,
            };
        }

        internal async Task AssertOrderedExpiryAsync(IDatabase database, Task<bool> set, Task<bool> expire)
        {
            await _setReceived.Task.WaitAsync(s_timeout);
            // Keeping the write reply pending exposes a later command that leaves the adapter
            // while the write still owns a retry or redirect. No expiry reply can overtake it here.
            await Assert.ThrowsAsync<TimeoutException>(() => _earlyExpiry.Task.WaitAsync(TimeSpan.FromMilliseconds(500)));
            Volatile.Write(ref _releaseSet, true);
            var setIndex = Array.IndexOf(_server.ReceivedCommands.ToArray(), Set);
            await _server.SendRawAsync(":1\r\n"u8.ToArray(), _server.ReceivedConnectionIds[setIndex]);

            Assert.True(await set.WaitAsync(s_timeout));
            Assert.True(await expire.WaitAsync(s_timeout));
            Assert.Equal(s_expiry, await database.KeyTimeToLiveAsync("key").WaitAsync(s_timeout));
            Assert.Equal(new[] { Set, Expire, "PTTL key" }, _server.ReceivedCommands.Where(IsHashCommand));
        }

        private byte[] ApplyExpiry()
        {
            Volatile.Write(ref _ttl, (long)s_expiry.TotalMilliseconds);
            return ":1\r\n"u8.ToArray();
        }
    }

    private sealed class PausedWriteStream(Socket socket) : NetworkStream(socket, ownsSocket: true)
    {
        internal bool PauseWrites
        {
            get; set;
        }

        internal TaskCompletionSource WriteStarted
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FailWrite
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (PauseWrites)
            {
                WriteStarted.TrySetResult();
                await FailWrite.Task.WaitAsync(cancellationToken);
                throw new IOException("The original connection lost its pending write.");
            }

            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
