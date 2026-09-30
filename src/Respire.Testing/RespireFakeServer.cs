using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using Respire.Protocol;

namespace Respire.Testing;

/// <summary>An in-memory RESP server for the documented strings/keys subset, using the real Respire client transport.</summary>
/// <remarks>No TCP socket or Docker daemon is used. Each server owns independent data and connection state.
/// Unsupported commands fail explicitly. This is not a substitute for compatibility tests against Redis or Valkey.</remarks>
public sealed partial class RespireFakeServer : IAsyncDisposable
{
    private const int MaximumRequestBytes = 16 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _host = $"respire-fake-{Guid.NewGuid():N}";
    private readonly TimeProvider _clock;
    private readonly Dictionary<byte[], Entry> _entries = new(BinaryKeyComparer.Instance);
    private readonly HashSet<Connection> _connections = [];
    private readonly List<Exception> _failures = [];
    private Task? _disposeTask;
    private bool _disposed;
    private long _nextConnectionId;
    private long _commandTime;

    /// <summary>Creates an isolated server. Supply RespireFakeClock to control expiry; otherwise wall-clock UTC is used.</summary>
    public RespireFakeServer(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Returns fresh options for this server. Clone them to configure protocol, serialization, prefixing, and timeouts.</summary>
    /// <remarks>Database zero is supported. TLS, authentication, Cluster, Sentinel and client-side tracking are unsupported.
    /// Dispose clients before the server. Options cannot be converted into a connection string.</remarks>
    public RespireOptions CreateOptions()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new RespireOptions { Endpoints = [new(_host, 6379)], TestingStreamFactory = Connect };
        }
    }

    private ValueTask<Stream> Connect(string host, int port, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (host != _host || port != 6379)
                throw new NotSupportedException($"Fake server options must retain endpoint {_host}:6379.");
            var requests = new Pipe();
            var responses = new Pipe();
            var client = new DuplexPipeStream(responses.Reader, requests.Writer);
            var server = new DuplexPipeStream(requests.Reader, responses.Writer);
            var connection = new Connection(++_nextConnectionId, server);
            _connections.Add(connection);
            connection.Completion = Task.Run(() => ServeAsync(connection));
            return ValueTask.FromResult<Stream>(client);
        }
    }

    private async Task ServeAsync(Connection connection)
    {
        var buffer = new byte[4096];
        var length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (length == MaximumRequestBytes)
                    {
                        connection.Failed = true;
                        throw new IOException("Fake request exceeds 16 MiB.");
                    }
                    System.Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaximumRequestBytes));
                }
                var read = await connection.Stream.ReadAsync(buffer.AsMemory(length)).ConfigureAwait(false);
                if (read == 0) return;
                length += read;
                var consumed = 0;
                while (consumed < length)
                {
                    var status = RespParser.TryParseValue(buffer.AsSpan(0, length), ref consumed, out var request);
                    if (status == RespParseStatus.NeedMoreData) break;
                    if (status != RespParseStatus.Done)
                    {
                        connection.Failed = true;
                        throw new IOException("Invalid RESP request to fake server.");
                    }
                    byte[]? reply;
                    try
                    {
                        var arguments = ReadArguments(in request);
                        reply = ExecuteLocked(connection, arguments);
                    }
                    catch
                    {
                        connection.Failed = true;
                        throw;
                    }
                    finally { request.Dispose(); }
                    if (reply is null) return; // Server disposal won the command's state lock.
                    await connection.Stream.WriteAsync(reply).ConfigureAwait(false);
                }
                if (consumed > 0) buffer.AsSpan(consumed, length - consumed).CopyTo(buffer);
                length -= consumed;
            }
        }
        catch (Exception error) when (!connection.Failed && (error is IOException or OperationCanceledException or ObjectDisposedException))
        {
            // Peer closure/cancellation terminates this connection; the real client observes EOF.
        }
        catch (Exception error)
        {
            // Retain only the failure, not the completed connection and its request buffer.
            // Disposal joins active loops before observing this list under the same gate.
            lock (_gate) _failures.Add(error);
        }
        finally
        {
            connection.Stream.Dispose();
            lock (_gate)
            {
                _connections.Remove(connection);
            }
        }
    }

    private static byte[][] ReadArguments(in RespValue request)
    {
        if (request.Type != RespDataType.Array) throw new IOException("Expected a RESP command array.");
        var values = request.AsArray();
        if (values.Length == 0) throw new IOException("Expected a command name.");
        var arguments = new byte[values.Length][];
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index].Type != RespDataType.BulkString || values[index].IsNull)
                throw new IOException("Expected bulk-string command arguments.");
            arguments[index] = values[index].AsSpan().ToArray();
        }
        return arguments;
    }

    private byte[]? ExecuteLocked(Connection connection, byte[][] arguments)
    {
        // Keep synchronous state protection outside the async receive state machine,
        // including exceptional command execution and clock callbacks.
        lock (_gate)
        {
            if (_disposed) return null;
            return Execute(connection, arguments).Encode(connection.Resp3);
        }
    }

    private FakeReply Execute(Connection connection, byte[][] args)
    {
        var command = Token(args[0]);
        _commandTime = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        try
        {
            if (!Commands.TryGetValue(command, out var handler))
                return FakeReply.Error($"ERR Respire.Testing does not support command or arguments: {command}");
            if (args.Length < handler.MinimumArity || args.Length > handler.MaximumArity)
                return WrongArity(command);
            return handler.Execute(this, connection, args);
        }
        catch (FormatException) { return FakeReply.Error("ERR value is not an integer or out of range"); }
        catch (OverflowException) { return FakeReply.Error("ERR increment or expiry would overflow"); }
    }

    private static FakeReply Hello(Connection connection, byte[][] args)
    {
        if (args.Length != 2 || Token(args[1]) is not ("2" or "3"))
            return FakeReply.Error("ERR Respire.Testing supports HELLO 2 or HELLO 3 without authentication");
        connection.Resp3 = Token(args[1]) == "3";
        FakeReply[] fields = [FakeReply.Text("server"), FakeReply.Text("respire-fake"),
            FakeReply.Text("version"), FakeReply.Text("0.0.0"), FakeReply.Text("proto"), FakeReply.Integer(Integer(args[1])),
            FakeReply.Text("id"), FakeReply.Integer(connection.Id), FakeReply.Text("mode"), FakeReply.Text("standalone")];
        return connection.Resp3 ? FakeReply.Map(fields) : FakeReply.Array(fields);
    }

    private static FakeReply Client(Connection connection, byte[][] args)
    {
        if (args.Length == 2 && Token(args[1]) == "ID") return FakeReply.Integer(connection.Id);
        if (args.Length == 2 && Token(args[1]) == "GETNAME") return FakeReply.Bulk(connection.Name);
        if (args.Length == 3 && Token(args[1]) == "SETNAME")
        {
            if (args[2].Any(value => value <= 32 || value > 126)) return FakeReply.Error("ERR Client names cannot contain spaces or special characters");
            connection.Name = args[2].Length == 0 ? null : args[2];
            return FakeReply.Ok;
        }
        return FakeReply.Error($"ERR Respire.Testing does not support CLIENT {(args.Length > 1 ? Token(args[1]) : "<missing>")}");
    }

    private long Now => _commandTime;
    private static string Token(byte[] bytes) => Encoding.UTF8.GetString(bytes).ToUpperInvariant();
    private static FakeReply WrongArity(string command)
        => FakeReply.Error($"ERR wrong number of arguments for '{command.ToLowerInvariant()}' command");
    private static long Integer(byte[] bytes)
    {
        if (!System.Buffers.Text.Utf8Parser.TryParse(bytes, out long value, out var consumed) || consumed != bytes.Length)
            throw new FormatException();
        // Redis rejects noncanonical integer strings, including leading '+', whitespace and leading zeros.
        if (!bytes.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(value.ToString(CultureInfo.InvariantCulture)))) throw new FormatException();
        return value;
    }

    private Entry? Find(byte[] key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return null;
        if (entry.ExpiresAt is { } expires && expires <= Now) { _entries.Remove(key); return null; }
        return entry;
    }

    private bool Remove(byte[] key) => Find(key) is not null && _entries.Remove(key);

    // ReadArguments owns each byte array. Stored keys never reference client or parser buffers.
    private sealed class BinaryKeyComparer : IEqualityComparer<byte[]>
    {
        internal static readonly BinaryKeyComparer Instance = new();
        public bool Equals(byte[]? left, byte[]? right)
            => ReferenceEquals(left, right) || left is not null && right is not null && left.AsSpan().SequenceEqual(right);
        public int GetHashCode(byte[] bytes)
        {
            var hash = new HashCode();
            hash.AddBytes(bytes);
            return hash.ToHashCode();
        }
    }

    /// <summary>Closes every owned pipe connection and waits for its server loop. Concurrent disposal joins the same task.</summary>
    public ValueTask DisposeAsync()
    {
        Connection[] connections;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            connections = _connections.ToArray();
            _connections.Clear();
            _entries.Clear();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        // Completing pipes can wake client continuations. Never do that while holding
        // the server-state lock needed by connection cleanup and command execution.
        _ = DisposeConnectionsAsync(connections, completion);
        return new(completion.Task);
    }

    private async Task DisposeConnectionsAsync(Connection[] connections, TaskCompletionSource completion)
    {
        List<Exception>? errors = null;
        foreach (var connection in connections)
        {
            try { connection.Stream.Dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        try { await Task.WhenAll(connections.Select(connection => connection.Completion)).ConfigureAwait(false); }
        catch (Exception error) { (errors ??= []).Add(error); }
        lock (_gate)
        {
            if (_failures.Count != 0) (errors ??= []).AddRange(_failures);
            _failures.Clear();
        }
        if (errors is null) completion.SetResult();
        else completion.SetException(errors);
    }

    private sealed class Connection(long id, Stream stream)
    {
        internal long Id { get; } = id;
        internal Stream Stream { get; } = stream;
        internal byte[]? Name { get; set; }
        internal bool Resp3 { get; set; }
        internal bool Failed { get; set; }
        internal Task Completion { get; set; } = Task.CompletedTask;
    }

    private sealed class Entry(byte[] value, long? expiresAt = null)
    {
        internal byte[] Value { get; set; } = value;
        internal long? ExpiresAt { get; set; } = expiresAt;
    }
}
