using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.FastClient;

/// <summary>
/// High-performance Redis client. Commands serialize straight into the connection's coalescing
/// write buffer as pre-compiled prefix + argument bulk strings; responses complete pooled
/// value-task sources in FIFO order — no delegates, queues, or per-command allocations.
/// </summary>
/// <remarks>
/// Methods returning <see cref="RespireValue"/> hand the caller a value backed by pooled
/// storage: call <see cref="RespireValue.Dispose"/> when finished with it (forgetting is safe,
/// just less efficient). Scalar-returning methods dispose internally.
/// </remarks>
public sealed class RespireClient : IAsyncDisposable, IDisposable
{
    private readonly RespireConnectionMultiplexer _multiplexer;
    private readonly ILogger? _logger;
    private volatile bool _disposed;

    public string Host => _multiplexer.Host;
    public int Port => _multiplexer.Port;
    public bool IsConnected => _multiplexer.IsConnected && !_disposed;

    private RespireClient(RespireConnectionMultiplexer multiplexer, ILogger? logger)
    {
        _multiplexer = multiplexer;
        _logger = logger;
    }

    public static async Task<RespireClient> CreateAsync(
        string host,
        int port = 6379,
        int connectionCount = 0,
        ILogger? logger = null,
        RespireConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var multiplexer = await RespireConnectionMultiplexer.CreateAsync(
            host, port, connectionCount, options, logger, cancellationToken).ConfigureAwait(false);

        logger?.LogInformation(
            "Created RespireClient for {Host}:{Port} with {ConnectionCount} connections",
            host, port, multiplexer.ConnectionCount);

        return new RespireClient(multiplexer, logger);
    }

    // String commands

    public ValueTask<RespireValue> GetAsync(string key, CancellationToken cancellationToken = default)
        => SendForValueAsync(new KeyCommand(CommandPrefixes.Get, key), cancellationToken);

    public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default)
        => SendForOkAsync(new KeyValueCommand(CommandPrefixes.Set, key, value), cancellationToken);

    public ValueTask<long> DelAsync(string key, CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new KeyCommand(CommandPrefixes.Del, key), cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
        => await SendForIntegerAsync(new KeyCommand(CommandPrefixes.Exists, key), cancellationToken).ConfigureAwait(false) > 0;

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<bool> ExpireAsync(string key, int seconds, CancellationToken cancellationToken = default)
        => await SendForIntegerAsync(new KeyIntegerCommand(CommandPrefixes.Expire, key, seconds), cancellationToken).ConfigureAwait(false) == 1;

    public ValueTask<long> IncrAsync(string key, CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new KeyCommand(CommandPrefixes.Incr, key), cancellationToken);

    public ValueTask<long> DecrAsync(string key, CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new KeyCommand(CommandPrefixes.Decr, key), cancellationToken);

    public ValueTask<long> AppendAsync(string key, string value, CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new KeyValueCommand(CommandPrefixes.Append, key, value), cancellationToken);

    public ValueTask<long> StrLenAsync(string key, CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new KeyCommand(CommandPrefixes.StrLen, key), cancellationToken);

    public ValueTask<RespireValue> TtlAsync(string key, CancellationToken cancellationToken = default)
        => SendForValueAsync(new KeyCommand(CommandPrefixes.Ttl, key), cancellationToken);

    // Hash commands

    public ValueTask<RespireValue> HGetAsync(string key, string field, CancellationToken cancellationToken = default)
        => SendForValueAsync(new KeyValueCommand(CommandPrefixes.HGet, key, field), cancellationToken);

    public ValueTask<long> HSetAsync(string key, string field, string value, CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new KeyFieldValueCommand(CommandPrefixes.HSet, key, field, value), cancellationToken);

    public ValueTask<long> HDelAsync(string key, string field, CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new KeyValueCommand(CommandPrefixes.HDel, key, field), cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<bool> HExistsAsync(string key, string field, CancellationToken cancellationToken = default)
        => await SendForIntegerAsync(new KeyValueCommand(CommandPrefixes.HExists, key, field), cancellationToken).ConfigureAwait(false) > 0;

    public ValueTask<long> HLenAsync(string key, CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new KeyCommand(CommandPrefixes.HLen, key), cancellationToken);

    // List commands

    public ValueTask<long> LPushAsync(string key, string value, CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new KeyValueCommand(CommandPrefixes.LPush, key, value), cancellationToken);

    public ValueTask<long> RPushAsync(string key, string value, CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new KeyValueCommand(CommandPrefixes.RPush, key, value), cancellationToken);

    public ValueTask<RespireValue> LPopAsync(string key, CancellationToken cancellationToken = default)
        => SendForValueAsync(new KeyCommand(CommandPrefixes.LPop, key), cancellationToken);

    public ValueTask<RespireValue> RPopAsync(string key, CancellationToken cancellationToken = default)
        => SendForValueAsync(new KeyCommand(CommandPrefixes.RPop, key), cancellationToken);

    public ValueTask<long> LLenAsync(string key, CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new KeyCommand(CommandPrefixes.LLen, key), cancellationToken);

    // Set commands

    public ValueTask<long> SAddAsync(string key, string member, CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new KeyValueCommand(CommandPrefixes.SAdd, key, member), cancellationToken);

    public ValueTask<long> SRemAsync(string key, string member, CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new KeyValueCommand(CommandPrefixes.SRem, key, member), cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<bool> SIsMemberAsync(string key, string member, CancellationToken cancellationToken = default)
        => await SendForIntegerAsync(new KeyValueCommand(CommandPrefixes.SIsMember, key, member), cancellationToken).ConfigureAwait(false) > 0;

    public ValueTask<long> SCardAsync(string key, CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new KeyCommand(CommandPrefixes.SCard, key), cancellationToken);

    // Connection commands

    public ValueTask<string> PingAsync(CancellationToken cancellationToken = default)
        => SendForStringAsync(new RawCommand(RespCommands.Ping), cancellationToken);

    public ValueTask<RespireValue> PingWithResponseAsync(CancellationToken cancellationToken = default)
        => SendForValueAsync(new RawCommand(RespCommands.Ping), cancellationToken);

    public ValueTask<string> EchoAsync(string message, CancellationToken cancellationToken = default)
        => SendForStringAsync(new SingleValueCommand(CommandPrefixes.Echo, message), cancellationToken);

    // Server commands

    public ValueTask FlushDbAsync(CancellationToken cancellationToken = default)
        => SendForOkAsync(new RawCommand(RespCommands.FlushDb), cancellationToken);

    public ValueTask FlushAllAsync(CancellationToken cancellationToken = default)
        => SendForOkAsync(new RawCommand(RespCommands.FlushAll), cancellationToken);

    public ValueTask<long> DbSizeAsync(CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new RawCommand(RespCommands.DbSize), cancellationToken);

    // Pub/sub

    /// <summary>Publishes a message and returns the number of subscribers that received it.</summary>
    public ValueTask<long> PublishAsync(string channel, string message, CancellationToken cancellationToken = default)
        => SendForIntegerAsync(new KeyValueCommand(CommandPrefixes.Publish, channel, message), cancellationToken);

    /// <summary>
    /// Opens a dedicated pub/sub connection using this client's host, port, and connection
    /// options (including authentication). The subscriber has its own lifetime — dispose it
    /// separately.
    /// </summary>
    public Task<RespireSubscriber> CreateSubscriberAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return RespireSubscriber.CreateAsync(Host, Port, _multiplexer.Options, _logger, cancellationToken);
    }

    // Transactions

    /// <summary>Starts building a MULTI/EXEC transaction. Build, execute once, discard.</summary>
    public RespireTransaction CreateTransaction()
    {
        ThrowIfDisposed();
        return new RespireTransaction(_multiplexer);
    }

    // Shared response handling

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespireValue> SendForValueAsync<TCommand>(TCommand command, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        ThrowIfDisposed();
        var response = await _multiplexer.SendAsync(in command, cancellationToken).ConfigureAwait(false);
        response.ThrowIfError();
        return response;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<long> SendForIntegerAsync<TCommand>(TCommand command, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        ThrowIfDisposed();
        var response = await _multiplexer.SendAsync(in command, cancellationToken).ConfigureAwait(false);
        response.ThrowIfError();
        var result = response.AsInteger();
        response.Dispose();
        return result;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<string> SendForStringAsync<TCommand>(TCommand command, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        ThrowIfDisposed();
        var response = await _multiplexer.SendAsync(in command, cancellationToken).ConfigureAwait(false);
        response.ThrowIfError();
        var result = response.AsString();
        response.Dispose();
        return result;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask SendForOkAsync<TCommand>(TCommand command, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        ThrowIfDisposed();
        var response = await _multiplexer.SendAsync(in command, cancellationToken).ConfigureAwait(false);
        response.ThrowIfError();
        try
        {
            if (response.Type != RespDataType.SimpleString || !response.AsSpan().SequenceEqual("OK"u8))
            {
                throw new RespireException($"Expected 'OK' but got: {response}");
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _multiplexer.DisposeAsync().ConfigureAwait(false);
        _logger?.LogInformation("RespireClient disposed");
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
