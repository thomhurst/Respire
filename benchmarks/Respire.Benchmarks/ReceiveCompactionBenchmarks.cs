using System.Text;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Respire.Commands;
using Respire.Networking;

namespace Respire.Benchmarks;

/// <summary>One GET pipeline whose second bulk reply starts in a deliberately small receive tail.</summary>
[MemoryDiagnoser]
public class ReceiveCompactionBenchmarks
{
    private const int SmallPayloadLength = 4095;
    private readonly Cmd1 _large = new(Verbs.Get, "large");
    private readonly Cmd1 _small = new(Verbs.Get, "small");
    private RespireConnection _connection = null!;
    private ReplyStream _stream = null!;
    private int _capacity;
    private int _largeLength;
    private long _pipelines;

    [Params(1, 4095, 4096)]
    public int FreeTail { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _stream = new ReplyStream();
        _connection = await RespireConnection.ConnectAsync("scripted", 6379,
            new RespireConnectionOptions
            {
                Protocol = RespProtocol.Resp2, ReceiveBufferSize = 65536,
                TestingStreamFactory = (_, _, _) => ValueTask.FromResult<Stream>(_stream),
            });
        _capacity = await _stream.InitialWindow.WaitAsync(TimeSpan.FromSeconds(10));
        var prefix = $"${SmallPayloadLength}\r\nxx";
        var budget = _capacity - FreeTail - prefix.Length;
        _largeLength = budget - 12;
        _largeLength = budget - $"${_largeLength}\r\n".Length - 2;
        var header = $"${_largeLength}\r\n";
        if (header.Length + _largeLength + 2 != budget)
            throw new InvalidOperationException("Unexpected bulk-header geometry.");
        _stream.Configure(
            Encoding.ASCII.GetBytes($"{header}{new string('x', _largeLength)}\r\n{prefix}"),
            Encoding.ASCII.GetBytes(new string('x', SmallPayloadLength - 2) + "\r\n"));
        if (await GetPipeline() != _largeLength + SmallPayloadLength)
            throw new InvalidOperationException("GET pipeline lost reply bytes.");
        ValidateReadShape();
    }

    [Benchmark(OperationsPerInvoke = 2)]
    public async Task<int> GetPipeline()
    {
        var first = _connection.SendAsync(_large);
        var second = _connection.SendAsync(_small);
        using var large = await first;
        using var small = await second;
        Interlocked.Increment(ref _pipelines);
        return large.AsSpan().Length + small.AsSpan().Length;
    }

    private void ValidateReadShape()
    {
        var window = _stream.TailWindow;
        if (window != FreeTail && window != _capacity - 2)
            throw new InvalidOperationException($"Unexpected initial tail window: {window}.");
        if (FreeTail == 4096 && window != FreeTail)
            throw new InvalidOperationException("The exact-threshold control unexpectedly compacted.");
        var readsPerPipeline = FreeTail == 1 && window == FreeTail ? 3 : 2;
        if (_stream.CompletedReads != Interlocked.Read(ref _pipelines) * readsPerPipeline)
            throw new InvalidOperationException("The scripted read count did not match the observed tail window.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_connection is null) return;
        await _connection.DisposeAsync();
        ValidateReadShape();
        Console.WriteLine($"RECEIVE_COMPACTION FreeTail={FreeTail} Capacity={_capacity} TailWindow={_stream.TailWindow} CompletedReads={_stream.CompletedReads} Pipelines={Interlocked.Read(ref _pipelines)}");
    }

    private sealed class ReplyStream : Stream
    {
        // Both GET keys have five ASCII bytes and produce a 24-byte command.
        private const int RequestPairLength = 2 * 24;
        private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly TaskCompletionSource<int> _initialWindow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ReadOnlyMemory<byte> _remaining;
        private byte[] _first = null!;
        private byte[] _last = null!;
        private int _written;
        private int _tailWindow;
        private long _completedReads;
        public Task<int> InitialWindow => _initialWindow.Task;
        public int TailWindow => Volatile.Read(ref _tailWindow);
        public long CompletedReads => Interlocked.Read(ref _completedReads);
        public void Configure(byte[] first, byte[] last) { _first = first; _last = last; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _written += buffer.Length;
            while (_written >= RequestPairLength)
            {
                _written -= RequestPairLength;
                _frames.Writer.TryWrite(_first);
                _frames.Writer.TryWrite(_last);
            }
            return ValueTask.CompletedTask;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _initialWindow.TrySetResult(buffer.Length);
            if (_remaining.IsEmpty)
            {
                byte[] frame;
                try { frame = await _frames.Reader.ReadAsync(cancellationToken); }
                catch (ChannelClosedException) { return 0; }
                if (ReferenceEquals(frame, _last)) Volatile.Write(ref _tailWindow, buffer.Length);
                _remaining = frame;
            }
            var count = Math.Min(buffer.Length, _remaining.Length);
            _remaining[..count].CopyTo(buffer);
            _remaining = _remaining[count..];
            Interlocked.Increment(ref _completedReads);
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            _frames.Writer.TryComplete();
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
