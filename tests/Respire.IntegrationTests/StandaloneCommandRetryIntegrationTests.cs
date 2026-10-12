using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.Keyed, Key = "standalone-command-retry")]
[Category(TestCategories.ProtocolIndependent)]
[NotInParallel]
public class StandaloneCommandRetryIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    [Arguments("PING", false, true)]
    [Arguments("CONFIG GET", false, true)]
    [Arguments("GET", false, true)]
    [Arguments("SETNX", false, false)]
    [Arguments("SETNX", true, true)]
    [Arguments("SET", false, false)]
    [Arguments("SET", true, true)]
    [Arguments("INCR", false, false)]
    [Arguments("INCR", true, true)]
    [Arguments("CONFIG SET", false, false)]
    [Arguments("CONFIG SET", true, true)]
    public async Task LostReplyPreservesRealRedisEffectsAndExplicitRisk(string operation, bool highRisk, bool retries)
    {
        var options = RespireOptions.Parse(fixture.ConnectionString) with
        { Protocol = RespProtocol.Resp2, ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(5) };
        await using var observer = await RespireClient.ConnectAsync(options);
        var key = "retry-effects:" + Guid.NewGuid().ToString("N");
        await observer.SetAsync(key, "0");
        if (operation == "SETNX") await observer.Keys.DeleteAsync(key);
        var fault = new LostReply(operation);
        await using var client = await RespireClient.ConnectAsync(options with
        {
            RetryPolicy = new() { InitialBackoff = TimeSpan.Zero,
                MaxCommandRetryCategory = highRisk ? RespireCommandRetryCategory.ServerAdmin : RespireCommandRetryCategory.ReadOnly },
            TestingStreamFactory = async (host, port, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, token);
                    return new DropReplyStream(new NetworkStream(socket, ownsSocket: true), fault);
                }
                catch { socket.Dispose(); throw; }
            },
        });
        fault.Armed = true;
        RespireResult result = default;
        Exception? error = null;
        try
        {
            switch (operation)
            {
                case "PING": await client.PingAsync(); break;
                case "GET": (await client.GetStringAsync(key)).Should().Be("0"); break;
                case "SET": (await client.SetAsync(key, "written")).Should().BeTrue(); break;
                case "INCR": (await client.IncrementAsync(key)).Should().Be(2); break;
                default:
                    var descriptor = RespireCommands.All.ToArray().Single(command => command.Name == operation);
                    result = await client.ExecuteAsync(descriptor, operation switch
                    {
                        "CONFIG GET" => ["timeout"], "CONFIG SET" => ["timeout", "123"],
                        _ => [key, "written"],
                    });
                    if (operation == "SETNX") result.AsInteger().Should().Be(0);
                    break;
            }
        }
        catch (Exception failure) { error = failure; }
        finally { result.Dispose(); }
        if (retries) error.Should().BeNull();
        else error.Should().BeAssignableTo<RespireConnectionException>();
        fault.Sends.Should().Be(retries ? 2 : 1);
        fault.Dropped.Should().Be(1);
        switch (operation)
        {
            case "SET" or "SETNX": (await observer.GetStringAsync(key)).Should().Be("written"); break;
            case "INCR": (await observer.GetStringAsync(key)).Should().Be(retries ? "2" : "1"); break;
            case "CONFIG SET":
                using (var configuration = await observer.ExecuteAsync(RespireCommands.Server.CONFIG_GET, ["timeout"]))
                    configuration[1].AsString().Should().Be("123");
                using (await observer.ExecuteAsync(RespireCommands.Server.CONFIG_SET, ["timeout", "0"])) { }
                break;
            default: (await observer.GetStringAsync(key)).Should().Be("0"); break;
        }
        await observer.Keys.DeleteAsync(key);
    }

    [Test]
    [Arguments("script", false, false)]
    [Arguments("script", true, true)]
    [Arguments("function", false, false)]
    [Arguments("function", true, true)]
    [Arguments("declared", false, false)]
    [Arguments("declared", true, true)]
    [Arguments("undeclared", true, false)]
    public async Task LostReplyHonorsCallerDeclaredScriptAndRawCategories(string shape, bool highRisk, bool retries)
    {
        var options = RespireOptions.Parse(fixture.ConnectionString) with
        { Protocol = RespProtocol.Resp2, ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(5) };
        await using var observer = await RespireClient.ConnectAsync(options);
        var key = "retry-declared:" + Guid.NewGuid().ToString("N");
        await observer.SetAsync(key, "0");
        var script = RespireScript.Create("return redis.call('INCR', KEYS[1])")
            .WithRetryCategory(RespireCommandRetryCategory.WriteAccumulating);
        // A cached script keeps the fault on EVALSHA, without a NOSCRIPT fallback exchange.
        await observer.Scripts.LoadAsync(script);
        var library = RespireFunctionLibrary.Create("""
            #!lua name=retry_acceptance
            redis.register_function('retry_increment', function(keys,args) return redis.call('INCR',keys[1]) end)
            """, replace: true);
        var function = library.Function("retry_increment").WithRetryCategory(RespireCommandRetryCategory.WriteAccumulating);
        if (shape == "function") await observer.Functions.LoadAsync(library);
        var fault = new LostReply(shape switch { "script" => "EVALSHA", "function" => "FCALL", _ => "INCR" });
        await using var client = await RespireClient.ConnectAsync(options with
        {
            RetryPolicy = new() { InitialBackoff = TimeSpan.Zero,
                MaxCommandRetryCategory = highRisk ? RespireCommandRetryCategory.WriteAccumulating : RespireCommandRetryCategory.ReadOnly },
            TestingStreamFactory = async (host, port, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, token);
                    return new DropReplyStream(new NetworkStream(socket, ownsSocket: true), fault);
                }
                catch { socket.Dispose(); throw; }
            },
        });
        fault.Armed = true;
        Exception? error = null;
        try
        {
            switch (shape)
            {
                case "script": (await client.Scripts.ExecuteIntegerAsync(script, [key])).Should().Be(2); break;
                case "function": (await client.Functions.ExecuteIntegerAsync(function, [key])).Should().Be(2); break;
                case "declared":
                    using (var result = await client.ExecuteAsync(
                        RespireCommand.Create("INCR").WithRetryCategory(RespireCommandRetryCategory.WriteAccumulating), [key]))
                        result.AsInteger().Should().Be(2);
                    break;
                default:
                    using (var result = await client.ExecuteAsync("INCR", [key])) result.AsInteger().Should().Be(2);
                    break;
            }
        }
        catch (Exception failure) { error = failure; }
        if (retries) error.Should().BeNull();
        else error.Should().BeAssignableTo<RespireConnectionException>();
        fault.Sends.Should().Be(retries ? 2 : 1);
        fault.Dropped.Should().Be(1);
        (await observer.GetStringAsync(key)).Should().Be(retries ? "2" : "1");
        await observer.Keys.DeleteAsync(key);
    }

    private sealed class LostReply(string operation)
    {
        private static byte[] EncodeVerb(string operation)
        {
            var verb = operation.Split(' ')[0];
            return Encoding.ASCII.GetBytes($"${verb.Length}\r\n{verb}\r\n");
        }
        internal readonly byte[] Verb = EncodeVerb(operation);
        internal volatile bool Armed;
        internal int Sends;
        internal int Dropped;
    }

    // Receive the real server reply before discarding it. This proves that the initial
    // command executed and separates reply loss from a definitely unwritten transport failure.
    private sealed class DropReplyStream(NetworkStream transport, LostReply fault) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => transport.Flush();
        public override int Read(byte[] buffer, int offset, int count) => transport.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => transport.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await transport.ReadAsync(buffer, cancellationToken);
            if (read != 0 && fault.Armed && Interlocked.CompareExchange(ref fault.Dropped, 1, 0) == 0)
                throw new IOException("Injected loss after receiving the executed command's reply.");
            return read;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (fault.Armed && buffer.Span.IndexOf(fault.Verb) >= 0) Interlocked.Increment(ref fault.Sends);
            return transport.WriteAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) transport.Dispose();
            base.Dispose(disposing);
        }
        public override ValueTask DisposeAsync() => transport.DisposeAsync();
    }
}
