using StackExchange.Redis;

// IConnectionMultiplexer includes synchronous wait helpers.
#pragma warning disable SER308

namespace Respire.StackExchangeCompat;

/// <summary>A limited StackExchange.Redis adapter for the official distributed cache and DataProtection integrations.</summary>
/// <remarks>Use native Respire APIs for other commands. Each selected database uses a separate native client.</remarks>
public sealed partial class RespireConnectionMultiplexer : IConnectionMultiplexer, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly RespireOptions? _options;
    private readonly RespireOptions _configuration;
    private readonly Dictionary<int, RespireClient> _clients = [];
    private readonly Dictionary<int, CompatDatabase> _databases = [];
    private readonly HashSet<Task> _operations = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationTokenSource _queuedShutdown = new();
    private readonly int _defaultDatabase;
    private readonly bool _ownsClients;
    private Task? _closeTask;

    private RespireConnectionMultiplexer(RespireOptions options)
    {
        _options = options.ValidateAndSnapshot();
        _configuration = _options;
        _defaultDatabase = _options.Database;
        _ownsClients = true;
    }

    private RespireConnectionMultiplexer(RespireClient client, bool ownsClient)
    {
        var database = client.Core.Options.Database;
        _configuration = client.Core.Options;
        _clients.Add(database, client);
        _defaultDatabase = database;
        _ownsClients = ownsClient;
    }

    /// <summary>Creates a lazy adapter using native configuration, with independently owned clients for each database.</summary>
    public static RespireConnectionMultiplexer Create(RespireOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Database);
        return new(options);
    }

    /// <summary>Wraps a native client using its actual database. Other database selections are rejected.</summary>
    /// <remarks>The caller retains ownership unless <paramref name="ownsClient"/> is true.</remarks>
    public static RespireConnectionMultiplexer Wrap(RespireClient client, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new(client, ownsClient);
    }

    /// <inheritdoc />
    public IDatabase GetDatabase(int db = -1, object? asyncState = null)
    {
        if (asyncState is not null) throw Compatibility.Unsupported("asyncState");
        if (db == -1) db = _defaultDatabase;
        ArgumentOutOfRangeException.ThrowIfNegative(db);
        lock (_gate)
        {
            ThrowIfClosed();
            if (!_clients.TryGetValue(db, out var client))
            {
                if (_options is null) throw Compatibility.Unsupported("GetDatabase for a different database on a wrapped client");
                client = RespireClient.Create(_options with { Database = db });
                _clients.Add(db, client);
            }
            if (!_databases.TryGetValue(db, out var database))
            {
                database = new CompatDatabase(this, client, db);
                _databases.Add(db, database);
            }
            return database;
        }
    }

    /// <inheritdoc />
    public string ClientName => _configuration.ClientName ?? string.Empty;
    /// <summary>Identifies native configuration without exposing credentials.</summary>
    public string Configuration => $"Respire;defaultDatabase={_defaultDatabase}";
    /// <inheritdoc />
    public override string ToString() => Configuration;
    /// <inheritdoc />
    public int TimeoutMilliseconds => _configuration.CommandTimeout is { } timeout ? checked((int)timeout.TotalMilliseconds) : Timeout.Infinite;
    /// <inheritdoc />
    public bool IsConnected { get { lock (_gate) return _closeTask is null && _clients.Values.Any(static client => client.IsConnected); } }

    internal CancellationToken QueuedShutdown => _queuedShutdown.Token;

    internal Task<T> Run<T>(Func<CancellationToken, Task<T>> operation)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            var task = operation(_shutdown.Token);
            _operations.Add(task);
            _ = RemoveWhenCompleteAsync(task);
            return task;
        }
    }

    private async Task RemoveWhenCompleteAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* The original task retains its result for its caller. */ }
        finally { lock (_gate) _operations.Remove(task); }
    }

    private void ThrowIfClosed() => ObjectDisposedException.ThrowIf(_closeTask is not null, this);

    /// <inheritdoc />
    public void Wait(Task task) => task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMilliseconds)).GetAwaiter().GetResult();
    /// <inheritdoc />
    public T Wait<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMilliseconds)).GetAwaiter().GetResult();
    /// <inheritdoc />
    public void WaitAll(params Task[] tasks) => Wait(Task.WhenAll(tasks));

    /// <inheritdoc />
    public void Close(bool allowCommandsToComplete = true) => CloseAsync(allowCommandsToComplete).GetAwaiter().GetResult();

    /// <inheritdoc />
    public Task CloseAsync(bool allowCommandsToComplete = true)
    {
        lock (_gate)
        {
            if (_closeTask is not null) return _closeTask;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _closeTask = completion.Task;
            // Callbacks only settle queued promises. Cancel under the lifetime gate so no work starts afterward.
#pragma warning disable CA1849
            _queuedShutdown.Cancel();
            if (!allowCommandsToComplete) _shutdown.Cancel();
#pragma warning restore CA1849
            _ = CloseCoreAsync(_operations.ToArray(), _clients.Values.ToArray(), completion);
            return _closeTask;
        }
    }

    private async Task CloseCoreAsync(Task[] operations, IRespireClient[] clients, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            try { await Task.WhenAll(operations).ConfigureAwait(false); }
            catch { /* Command failures remain on the command tasks. */ }
            if (_ownsClients)
            {
                await Task.WhenAll(clients.Select(static client => client.DisposeAsync().AsTask())).ConfigureAwait(false);
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            _shutdown.Dispose();
            _queuedShutdown.Dispose();
            foreach (var database in _databases.Values) database.DisposeDispatch();
        }
        if (failure is null) completion.SetResult();
        else completion.SetException(failure);
    }

    /// <inheritdoc />
    public void Dispose() => Close();
    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(CloseAsync());
}

internal static class Compatibility
{
    internal static NotSupportedException Unsupported(string member)
        => new($"{member} is not supported by Respire.StackExchangeCompat. "
            + "This adapter supports the official distributed cache and DataProtection command surface only. "
            + "Use native Respire APIs for other operations.");
}
