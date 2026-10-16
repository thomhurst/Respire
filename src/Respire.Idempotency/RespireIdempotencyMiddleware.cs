using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Respire.Coordination;

namespace Respire.Idempotency;

internal sealed class RespireIdempotencyMiddleware(RequestDelegate next, RespireIdempotency store,
    IOptions<RespireIdempotencyOptions> options, ILogger<RespireIdempotencyMiddleware> logger)
{
    private readonly RespireIdempotencyOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        var endpoint = context.GetEndpoint()?.Metadata.GetMetadata<IdempotencyEndpoint>();
        if (endpoint is null || !context.Request.Headers.TryGetValue("Idempotency-Key", out var keys))
        {
            await next(context).ConfigureAwait(false);
            return;
        }
        if (keys.Count != 1 || string.IsNullOrWhiteSpace(keys[0]) || keys[0]!.Length > 256)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var caller = _options.CallerScope!(context);
        if (string.IsNullOrWhiteSpace(caller))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        string? fingerprint = null;
        if (_options.VerifyRequestBody)
        {
            fingerprint = await FingerprintAsync(context).ConfigureAwait(false);
            if (fingerprint is null)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
        }
        var key = BuildKey(context, endpoint.Name, caller, keys[0]!);
        // Keep waiting for ownership after a disconnect so we can release an accepted claim.
        using var acquisition = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var claim = await store.TryBeginAsync(key, _options.TimeToLive, fingerprint, acquisition.Token).ConfigureAwait(false);
        switch (claim.State)
        {
            case RespireIdempotencyState.InProgress:
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            case RespireIdempotencyState.FingerprintMismatch:
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                return;
            case RespireIdempotencyState.Completed:
                await StoredResponse.Decode(claim.Result, _options.MaxResponseBodyBytes).ReplayAsync(context).ConfigureAwait(false);
                return;
        }

        var original = context.Features.Get<IHttpResponseBodyFeature>()!;
        using var buffer = new BoundedResponseStream(_options.MaxResponseBodyBytes);
        var capture = new BufferedBodyFeature(buffer);
        var completed = false;
        // Registered before the handler, this runs after its OnStarting callbacks.
        // A callback must not turn an oversized response's empty 500 into a success.
        context.Response.OnStarting(() =>
        {
            if (buffer.Exceeded)
            {
                context.Response.Headers.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentLength = 0;
            }
            return Task.CompletedTask;
        });
        context.Features.Set<IHttpResponseBodyFeature>(capture);
        try
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            await next(context).ConfigureAwait(false);
            await capture.CompleteAsync().ConfigureAwait(false);
            if (buffer.Exceeded) throw new ResponseLimitException();
            context.RequestAborted.ThrowIfCancellationRequested();
            context.Features.Set(original);
            // Run OnStarting before taking the status/header snapshot (cookies are never stored).
            await original.StartAsync(context.RequestAborted).ConfigureAwait(false);
            if (_options.StoredStatusCodes.Contains(context.Response.StatusCode))
            {
                var payload = StoredResponse.Encode(context.Response, buffer);
                if (payload is not null)
                {
                    try
                    {
                        completed = await store.CompleteAsync(key, claim.OwnerToken!, payload, context.RequestAborted).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        // Headers are committed. A store failure must not truncate a successful response.
                        // Cleanup only removes a still-pending claim, including when the reply was lost.
                        IdempotencyLog.CompletionFailed(logger, error);
                    }
                }
            }
            await original.Stream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), context.RequestAborted).ConfigureAwait(false);
        }
        catch (ResponseLimitException)
        {
            if (context.Response.HasStarted) throw;
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            IdempotencyLog.ResponseLimitExceeded(logger);
        }
        finally
        {
            context.Features.Set(original);
            // Completing with an error discards unflushed pipe buffers on an exceptional path.
            // On the normal path the writer was already completed by capture.CompleteAsync().
            try
            {
                await capture.Writer.CompleteAsync(new OperationCanceledException("Response capture ended.")).ConfigureAwait(false);
            }
            finally
            {
                capture.Dispose();
                if (!completed) await AbandonAsync(key, claim.OwnerToken!).ConfigureAwait(false);
            }
        }
    }

    private async Task AbandonAsync(string key, string owner)
    {
        // Request cancellation must not prevent cleanup or replace the handler's exception.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await store.AbandonAsync(key, owner, cleanup.Token).ConfigureAwait(false); }
        catch (Exception error) { IdempotencyLog.AbandonFailed(logger, error); }
    }

    private string BuildKey(HttpContext context, string endpoint, string caller, string key)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        foreach (var part in new[] { endpoint, caller, context.Request.Method, context.Request.Host.Value ?? "",
                     context.Request.PathBase.Value ?? "", context.Request.Path.Value ?? "", context.Request.QueryString.Value ?? "", key })
            writer.Write(part);
        return _options.KeyPrefix + Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
    }

    private async Task<string?> FingerprintAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength > _options.MaxRequestBodyBytes) return null;
        request.EnableBuffering();
        var position = request.Body.Position;
        var bytes = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            request.Body.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var total = 0;
            while (true)
            {
                var count = await request.Body.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, _options.MaxRequestBodyBytes - total + 1)),
                    context.RequestAborted).ConfigureAwait(false);
                if (count == 0) return Convert.ToHexString(hash.GetHashAndReset());
                total += count;
                if (total > _options.MaxRequestBodyBytes) return null;
                hash.AppendData(bytes, 0, count);
            }
        }
        finally
        {
            request.Body.Position = position;
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private sealed class BufferedBodyFeature(Stream stream) : StreamResponseBodyFeature(stream)
    {
        public override void DisableBuffering() => throw new InvalidOperationException("Streaming responses cannot use HTTP idempotency.");
    }
}
