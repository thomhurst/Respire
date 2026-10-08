using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Respire.Streaming;

internal sealed partial class RespireStreamWorker<THandler, TMessage>(
    IRespireClient client, IServiceScopeFactory scopeFactory,
    ILogger<RespireStreamWorker<THandler, TMessage>> logger,
    string stream, string group, Func<RespireStreamEntry, TMessage> deserialize,
    RespireStreamWorkerOptions options) : BackgroundService
    where THandler : class, IRespireStreamHandler<TMessage>
{
    private readonly CancellationTokenSource _handlers = new();
    private readonly string _identity = options.ConsumerName ?? Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource _firstFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _consumerDrain;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var readers = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            if (options.CreateGroup)
                await client.Streams.CreateGroupAsync(stream, group, options.GroupStart,
                    cancellationToken: readers.Token).ConfigureAwait(false);
        }
        catch { readers.Dispose(); throw; }

        var consumers = new Task[options.ConsumerCount];
        for (var i = 0; i < consumers.Length; i++)
            consumers[i] = ConsumeAsync($"{_identity}-{i}", readers);
        var drain = DrainConsumersAsync(consumers, readers);
        Volatile.Write(ref _consumerDrain, drain);
        // A failed reader must reach the host even if a sibling ignores cancellation.
        // Its handler scope remains owned by that sibling until the separate drain ends.
        await Task.WhenAny(_firstFailure.Task, drain).ConfigureAwait(false);
        // The fault's asynchronous continuation can lose to an already completed drain.
        // Its published state still takes precedence over clean completion.
        if (_firstFailure.Task.IsCompleted) await _firstFailure.Task.ConfigureAwait(false);
    }

    private static async Task DrainConsumersAsync(Task[] consumers, CancellationTokenSource readers)
    {
        try { await Task.WhenAll(consumers).ConfigureAwait(false); }
        catch { /* ConsumeAsync has already published the first fault to the host. */ }
        finally { readers.Dispose(); }
    }

    private async Task ConsumeAsync(string consumer, CancellationTokenSource readers)
    {
        var readOptions = new StreamReadOptions { Count = options.BatchSize, WaitFor = options.ReadWait };
        RespireStreamId? cursor = options.ConsumerName is null ? (RespireStreamId?)null : RespireStreamId.Beginning;
        try
        {
            while (!readers.IsCancellationRequested)
            {
                var entries = await client.Streams.ReadGroupOnceAsync(stream, group, consumer,
                    cursor.HasValue ? readOptions with { WaitFor = null } : readOptions,
                    cursor, readers.Token).ConfigureAwait(false);
                if (cursor.HasValue)
                {
                    // Visit each owned pending ID once at startup, including a Nack. Moving
                    // the cursor prevents a failed entry from becoming a hot retry loop.
                    if (entries.Length == 0) { cursor = null; continue; }
                    cursor = entries[^1].Id;
                }
                foreach (var entry in entries)
                {
                    // A batch delivered during shutdown stays pending rather than starting more handlers.
                    if (readers.IsCancellationRequested) break;
                    await ProcessAsync(entry).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (readers.IsCancellationRequested) { }
        catch (Exception error)
        {
            _firstFailure.TrySetException(error);
            try { await readers.CancelAsync().ConfigureAwait(false); }
            finally { CancelHandlers(); }
            throw;
        }
    }

    private async Task ProcessAsync(RespireStreamEntry entry)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<THandler>();
        RespireStreamWorkerResult result;
        try
        {
            result = await handler.HandleAsync(deserialize(entry), _handlers.Token).ConfigureAwait(false);
            if (result is not (RespireStreamWorkerResult.Ack or RespireStreamWorkerResult.Nack))
                throw new InvalidOperationException("The stream handler returned an unknown completion result.");
        }
        catch (OperationCanceledException) when (_handlers.IsCancellationRequested) { return; }
        catch (Exception)
        {
            // Exception messages may contain payloads. Do not pass them to the logger by default.
            HandlerFailed(logger);
            return;
        }

        if (result == RespireStreamWorkerResult.Ack && !_handlers.IsCancellationRequested)
            // ConsumeAsync treats acknowledgement cancellation as expected only after
            // readers stop. StopAsync and Dispose must therefore cancel readers first.
            await client.Streams.AcknowledgeAsync(stream, group, [entry.Id], _handlers.Token).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // BackgroundService cancels readers immediately. Handlers keep a separate token while draining.
        var stopping = base.StopAsync(cancellationToken);
        using var abort = cancellationToken.UnsafeRegister(static state =>
            ((RespireStreamWorker<THandler, TMessage>)state!).CancelHandlers(), this);
        try { await stopping.ConfigureAwait(false); }
        finally
        {
            // WaitAsync's cancellation callback can finish the wait and remove our registration first.
            if (cancellationToken.IsCancellationRequested) CancelHandlers();
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        CancelHandlers();
        var running = Volatile.Read(ref _consumerDrain) ?? ExecuteTask;
        if (running is not { IsCompleted: false })
            _handlers.Dispose();
        else
            _ = running.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(), _handlers,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void CancelHandlers()
    {
        try { _handlers.Cancel(); }
        catch (ObjectDisposedException) { } // Stop/Dispose can be called after the execution task has completed.
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream handler or serializer failed; delivery remains pending.")]
    private static partial void HandlerFailed(ILogger logger);
}
