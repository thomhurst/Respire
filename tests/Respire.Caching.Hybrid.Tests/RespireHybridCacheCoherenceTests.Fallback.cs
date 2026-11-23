using System.Diagnostics.Tracing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Hybrid.Tests;

public partial class RespireHybridCacheCoherenceTests
{
    [Test]
    [NotInParallel]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task TaggedFallbackCoalescesConcurrentFactories(bool disableLocalCache, bool hasInvalidatedPayload)
    {
        string prefix = "fallback:" + NewKey() + ":";
        await using var reader = BuildProvider(true, clientPrefix: prefix, configure: options =>
        {
            TagOptions("tags:" + NewKey())(options);
            options.MaxObservedKeys = 1;
        });
        HybridCache cache = reader.GetRequiredService<HybridCache>();
        var options = new HybridCacheEntryOptions
        {
            Flags = disableLocalCache ? HybridCacheEntryFlags.DisableLocalCache : HybridCacheEntryFlags.None,
        };

        if (!disableLocalCache)
        {
            await cache.SetAsync(NewKey(), "occupy observation", new HybridCacheEntryOptions
            {
                Flags = HybridCacheEntryFlags.DisableDistributedCache,
            });
            await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(1);
        }

        string key = NewKey();
        string[] tags = [NewKey()];

        if (hasInvalidatedPayload)
        {
            await using var writer = BuildProvider(false, clientPrefix: prefix);
            await WriteInvalidatedPayloadAsync(writer, key, tags[0]);
        }

        using var listener = new StampedeJoinListener();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> first = cache.GetOrCreateAsync(key, async _ =>
        {
            entered.SetResult();
            await release.Task;
            return "first";
        }, options, tags).AsTask();

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task<string> second = cache.GetOrCreateAsync(key, _ => ValueTask.FromResult("second"), options, tags).AsTask();

            // Wait until the second caller joins the native stampede or finishes its own fill.
            await Task.WhenAny(listener.Joined.Task, second).WaitAsync(TimeSpan.FromSeconds(10));
            release.SetResult();

            await Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo("first");
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task UpdatedTagMarkerStartsANewFallbackFill()
    {
        string prefix = "fallback:" + NewKey() + ":";
        await using var writer = BuildProvider(false, clientPrefix: prefix);
        await using var reader = BuildProvider(true, clientPrefix: prefix, configure: TagOptions("tags:" + NewKey()));
        HybridCache cache = reader.GetRequiredService<HybridCache>();
        string key = NewKey();
        string tag = NewKey();
        var options = new HybridCacheEntryOptions
        {
            Flags = HybridCacheEntryFlags.DisableLocalCache | HybridCacheEntryFlags.DisableDistributedCacheWrite,
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> first = cache.GetOrCreateAsync(key, async _ =>
        {
            entered.SetResult();
            await release.Task;
            return "first";
        }, options, [tag]).AsTask();

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // A regular HybridCache writes the marker without publishing a tag notification.
            await writer.GetRequiredService<HybridCache>().RemoveByTagAsync(tag);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string second = await cache.GetOrCreateAsync(key, _ => ValueTask.FromResult("second"),
                options, [tag], timeout.Token);
            await Assert.That(second).IsEqualTo("second");
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task RemoteKeyWriteStartsANewFallbackRead(bool disableLocalCache, bool hasInvalidatedPayload)
    {
        string prefix = "fallback:" + NewKey() + ":";
        await using var writer = BuildProvider(false, clientPrefix: prefix);
        await using var reader = BuildProvider(true, clientPrefix: prefix, configure: options =>
        {
            TagOptions("tags:" + NewKey())(options);
            options.MaxObservedKeys = 1;
        });
        HybridCache cache = reader.GetRequiredService<HybridCache>();
        var options = new HybridCacheEntryOptions
        {
            Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite
                | (disableLocalCache ? HybridCacheEntryFlags.DisableLocalCache : HybridCacheEntryFlags.None),
        };

        if (!disableLocalCache)
        {
            await cache.SetAsync(NewKey(), "occupy observation", new HybridCacheEntryOptions
            {
                Flags = HybridCacheEntryFlags.DisableDistributedCache,
            });
            await Assert.That(Coherent(reader).ObservationCount).IsEqualTo(1);
        }

        string key = NewKey();
        string[] tags = [NewKey()];

        if (hasInvalidatedPayload)
        {
            await WriteInvalidatedPayloadAsync(writer, key, tags[0]);
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> first = cache.GetOrCreateAsync(key, async _ =>
        {
            entered.SetResult();
            await release.Task;
            return "first";
        }, options, tags).AsTask();

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await writer.GetRequiredService<HybridCache>().SetAsync(key, "replacement", tags: tags);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string second = await cache.GetOrCreateAsync(key, _ => ValueTask.FromResult("second"),
                options, tags, timeout.Token);
            await Assert.That(second).IsEqualTo("replacement");
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task WriteInvalidatedPayloadAsync(ServiceProvider writer, string key, string tag)
    {
        HybridCache cache = writer.GetRequiredService<HybridCache>();
        await cache.SetAsync(key, "invalidated", LongLived, [tag]);
        await cache.RemoveByTagAsync(tag);
        await Assert.That(await writer.GetRequiredService<IDistributedCache>().GetAsync(key)).IsNotNull();
    }

    private sealed class StampedeJoinListener : EventListener
    {
        internal TaskCompletionSource Joined
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Extensions-HybridCache")
            {
                EnableEvents(eventSource, EventLevel.Verbose);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName == "StampedeJoin")
            {
                Joined.TrySetResult();
            }
        }
    }
}
