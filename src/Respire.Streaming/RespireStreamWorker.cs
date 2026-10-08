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
    private readonly string _identity = Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var readers = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (options.CreateGroup)
            await client.Streams.CreateGroupAsync(stream, group, options.GroupStart,
                cancellationToken: readers.Token).ConfigureAwait(false);

        var consumers = new Task[options.ConsumerCount];
        for (var i = 0; i < consumers.Length; i++)
            consumers[i] = ConsumeAsync($"{_identity}-{i}", readers);
        await Task.WhenAll(consumers).ConfigureAwait(false);
    }

    private async Task ConsumeAsync(string consumer, CancellationTokenSource readers)
    {
        var readOptions = new StreamReadOptions { Count = options.BatchSize, WaitFor = options.ReadWait };
        try
        {
            while (!readers.IsCancellationRequested)
            {
                var entries = await client.Streams.ReadGroupOnceAsync(stream, group, consumer, readOptions,
                    cancellationToken: readers.Token).ConfigureAwait(false);
                foreach (var entry in entries)
                {
                    // A batch delivered during shutdown stays pending rather than starting more handlers.
                    if (readers.IsCancellationRequested) break;
                    await ProcessAsync(entry).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (readers.IsCancellationRequested) { }
        catch
        {
            await readers.CancelAsync().ConfigureAwait(false);
            CancelHandlers();
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
        catch (Exception)
        {
            // Exception messages may contain payloads. Do not pass them to the logger by default.
            HandlerFailed(logger);
            return;
        }

        if (result == RespireStreamWorkerResult.Ack && !_handlers.IsCancellationRequested)
            await client.Streams.AcknowledgeAsync(stream, group, [entry.Id], _handlers.Token).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // BackgroundService cancels readers immediately. Handlers keep a separate token while draining.
        using var abort = cancellationToken.UnsafeRegister(static state =>
            ((RespireStreamWorker<THandler, TMessage>)state!).CancelHandlers(), this);
        try { await base.StopAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            // WaitAsync's cancellation callback can finish the wait and remove our registration first.
            if (cancellationToken.IsCancellationRequested) CancelHandlers();
        }
    }

    public override void Dispose()
    {
        CancelHandlers();
        base.Dispose();
        if (ExecuteTask is not { IsCompleted: false } running)
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
