using System.Net;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Idempotency.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class IdempotencyMiddlewareTests(RedisTestContainer redis)
{
    [Test]
    public async Task CancellationAfterRedisAcceptsBeginAbandonsClaimBeforeHandlerRuns()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        using var aborted = new CancellationTokenSource();
        var (wrapped, scripts) = WrapScripts(client);
        scripts.AfterBegin = aborted.Cancel;
        var executions = 0;
        using var server = CreateServer(wrapped, context =>
        {
            Interlocked.Increment(ref executions);
            return context.Response.WriteAsync("recovered");
        }, before: app => app.Use(async (context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Abort-Begin")) context.RequestAborted = aborted.Token;
            await next(context);
        }));
        using var http = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders");
        request.Headers.Add("Idempotency-Key", "operation");
        request.Headers.Add("X-Caller", "alice");
        request.Headers.Add("X-Abort-Begin", "true");
        await Assert.That(async () => { using var response = await http.SendAsync(request); })
            .Throws<OperationCanceledException>();
        await Assert.That(executions).IsEqualTo(0);
        using var retry = await SendAsync(http);
        using var replay = await SendAsync(http);
        await Assert.That(await retry.Content.ReadAsStringAsync()).IsEqualTo("recovered");
        await Assert.That(await replay.Content.ReadAsStringAsync()).IsEqualTo("recovered");
        await Assert.That(executions).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompletionFailureStillDeliversBufferedResponse(bool accepted)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var (wrapped, scripts) = WrapScripts(client);
        scripts.FailComplete = true;
        scripts.AcceptCompleteBeforeFailure = accepted;
        var executions = 0;
        using var server = CreateServer(wrapped, context =>
        {
            Interlocked.Increment(ref executions);
            context.Response.ContentLength = 8;
            return context.Response.WriteAsync("complete");
        }, kestrel: true);
        using var http = server.CreateClient();
        using var first = await SendAsync(http);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await first.Content.ReadAsStringAsync()).IsEqualTo("complete");
        scripts.FailComplete = false;
        using var retry = await SendAsync(http);
        using var replay = await SendAsync(http);
        await Assert.That(await retry.Content.ReadAsStringAsync()).IsEqualTo("complete");
        await Assert.That(await replay.Content.ReadAsStringAsync()).IsEqualTo("complete");
        await Assert.That(executions).IsEqualTo(accepted ? 1 : 2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompressionInsideIdempotencyReplaysValidCompressedBytes(bool kestrel)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        using var server = CreateServer(client, context =>
        {
            Interlocked.Increment(ref executions);
            context.Response.ContentType = "text/plain";
            return context.Response.WriteAsync(new string('x', 1000));
        }, kestrel: kestrel, compression: true);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
        using var first = await SendAsync(http);
        using var replay = await SendAsync(http);
        foreach (var response in new[] { first, replay })
        {
            await Assert.That(response.Content.Headers.ContentEncoding.Single()).IsEqualTo("gzip");
            await using var compressed = await response.Content.ReadAsStreamAsync();
            await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            await Assert.That(await reader.ReadToEndAsync()).IsEqualTo(new string('x', 1000));
        }
        await Assert.That(executions).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HeadReplayPreservesRepresentationLength(bool kestrel)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        RequestDelegate endpoint = context =>
        {
            Interlocked.Increment(ref executions);
            context.Response.ContentLength = 1234;
            return Task.CompletedTask;
        };
        using var server = CreateServer(client, endpoint, kestrel: kestrel,
            routes: routes => routes.MapMethods("/orders", ["HEAD"], endpoint).WithRespireIdempotency("orders"));
        using var http = server.CreateClient();
        using var first = await SendAsync(http, method: HttpMethod.Head);
        using var replay = await SendAsync(http, method: HttpMethod.Head);
        await Assert.That(first.Content.Headers.ContentLength).IsEqualTo(1234);
        await Assert.That(replay.Content.Headers.ContentLength).IsEqualTo(1234);
        await Assert.That((await replay.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
        await Assert.That(executions).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SeparateServersReplayBinaryStatusAndSafeHeadersOnlyOnce(bool kestrel)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        byte[] payload = [0, 255, 128, 13, 10];
        RequestDelegate endpoint = async context =>
        {
            Interlocked.Increment(ref executions);
            context.Response.StatusCode = StatusCodes.Status201Created;
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.Location = "/orders/42";
            context.Response.Headers.ETag = "\"version-1\"";
            context.Response.Headers.SetCookie = "session=secret";
            context.Response.Headers["X-Private"] = "secret";
            await context.Response.Body.WriteAsync(payload);
        };
        using var firstServer = CreateServer(client, endpoint, kestrel: kestrel);
        // The request Host is part of the scope, so the two independently listening servers
        // receive the same logical public host, as they would behind an ingress proxy.
        using var secondServer = CreateServer(client, endpoint, kestrel: kestrel);
        using var firstHttp = firstServer.CreateClient();
        using var secondHttp = secondServer.CreateClient();
        firstHttp.DefaultRequestHeaders.Host = "orders.example";
        secondHttp.DefaultRequestHeaders.Host = "orders.example";
        using var first = await SendAsync(firstHttp);
        using var replay = await SendAsync(secondHttp);
        await Assert.That(executions).IsEqualTo(1);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(replay.StatusCode).IsEqualTo(first.StatusCode);
        await Assert.That((await replay.Content.ReadAsByteArrayAsync()).SequenceEqual(payload)).IsTrue();
        await Assert.That(replay.Content.Headers.ContentType!.MediaType).IsEqualTo("application/octet-stream");
        await Assert.That(replay.Headers.Location!.ToString()).IsEqualTo("/orders/42");
        await Assert.That(replay.Headers.ETag!.Tag).IsEqualTo("\"version-1\"");
        await Assert.That(first.Headers.Contains("Set-Cookie")).IsTrue();
        await Assert.That(replay.Headers.Contains("Set-Cookie")).IsFalse();
        await Assert.That(replay.Headers.Contains("X-Private")).IsFalse();
    }

    [Test]
    public async Task ConcurrentRequestsConflictUntilOwnerCompletes()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        using var server = CreateServer(client, async context =>
        {
            Interlocked.Increment(ref executions);
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await context.Response.WriteAsync("done");
        });
        using var http = server.CreateClient();
        var owner = SendAsync(http);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var contenders = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SendAsync(http)));
            foreach (var contender in contenders)
            {
                using (contender)
                    await Assert.That(contender.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
            }
            await Assert.That(executions).IsEqualTo(1);
        }
        finally { release.TrySetResult(); }
        using var completed = await owner;
        using var replay = await SendAsync(http);
        await Assert.That(await replay.Content.ReadAsStringAsync()).IsEqualTo("done");
        await Assert.That(executions).IsEqualTo(1);
    }

    [Test]
    public async Task FingerprintMismatchRejectsChangedBodyAndPreservesBodyForHandler()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        using var server = CreateServer(client, async context =>
        {
            Interlocked.Increment(ref executions);
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            await context.Response.WriteAsync(await reader.ReadToEndAsync());
        }, options => options.VerifyRequestBody = true);
        using var http = server.CreateClient();
        using var first = await SendAsync(http, body: "original");
        using var mismatch = await SendAsync(http, body: "changed");
        using var replay = await SendAsync(http, body: "original");
        await Assert.That(await first.Content.ReadAsStringAsync()).IsEqualTo("original");
        await Assert.That(mismatch.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(await replay.Content.ReadAsStringAsync()).IsEqualTo("original");
        await Assert.That(executions).IsEqualTo(1);
    }

    [Test]
    public async Task CallerEndpointMethodPathAndQueryHaveIndependentScopes()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        RequestDelegate endpoint = context => context.Response.WriteAsync(Interlocked.Increment(ref executions).ToString());
        using var server = CreateServer(client, endpoint, routes: endpoints =>
        {
            endpoints.MapMethods("/orders/{id?}", ["POST", "PUT"], endpoint).WithRespireIdempotency("orders");
            endpoints.MapPost("/other", endpoint).WithRespireIdempotency("other");
        }, before: app => app.UsePathBase("/tenant"));
        using var http = server.CreateClient();
        var variants = new (string Path, string Caller, HttpMethod Method)[]
        {
            ("/orders", "alice", HttpMethod.Post),
            ("/orders", "bob", HttpMethod.Post),
            ("/other", "alice", HttpMethod.Post),
            ("/orders", "alice", HttpMethod.Put),
            ("/orders/42", "alice", HttpMethod.Post),
            ("/orders?currency=GBP", "alice", HttpMethod.Post),
            ("/tenant/orders", "alice", HttpMethod.Post),
        };
        foreach (var variant in variants)
        {
            using var response = await SendAsync(http, variant.Path, caller: variant.Caller, method: variant.Method);
            using var replay = await SendAsync(http, variant.Path, caller: variant.Caller, method: variant.Method);
            await Assert.That(await replay.Content.ReadAsStringAsync()).IsEqualTo(await response.Content.ReadAsStringAsync());
        }
        await Assert.That(executions).IsEqualTo(variants.Length);
    }

    [Test]
    public async Task MissingKeyAndUnmarkedEndpointsBypassStorage()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        RequestDelegate endpoint = context => context.Response.WriteAsync(Interlocked.Increment(ref executions).ToString());
        using var server = CreateServer(client, endpoint, routes: endpoints =>
        {
            endpoints.MapPost("/orders", endpoint).WithRespireIdempotency("orders");
            endpoints.MapPost("/unmarked", endpoint);
        });
        using var http = server.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            using var missing = await SendAsync(http, key: null);
            using var unmarked = await SendAsync(http, path: "/unmarked");
            await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(unmarked.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        await Assert.That(executions).IsEqualTo(4);
    }

    [Test]
    public async Task InvalidKeysAndMissingCallerAreRejectedBeforeExecution()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        using var server = CreateServer(client, context =>
        {
            Interlocked.Increment(ref executions);
            return Task.CompletedTask;
        });
        using var http = server.CreateClient();
        using var invalid = await SendAsync(http, key: new string('k', 257));
        await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        // HttpClient drops empty header values; set this malformed request at the server boundary.
        var empty = await server.SendAsync(context =>
        {
            context.Request.Method = "POST";
            context.Request.Path = "/orders";
            context.Request.Headers["Idempotency-Key"] = "";
            context.Request.Headers["X-Caller"] = "alice";
        });
        await Assert.That(empty.Response.StatusCode).IsEqualTo(StatusCodes.Status400BadRequest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", new[] { "one", "two" });
        request.Headers.Add("X-Caller", "alice");
        using var multiple = await http.SendAsync(request);
        using var callerMissing = await SendAsync(http, caller: null);
        await Assert.That(multiple.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(callerMissing.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(executions).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RequestBodyLimitRejectsOversizeAndAllowsSameKeyRetry(bool chunked)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        using var server = CreateServer(client, context =>
        {
            Interlocked.Increment(ref executions);
            return context.Response.WriteAsync("ok");
        }, options => { options.VerifyRequestBody = true; options.MaxRequestBodyBytes = 4; });
        using var http = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = new ByteArrayContent("12345"u8.ToArray()),
        };
        request.Headers.Add("Idempotency-Key", "operation");
        request.Headers.Add("X-Caller", "alice");
        if (chunked) request.Headers.TransferEncodingChunked = true;
        using var oversized = await http.SendAsync(request);
        using var retry = await SendAsync(http, body: "1234");
        await Assert.That(oversized.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(executions).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ResponseBodyLimitAbandonsClaimWithoutLeakingPartialResponse(bool bodyWriter)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        using var server = CreateServer(client, async context =>
        {
            if (Interlocked.Increment(ref executions) == 1)
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.StatusCode = StatusCodes.Status201Created;
                    context.Response.ContentLength = 99;
                    return Task.CompletedTask;
                });
                if (bodyWriter)
                {
                    await context.Response.BodyWriter.WriteAsync("12345"u8.ToArray());
                    await context.Response.CompleteAsync();
                }
                else
                {
                    await context.Response.Body.WriteAsync("1234"u8.ToArray());
                    await context.Response.Body.WriteAsync("5"u8.ToArray());
                }
            }
            else await context.Response.WriteAsync("ok");
        }, options => options.MaxResponseBodyBytes = 4);
        using var http = server.CreateClient();
        using var oversized = await SendAsync(http);
        using var retry = await SendAsync(http);
        using var replay = await SendAsync(http);
        await Assert.That(oversized.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
        await Assert.That((await oversized.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
        await Assert.That(await retry.Content.ReadAsStringAsync()).IsEqualTo("ok");
        await Assert.That(await replay.Content.ReadAsStringAsync()).IsEqualTo("ok");
        await Assert.That(executions).IsEqualTo(2);
    }

    [Test]
    public async Task UnstoredStatusCanRetryAndConfiguredStatusIsReplayed()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        using var server = CreateServer(client, context =>
        {
            context.Response.StatusCode = Interlocked.Increment(ref executions) == 1 ? 503 : 202;
            return context.Response.WriteAsync("result");
        });
        using var http = server.CreateClient();
        using var first = await SendAsync(http);
        using var retry = await SendAsync(http);
        using var replay = await SendAsync(http);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Accepted);
        await Assert.That(executions).IsEqualTo(2);

        using var customServer = CreateServer(client, context =>
        {
            Interlocked.Increment(ref executions);
            context.Response.StatusCode = 409;
            return context.Response.WriteAsync("business conflict");
        }, options => options.StoredStatusCodes.Add(409));
        using var customHttp = customServer.CreateClient();
        using var custom = await SendAsync(customHttp, key: "custom");
        using var customReplay = await SendAsync(customHttp, key: "custom");
        await Assert.That(await customReplay.Content.ReadAsStringAsync()).IsEqualTo("business conflict");
        await Assert.That(executions).IsEqualTo(3);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExceptionOrCancellationAbandonsClaim(bool cancellation)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        using var server = CreateServer(client, context =>
        {
            if (Interlocked.Increment(ref executions) == 1)
            {
                if (cancellation) throw new OperationCanceledException("handler cancelled");
                throw new InvalidOperationException("handler failed");
            }
            return context.Response.WriteAsync("recovered");
        });
        using var http = server.CreateClient();
        Exception? failure = null;
        try { using var response = await SendAsync(http); }
        catch (Exception exception) { failure = exception; }
        await Assert.That(failure is OperationCanceledException or InvalidOperationException).IsTrue();
        using var retry = await SendAsync(http);
        using var replay = await SendAsync(http);
        await Assert.That(await retry.Content.ReadAsStringAsync()).IsEqualTo("recovered");
        await Assert.That(await replay.Content.ReadAsStringAsync()).IsEqualTo("recovered");
        await Assert.That(executions).IsEqualTo(2);
    }

    [Test]
    public async Task AbortedRequestReleasesClaimUsingAnUncancelledCleanupToken()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        using var server = CreateServer(client, async context =>
        {
            if (Interlocked.Increment(ref executions) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
            }
            await context.Response.WriteAsync("recovered");
        }, before: app => app.Use(async (context, next) =>
        {
            try { await next(context); }
            finally { finished.TrySetResult(); }
        }));
        using var http = server.CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders");
        request.Headers.Add("Idempotency-Key", "operation");
        request.Headers.Add("X-Caller", "alice");
        var interrupted = http.SendAsync(request, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        cancellation.Cancel();
        try { using var response = await interrupted; }
        catch (OperationCanceledException) { }
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        using var retry = await SendAsync(http);
        await Assert.That(await retry.Content.ReadAsStringAsync()).IsEqualTo("recovered");
        await Assert.That(executions).IsEqualTo(2);
    }

    [Test]
    public async Task BodyWriterCompletionAndOnStartingAreCapturedBeforeReplay()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        using var server = CreateServer(client, async context =>
        {
            Interlocked.Increment(ref executions);
            context.Response.OnStarting(() =>
            {
                context.Response.StatusCode = 201;
                context.Response.ContentType = "application/octet-stream";
                context.Response.Headers.Location = "/created";
                return Task.CompletedTask;
            });
            var buffer = context.Response.BodyWriter.GetMemory(3);
            new byte[] { 0, 255, 42 }.CopyTo(buffer);
            context.Response.BodyWriter.Advance(3);
            await context.Response.CompleteAsync();
        });
        using var http = server.CreateClient();
        using var first = await SendAsync(http);
        using var replay = await SendAsync(http);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Created);
        await Assert.That(replay.Headers.Location!.ToString()).IsEqualTo("/created");
        await Assert.That((await replay.Content.ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 0, 255, 42 })).IsTrue();
        await Assert.That(executions).IsEqualTo(1);
    }

    [Test]
    public async Task StreamingOptOutThrowsAndAbandonsClaim()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        using var server = CreateServer(client, context =>
        {
            if (Interlocked.Increment(ref executions) == 1)
                context.Features.Get<IHttpResponseBodyFeature>()!.DisableBuffering();
            return context.Response.WriteAsync("buffered");
        });
        using var http = server.CreateClient();
        Exception? failure = null;
        try { using var response = await SendAsync(http); }
        catch (InvalidOperationException exception) { failure = exception; }
        await Assert.That(failure).IsNotNull();
        using var retry = await SendAsync(http);
        await Assert.That(await retry.Content.ReadAsStringAsync()).IsEqualTo("buffered");
        await Assert.That(executions).IsEqualTo(2);
    }

    [Test]
    public async Task FingerprintingIncludesAlreadyReadPrefixAndRestoresRequestPosition()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        using var server = CreateServer(client, async context =>
        {
            Interlocked.Increment(ref executions);
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            await context.Response.WriteAsync(await reader.ReadToEndAsync());
        }, options => options.VerifyRequestBody = true, before: app => app.Use(async (context, next) =>
        {
            context.Request.EnableBuffering();
            var prefix = new byte[2];
            await context.Request.Body.ReadExactlyAsync(prefix);
            await next(context);
        }));
        using var http = server.CreateClient();
        using var first = await SendAsync(http, body: "AArest");
        using var changedPrefix = await SendAsync(http, body: "BBrest");
        using var replay = await SendAsync(http, body: "AArest");
        await Assert.That(await first.Content.ReadAsStringAsync()).IsEqualTo("rest");
        await Assert.That(changedPrefix.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That(await replay.Content.ReadAsStringAsync()).IsEqualTo("rest");
        await Assert.That(executions).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExpiredOwnerCannotCompleteOrAbandonReplacement(bool oldOwnerFails)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var oldEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        using var server = CreateServer(client, async context =>
        {
            var execution = Interlocked.Increment(ref executions);
            (execution == 1 ? oldEntered : newEntered).TrySetResult();
            await (execution == 1 ? oldRelease : newRelease).Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (execution == 1 && oldOwnerFails) throw new InvalidOperationException("expired handler failed");
            await context.Response.WriteAsync(execution == 1 ? "old" : "replacement");
        }, options => options.KeyPrefix = "expiry:");
        using var http = server.CreateClient();
        var oldRequest = SendAsync(http);
        try
        {
            await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var keys = new List<RespireKey>();
            await foreach (var key in client.Keys.ScanAsync("expiry:*")) keys.Add(key);
            await Assert.That(keys.Count).IsEqualTo(1);
            // Expire the claim at Redis rather than relying on the duration of HTTP scheduling.
            await client.Keys.ExpireAsync(keys[0], RespireExpiry.In(TimeSpan.Zero));
            var replacement = SendAsync(http);
            try
            {
                await newEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                oldRelease.TrySetResult();
                try
                {
                    using var oldResponse = await oldRequest;
                    await Assert.That(oldOwnerFails).IsFalse();
                    await Assert.That(await oldResponse.Content.ReadAsStringAsync()).IsEqualTo("old");
                }
                catch (InvalidOperationException) when (oldOwnerFails) { }
                using var contender = await SendAsync(http);
                await Assert.That(contender.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
                newRelease.TrySetResult();
                using var completed = await replacement;
                using var replay = await SendAsync(http);
                await Assert.That(await completed.Content.ReadAsStringAsync()).IsEqualTo("replacement");
                await Assert.That(await replay.Content.ReadAsStringAsync()).IsEqualTo("replacement");
                await Assert.That(executions).IsEqualTo(2);
            }
            finally { newRelease.TrySetResult(); }
        }
        finally { oldRelease.TrySetResult(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EmptyNoContentResponseIsReplayed(bool kestrel)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        using var server = CreateServer(client, context =>
        {
            Interlocked.Increment(ref executions);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }, kestrel: kestrel);
        using var http = server.CreateClient();
        using var first = await SendAsync(http);
        using var replay = await SendAsync(http);
        await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That((await replay.Content.ReadAsByteArrayAsync()).Length).IsEqualTo(0);
        await Assert.That(executions).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OversizedSafeHeadersAbandonClaimAndAllowRetry(bool combinedHeaders)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        using var server = CreateServer(client, context =>
        {
            if (Interlocked.Increment(ref executions) == 1)
            {
                // Exercise both the individual-value limit and aggregate header envelope limit.
                context.Response.Headers.ContentLanguage = new string('a', combinedHeaders ? 9000 : 17 * 1024);
                if (combinedHeaders) context.Response.Headers.ETag = "\"" + new string('b', 9000) + "\"";
                return context.Response.WriteAsync("not stored");
            }
            context.Response.Headers.ContentLanguage = "en";
            return context.Response.WriteAsync("stored");
        });
        using var http = server.CreateClient();
        using var oversized = await SendAsync(http);
        using var retry = await SendAsync(http);
        using var replay = await SendAsync(http);
        await Assert.That(oversized.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await oversized.Content.ReadAsStringAsync()).IsEqualTo("not stored");
        await Assert.That(await retry.Content.ReadAsStringAsync()).IsEqualTo("stored");
        await Assert.That(await replay.Content.ReadAsStringAsync()).IsEqualTo("stored");
        await Assert.That(replay.Content.Headers.ContentLanguage.Single()).IsEqualTo("en");
        await Assert.That(executions).IsEqualTo(2);
    }

    [Test]
    public async Task ConnectionNominatedSafeHeadersAreExcludedFromReplay()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var executions = 0;
        using var server = CreateServer(client, context =>
        {
            Interlocked.Increment(ref executions);
            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.ETag = "\"private-hop\"";
            context.Response.Headers.ContentLanguage = "en";
            context.Response.Headers.Connection = "Keep-Alive, eTaG, CONTENT-language";
            return context.Response.WriteAsync("result");
        });
        using var http = server.CreateClient();
        using var first = await SendAsync(http);
        using var replay = await SendAsync(http);
        await Assert.That(first.Headers.Contains("ETag")).IsTrue();
        await Assert.That(first.Content.Headers.Contains("Content-Language")).IsTrue();
        await Assert.That(replay.Headers.Contains("ETag")).IsFalse();
        await Assert.That(replay.Content.Headers.Contains("Content-Language")).IsFalse();
        await Assert.That(replay.Headers.Contains("Connection")).IsFalse();
        await Assert.That(replay.Content.Headers.ContentType!.MediaType).IsEqualTo("application/octet-stream");
        await Assert.That(await replay.Content.ReadAsStringAsync()).IsEqualTo("result");
        await Assert.That(executions).IsEqualTo(1);
    }

    private static TestApplication CreateServer(IRespireClient client, RequestDelegate endpoint,
        Action<RespireIdempotencyOptions>? configure = null, Action<IEndpointRouteBuilder>? routes = null,
        Action<IApplicationBuilder>? before = null, bool kestrel = false, bool compression = false)
        => new(new HostBuilder().ConfigureWebHost(web => ConfigureTransport(web, kestrel).ConfigureServices(services =>
        {
            services.AddRouting();
            if (compression) services.AddResponseCompression();
            services.AddSingleton(client);
            services.AddRespireIdempotency(options =>
            {
                options.CallerScope = context => context.Request.Headers["X-Caller"].FirstOrDefault();
                configure?.Invoke(options);
            });
        }).Configure(app =>
        {
            before?.Invoke(app);
            app.UseRouting();
            app.UseRespireIdempotency();
            if (compression) app.UseResponseCompression();
            app.UseEndpoints(endpoints =>
            {
                if (routes is not null) routes(endpoints);
                else endpoints.MapPost("/orders", endpoint).WithRespireIdempotency("orders");
            });
        })).Start(), kestrel);

    private static (IRespireClient Client, ScriptFaults Scripts) WrapScripts(IRespireClient client)
    {
        var scripts = DispatchProxy.Create<IRespireScriptCommands, ScriptFaults>();
        var faults = (ScriptFaults)scripts;
        faults.Inner = client.Scripts;
        var wrapper = DispatchProxy.Create<IRespireClient, ClientProxy>();
        ((ClientProxy)wrapper).Inner = client;
        ((ClientProxy)wrapper).Scripts = scripts;
        return (wrapper, faults);
    }

    public class ClientProxy : DispatchProxy
    {
        public IRespireClient Inner { get; set; } = null!;
        public IRespireScriptCommands Scripts { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
            => method!.Name == "get_Scripts" ? Scripts : method.Invoke(Inner, arguments);
    }

    public class ScriptFaults : DispatchProxy
    {
        public IRespireScriptCommands Inner { get; set; } = null!;
        public Action? AfterBegin { get; set; }
        public bool FailComplete { get; set; }
        public bool AcceptCompleteBeforeFailure { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
            => method!.Name == nameof(IRespireScriptCommands.ExecuteLeaseAsync)
                ? ExecuteAsync(method, arguments!) : method.Invoke(Inner, arguments);

        private async ValueTask<RespireResult> ExecuteAsync(MethodInfo method, object?[] arguments)
        {
            var source = ((RespireScript)arguments[0]!).Source;
            var complete = source.Contains("'HDEL'", StringComparison.Ordinal);
            if (complete && FailComplete && !AcceptCompleteBeforeFailure)
                throw new IOException("Completion rejected before Redis executes it.");
            var result = await (ValueTask<RespireResult>)method.Invoke(Inner, arguments)!;
            try
            {
                if (source.Contains("'EXISTS'", StringComparison.Ordinal) && AfterBegin is { } after)
                {
                    AfterBegin = null;
                    after();
                    // Model cancellation of the wait after Redis has accepted the command.
                    ((CancellationToken)arguments[^1]!).ThrowIfCancellationRequested();
                }
                if (complete && FailComplete) throw new IOException("Completion reply lost after Redis accepts it.");
                return result;
            }
            catch { result.Dispose(); throw; }
        }
    }

    private static IWebHostBuilder ConfigureTransport(IWebHostBuilder web, bool kestrel)
        => kestrel ? web.UseKestrel().UseUrls("http://127.0.0.1:0") : web.UseTestServer();

    private sealed class TestApplication(IHost host, bool kestrel) : IDisposable
    {
        public HttpClient CreateClient() => kestrel
            ? new HttpClient { BaseAddress = new Uri(host.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) }
            : host.GetTestClient();
        public Task<HttpContext> SendAsync(Action<HttpContext> configure) => host.GetTestServer().SendAsync(configure);
        public void Dispose() => host.Dispose();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, string path = "/orders",
        string? key = "operation", string? caller = "alice", string body = "", HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        if (caller is not null) request.Headers.Add("X-Caller", caller);
        return await http.SendAsync(request);
    }
}
