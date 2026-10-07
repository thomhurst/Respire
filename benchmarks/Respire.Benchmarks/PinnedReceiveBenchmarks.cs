using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Respire.Protocol;

namespace Respire.Benchmarks;

/// <summary>Public socket receive paths, with the same in-process peer on Windows and Linux.</summary>
[MemoryDiagnoser]
public class PinnedReceiveBenchmarks
{
    private const int PipelineSize = 128;
    private const int Concurrency = 50;
    private const int LargeLength = 64 * 1024;
    private readonly ValueTask<string?>[] _pipeline = new ValueTask<string?>[PipelineSize];
    private readonly ValueTask<string?>[] _concurrent = new ValueTask<string?>[Concurrency];
    private RespireClient? _single;
    private RespireClient? _stress;
    private ReplyServer? _server;

    [GlobalSetup]
    public async Task Setup()
    {
        ReportMemory("before-connect");
        _server = new ReplyServer(Concurrency + 1);
        try
        {
            _single = await ConnectAsync(1);
            _stress = await ConnectAsync(Concurrency);
            await Ping();
            if (await Get64() != 64 || await GetLarge64KiB() != LargeLength
                || await GetPipeline128() != PipelineSize * 64 || await GetConcurrent50() != Concurrency * 64)
                throw new InvalidOperationException("Receive fixture lost or changed a reply.");
            ReportMemory("connected-idle");
        }
        catch
        {
            await Cleanup();
            throw;
        }
    }

    private ValueTask<RespireClient> ConnectAsync(int connections)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", _server!.Port) },
            Protocol = RespProtocol.Resp2, Connections = connections,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
            ThreadPoolMonitoring = false,
        });

    [Benchmark] public ValueTask<TimeSpan> Ping() => _single!.PingAsync();
    [Benchmark] public async ValueTask<int> Get64() => Check(await _single!.GetStringAsync("small"), 64);
    [Benchmark] public async ValueTask<int> GetLarge64KiB() => Check(await _single!.GetStringAsync("large"), LargeLength);

    [Benchmark(OperationsPerInvoke = PipelineSize)]
    public async ValueTask<int> GetPipeline128()
    {
        // Issue every command before awaiting: this measures a real pipeline on one socket.
        for (var i = 0; i < _pipeline.Length; i++) _pipeline[i] = _single!.GetStringAsync("small");
        var length = 0;
        for (var i = 0; i < _pipeline.Length; i++) length += Check(await _pipeline[i], 64);
        return length;
    }

    [Benchmark(OperationsPerInvoke = Concurrency)]
    public async ValueTask<int> GetConcurrent50()
    {
        for (var i = 0; i < _concurrent.Length; i++) _concurrent[i] = _stress!.GetStringAsync("small");
        var length = 0;
        for (var i = 0; i < _concurrent.Length; i++) length += Check(await _concurrent[i], 64);
        return length;
    }

    private static int Check(string? value, int expected)
    {
        if (value is null || value.Length != expected || value[0] != 'x' || value[^1] != 'x')
            throw new InvalidOperationException("Unexpected GET payload.");
        return value.Length;
    }

    private static void ReportMemory(string stage)
    {
        // Whole-process retention includes the identical peer, 51 receive loops, and pools.
        // These snapshots complement per-operation allocation measurements, not a leak assertion.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var info = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        Console.WriteLine("RECEIVE_MEMORY " + JsonSerializer.Serialize(new
        {
            stage, managedBytes = GC.GetTotalMemory(false), pohBytes = info.GenerationInfo[4].SizeAfterBytes,
            pohFragmentedBytes = info.GenerationInfo[4].FragmentationAfterBytes,
            workingSetBytes = process.WorkingSet64,
        }));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_single is not null) { await _single.DisposeAsync(); _single = null; }
        if (_stress is not null) { await _stress.DisposeAsync(); _stress = null; }
        if (_server is not null) { await _server.DisposeAsync(); _server = null; }
        Array.Clear(_pipeline);
        Array.Clear(_concurrent);
        ReportMemory("after-dispose");
    }

    // No external Redis/Docker dependency: identical command parsing, sends, and peer buffers
    // are copied to the pinned baseline. Only the library's client receive policy changes.
    private sealed class ReplyServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;
        private readonly byte[] _small = Bulk(64);
        private readonly byte[] _large = Bulk(LargeLength);
        private static readonly byte[] Pong = "+PONG\r\n"u8.ToArray();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ReplyServer(int connections)
        {
            _listener.Start();
            _run = RunAsync(connections);
        }

        private static byte[] Bulk(int length) => Encoding.ASCII.GetBytes($"${length}\r\n{new string('x', length)}\r\n");

        private async Task RunAsync(int count)
        {
            var peers = new List<Task>(count);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var socket = await _listener.AcceptSocketAsync(_stop.Token);
                    socket.NoDelay = true;
                    peers.Add(ServeAsync(socket));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { await Task.WhenAll(peers); }
        }

        private async Task ServeAsync(Socket socket)
        {
            using (socket)
            {
                var buffer = new byte[64 * 1024];
                var end = 0;
                try
                {
                    while (true)
                    {
                        var read = await socket.ReceiveAsync(buffer.AsMemory(end), SocketFlags.None, _stop.Token);
                        if (read == 0) return;
                        end += read;
                        var position = 0;
                        while (RespParser.TryParseValue(buffer.AsSpan(0, end), ref position, out var command) == RespParseStatus.Done)
                        {
                            byte[] reply;
                            try { reply = Reply(in command); }
                            finally { command.Dispose(); }
                            ReadOnlyMemory<byte> remaining = reply;
                            while (!remaining.IsEmpty)
                            {
                                var sent = await socket.SendAsync(remaining, SocketFlags.None, _stop.Token);
                                if (sent == 0) throw new IOException("Peer closed during reply send.");
                                remaining = remaining[sent..];
                            }
                        }
                        buffer.AsSpan(position, end - position).CopyTo(buffer);
                        end -= position;
                        if (end == buffer.Length) throw new InvalidOperationException("Oversized fixture command.");
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { }
            }
        }

        private byte[] Reply(in RespValue command)
        {
            var parts = command.AsArray();
            if (parts.Length == 1 && parts[0].AsSpan().SequenceEqual("PING"u8)) return Pong;
            if (parts.Length == 2 && parts[0].AsSpan().SequenceEqual("GET"u8))
            {
                if (parts[1].AsSpan().SequenceEqual("small"u8)) return _small;
                if (parts[1].AsSpan().SequenceEqual("large"u8)) return _large;
            }
            throw new InvalidOperationException("Unexpected command in receive fixture.");
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try { await _run; }
            finally { _listener.Stop(); _stop.Dispose(); }
        }
    }
}
