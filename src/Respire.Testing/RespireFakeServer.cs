using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using Respire.Protocol;

namespace Respire.Testing;

/// <summary>An in-memory RESP server for the documented strings, keys, collections, pub/sub, and transactions subset, using the real Respire client transport.</summary>
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
            // The client can close after its server loop has completed. Keep this managed
            // source valid for that late callback; it owns no timer or wait handle.
            var lifetime = new CancellationTokenSource();
            var client = new DuplexPipeStream(responses.Reader, requests.Writer, lifetime.Cancel);
            var server = new DuplexPipeStream(requests.Reader, responses.Writer);
            var connection = new Connection(++_nextConnectionId, server, lifetime);
            _connections.Add(connection);
            connection.Completion = Task.Run(() => ServeAsync(connection));
            return ValueTask.FromResult<Stream>(client);
        }
    }

    private async Task ServeAsync(Connection connection)
    {
        var sender = SendRepliesAsync(connection);
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
                var read = await connection.Stream.ReadAsync(buffer.AsMemory(length), connection.Lifetime.Token).ConfigureAwait(false);
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
                    byte[][] arguments;
                    try
                    {
                        arguments = ReadArguments(in request);
                    }
                    catch
                    {
                        connection.Failed = true;
                        throw;
                    }
                    finally { request.Dispose(); }
                    var reply = await ExecuteWithFaultAsync(connection, arguments).ConfigureAwait(false);
                    if (reply is null) return; // Disposal or an injected disconnect ends this connection.
                    await reply.Flushed.Task.WaitAsync(connection.Lifetime.Token).ConfigureAwait(false);
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
            lock (_gate) StopConnectionLocked(connection);
            try
            {
                await sender.ConfigureAwait(false);
                await connection.Stopping.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lock (_gate) _failures.Add(error);
            }
            try { connection.Stream.Dispose(); }
            catch (Exception error) { lock (_gate) _failures.Add(error); }
            lock (_gate)
            {
                ClearTransaction(connection);
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

    private Outbound? ExecuteLocked(Connection connection, byte[][] arguments, RespireFakeFaultScope? scope,
        out ListMoveWaiters? listMoveChanged, bool allowListMoveWait = false)
    {
        listMoveChanged = null;
        // Keep synchronous state protection outside the async receive state machine,
        // including exceptional command execution and clock callbacks.
        lock (_gate)
        {
            if (_disposed || connection.Closed) return null;
            connection.ExecutingReply = true;
            try
            {
                scope?.ObserveExecution();
                // EXEC and all its commands share one server-time sample.
                _commandTime = _clock.GetUtcNow().ToUnixTimeMilliseconds();
                var result = Execute(connection, arguments);
                if (allowListMoveWait && ReferenceEquals(result, FakeReply.NullArray))
                {
                    if (!_listMoveWaiters.TryGetValue(arguments[1], out listMoveChanged))
                    {
                        listMoveChanged = new ListMoveWaiters();
                        _listMoveWaiters.Add(arguments[1], listMoveChanged);
                    }
                    listMoveChanged.Count++;
                    return null;
                }
                var reply = result.Encode(connection.Resp3);
                // Enqueue before releasing state ownership: newly subscribed routes cannot
                // receive a publication ahead of their acknowledgement, even behind a fault gate.
                return QueueOutputLocked(connection, reply, push: false);
            }
            catch
            {
                connection.Failed = true;
                throw;
            }
            finally
            {
                connection.ExecutingReply = false;
                FlushDeferredPushesLocked(connection);
            }
        }
    }

    private FakeReply Execute(Connection connection, byte[][] args)
    {
        var command = Token(args[0]);
        try
        {
            if (!Commands.TryGetValue(command, out var handler))
                return RejectCommand(connection, command, FakeReply.Error($"ERR Respire.Testing does not support command or arguments: {command}"));
            // Redis command-table arity is either exact or a minimum. Optional-argument
            // upper bounds (PING, LPOP/RPOP) are checked by the handler, including in EXEC.
            if (args.Length < handler.MinimumArity
                || handler.MaximumArity == handler.MinimumArity && args.Length > handler.MaximumArity)
                return RejectCommand(connection, command, WrongArity(command));
            if (connection.IsResp2Subscribed
                && command is not ("SUBSCRIBE" or "UNSUBSCRIBE" or "PING"))
                return FakeReply.Error($"ERR Can't execute '{command.ToLowerInvariant()}': only SUBSCRIBE / UNSUBSCRIBE / PING are supported in this context");
            // Redis resolves CLIENT subcommands and their arity before queueing.
            if (command == "CLIENT")
            {
                var subcommand = Token(args[1]);
                var arity = subcommand switch { "ID" or "GETNAME" => 2, "SETNAME" => 3, _ => 0 };
                if (arity == 0)
                    return RejectCommand(connection, command, FakeReply.Error($"ERR Respire.Testing does not support CLIENT {subcommand}"));
                if (args.Length != arity) return RejectCommand(connection, command, WrongArity($"CLIENT|{subcommand}"));
            }
            if (connection.Transaction is not null && command is not ("MULTI" or "EXEC" or "DISCARD" or "WATCH"))
                return QueueTransaction(connection, args);
            if (args.Length > handler.MaximumArity) return WrongArity(command);
            return handler.Execute(this, connection, args);
        }
        catch (WrongTypeException) { return FakeReply.Error("WRONGTYPE Operation against a key holding the wrong kind of value"); }
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
        if (entry.ExpiresAt is { } expires && expires <= Now) { DeleteEntry(key); return null; }
        return entry;
    }

    private bool Remove(byte[] key) => Find(key) is not null && DeleteEntry(key);

    private void SetEntry(byte[] key, Entry entry)
    {
        _entries[key] = entry;
        TouchWatchedKey(key);
    }

    private bool DeleteEntry(byte[] key)
    {
        if (!_entries.Remove(key)) return false;
        TouchWatchedKey(key);
        return true;
    }

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
            try
            {
                connection.Lifetime.Cancel();
                // The server loop joins its writer before disposing the shared stream.
            }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        // Cancel I/O before releasing rules, otherwise a held reply could escape
        // while reset wakes its continuation and shutdown has not cancelled it yet.
        // Each server loop disposes its stream after I/O unwinds. Completing its
        // PipeReader here would race PipeReaderStream's final AdvanceTo call.
        ResetFaults();
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

    private sealed class Connection(long id, Stream stream, CancellationTokenSource lifetime)
    {
        internal long Id { get; } = id;
        internal Stream Stream { get; } = stream;
        internal CancellationTokenSource Lifetime { get; } = lifetime;
        internal byte[]? Name { get; set; }
        internal bool Resp3 { get; set; }
        internal bool Failed { get; set; }
        internal Task Completion { get; set; } = Task.CompletedTask;
        internal bool Closed { get; set; }
        internal Task Stopping { get; set; } = Task.CompletedTask;
        internal HashSet<byte[]> Channels { get; } = new(BinaryKeyComparer.Instance);
        internal bool IsResp2Subscribed => !Resp3 && Channels.Count != 0;
        internal Channel<Outbound> Output { get; } = Channel.CreateUnbounded<Outbound>(new()
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });
        internal int PendingPushBytes;
        internal bool ExecutingReply;
        internal List<Outbound>? DeferredPushes;
        internal List<byte[][]>? Transaction { get; set; }
        internal long QueuedBytes { get; set; }
        internal bool TransactionError { get; set; }
        internal bool WatchChanged { get; set; }
        internal HashSet<byte[]> WatchedKeys { get; } = new(BinaryKeyComparer.Instance);
    }

    private sealed class WrongTypeException : Exception { }

    private sealed class Entry(object data, long? expiresAt = null)
    {
        internal object Data { get; } = data;
        internal byte[] Value => Data as byte[] ?? throw new WrongTypeException();
        internal Dictionary<byte[], byte[]> Hash => Data as Dictionary<byte[], byte[]> ?? throw new WrongTypeException();
        internal HashSet<byte[]> Set => Data as HashSet<byte[]> ?? throw new WrongTypeException();
        internal List<byte[]> List => Data as List<byte[]> ?? throw new WrongTypeException();
        internal Dictionary<byte[], double> SortedSet => Data as Dictionary<byte[], double> ?? throw new WrongTypeException();
        internal string Type => Data switch
        {
            byte[] => "string",
            Dictionary<byte[], byte[]> => "hash",
            HashSet<byte[]> => "set",
            List<byte[]> => "list",
            Dictionary<byte[], double> => "zset",
            _ => throw new InvalidOperationException("Unknown fake entry type."),
        };
        internal long? ExpiresAt { get; set; } = expiresAt;
    }
}
