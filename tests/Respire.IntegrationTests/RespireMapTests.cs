using System.Globalization;
using System.Text;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class RespireMapTests(ModernRedisTestContainer fixture)
{
    [Test]
    [Arguments(RespireClientTrackingMode.OptIn)]
    [Arguments(RespireClientTrackingMode.Broadcast)]
    public async Task IndependentViewsInvalidateOnWritesDeletesAndExternalMutations(RespireClientTrackingMode mode)
    {
        var prefix = $"map:{Guid.NewGuid():N}:";
        await using var first = await ConnectAsync(mode, prefix);
        await using var second = await ConnectAsync(mode, prefix);
        await using var a = Create(first.WithKeyPrefix(prefix), "values");
        await using var b = Create(second.WithKeyPrefix(prefix), "values");
        await a.SetAsync(1, 10);
        (await b.GetAsync(1)).Value.Should().Be(10);
        var hits = second.ClientSideCache!.GetStatistics().Hits;
        (await b.GetAsync(1)).Value.Should().Be(10);
        second.ClientSideCache.GetStatistics().Hits.Should().BeGreaterThan(hits);

        await a.SetAsync(1, 20);
        await UntilAsync(async () => (await b.GetAsync(1)).Value == 20);
        (await a.RemoveAsync(1)).Should().BeTrue();
        await UntilAsync(async () => !(await b.GetAsync(1)).Found);
        await first.Hashes.SetAsync(prefix + "values", "1", (RespireValue)"30");
        await UntilAsync(async () => (await b.GetAsync(1)).Value == 30);
    }

    [Test]
    public async Task LoaderDistinguishesZeroFromMissingAndDoesNotEchoToWriter()
    {
        await using var client = await ConnectAsync();
        var loads = 0;
        var writes = 0;
        await using var map = Create(client, UniqueKey(), new()
        {
            Loader = (key, _) => { loads++; return ValueTask.FromResult(new RespireGet<int>(key == 1, 0)); },
            Writer = (_, _) => { writes++; return ValueTask.CompletedTask; },
        });
        (await map.GetAsync(1)).Should().Be(new RespireGet<int>(true, 0));
        (await map.GetAsync(1)).Found.Should().BeTrue();
        (await map.GetAsync(2)).Found.Should().BeFalse();
        loads.Should().Be(2);
        writes.Should().Be(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SlowLoaderDoesNotOverwriteConcurrentWriter(bool expiry)
    {
        await using var client = await ConnectAsync();
        await using var other = await ConnectAsync();
        var hash = UniqueKey();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var map = Create(client, hash, new()
        {
            EntryExpiration = expiry ? TimeSpan.FromMinutes(1) : null,
            Loader = async (_, _) => { entered.SetResult(); await resume.Task; return new(true, 10); },
        });
        var read = map.GetAsync(1).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await other.Hashes.SetAsync(hash, "1", (RespireValue)"99");
        }
        finally { resume.TrySetResult(); }
        (await read).Value.Should().Be(99);
    }

    [Test]
    public async Task PreloadWarmsRedisFieldsAndCanLoadKnownKeys()
    {
        await using var client = await ConnectAsync();
        var hash = UniqueKey();
        await client.Hashes.SetAsync(hash, "1", (RespireValue)"10");
        await using var map = Create(client, hash, new()
        {
            Loader = (key, _) => ValueTask.FromResult(new RespireGet<int>(true, key * 10)),
        });
        await map.PreloadAsync();
        var misses = client.ClientSideCache!.GetStatistics().Misses;
        (await map.GetAsync(1)).Value.Should().Be(10);
        client.ClientSideCache.GetStatistics().Misses.Should().Be(misses);
        (await map.PreloadAsync([1, 2, 3])).Should().Be(3);
        (await client.Hashes.CountAsync(hash)).Should().Be(3);
    }

    [Test]
    public async Task ClearLocalCachesPreservesValuesAndExpiryAndInvalidatesOtherClients()
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();
        var hash = UniqueKey();
        await using var a = Create(first, hash);
        await using var b = Create(second, hash);
        await a.SetAsync(1, 10, TimeSpan.FromMinutes(1));
        (await b.GetAsync(1)).Value.Should().Be(10);
        var ttlBefore = (await first.Hashes.ExpiryTimeAsync(hash, "1"))[0];
        await a.ClearLocalCachesAsync();
        await UntilAsync(() => Task.FromResult(second.ClientSideCache!.Count == 0));
        (await b.GetAsync(1)).Value.Should().Be(10);
        (await first.Hashes.CountAsync(hash)).Should().Be(1);
        (await first.Hashes.ExpiryTimeAsync(hash, "1"))[0].Should().Be(ttlBefore);

        await a.RemoveAsync(1);
        await UntilAsync(async () => !(await b.GetAsync(1)).Found);
        await a.ClearLocalCachesAsync();
        await UntilAsync(() => Task.FromResult(second.ClientSideCache!.Count == 0));
        (await first.Hashes.CountAsync(hash)).Should().Be(0);
    }

    [Test]
    public async Task FieldExpiryInvalidatesCachedValuesAcrossClients()
    {
        await using var first = await ConnectAsync();
        await using var second = await ConnectAsync();
        var hash = UniqueKey();
        await using var a = Create(first, hash, new() { EntryExpiration = TimeSpan.FromMilliseconds(500) });
        await using var b = Create(second, hash);
        await a.SetAsync(1, 10);
        (await b.GetAsync(1)).Found.Should().BeTrue();
        await UntilAsync(async () => !(await b.GetAsync(1)).Found);
    }

    [Test]
    public async Task WriteThroughIsOrderedAndReportsFailureAfterRedisWrite()
    {
        await using var client = await ConnectAsync();
        var changes = new List<RespireMapChange<int, int>>();
        var hash = UniqueKey();
        await using var map = Create(client, hash, new()
        {
            Writer = (batch, _) =>
            {
                changes.AddRange(batch);
                if (batch[0].Entry.Value == 99) throw new IOException("store unavailable");
                return ValueTask.CompletedTask;
            },
        });
        await map.SetAsync(1, 10);
        await map.RemoveAsync(1);
        await map.RemoveAsync(2);
        changes.Select(change => change.Entry.Found).Should().Equal(true, false, false);
        var failedWrite = () => map.SetAsync(1, 99).AsTask();
        await failedWrite.Should().ThrowAsync<IOException>();
        map.LastWriterError.Should().BeOfType<IOException>();
        (await map.GetAsync(1)).Value.Should().Be(99);
    }

    [Test]
    public async Task CleanShutdownDrainsPartialBatchesInOrderAndKeepsClientAlive()
    {
        await using var client = await ConnectAsync();
        var batches = new List<RespireMapChange<int, int>[]>();
        var map = Create(client, UniqueKey(), new()
        {
            WriteBehind = true, BatchSize = 2, FlushInterval = TimeSpan.FromHours(1),
            Writer = (batch, _) => { batches.Add(batch.ToArray()); return ValueTask.CompletedTask; },
        });
        await using (map)
        {
            await map.SetAsync(1, 10);
            await map.SetAsync(1, 20);
            await map.RemoveAsync(1);
            batches.Should().BeEmpty();
        }
        batches.Select(batch => batch.Length).Should().Equal(2, 1);
        batches.SelectMany(batch => batch).Select(change => change.Entry).Should().Equal(
            new RespireGet<int>(true, 10), new RespireGet<int>(true, 20), default);
        var disposedWrite = () => map.SetAsync(2, 30).AsTask();
        await disposedWrite.Should().ThrowAsync<ObjectDisposedException>();
        await map.DisposeAsync();
        await client.Hashes.SetAsync(UniqueKey(), "still", (RespireValue)"alive");
    }

    [Test]
    public async Task FailedShutdownRetainsBatchForRetry()
    {
        await using var client = await ConnectAsync();
        var attempts = 0;
        var received = new List<RespireMapChange<int, int>[]>();
        var map = Create(client, UniqueKey(), new()
        {
            WriteBehind = true, FlushInterval = TimeSpan.FromHours(1),
            Writer = (batch, _) =>
            {
                received.Add(batch.ToArray());
                if (++attempts == 1) throw new IOException("retry me");
                return ValueTask.CompletedTask;
            },
        });
        await map.SetAsync(1, 10);
        var dispose = () => map.DisposeAsync().AsTask();
        await dispose.Should().ThrowAsync<IOException>();
        map.LastWriterError.Should().BeOfType<IOException>();
        await map.DisposeAsync();
        received.Should().HaveCount(2);
        received[1].Should().Equal(received[0]);
        map.LastWriterError.Should().BeNull();
    }

    [Test]
    public async Task CapacityFailurePreventsNextRedisMutation()
    {
        await using var client = await ConnectAsync();
        var fail = true;
        var hash = UniqueKey();
        var map = Create(client, hash, new()
        {
            WriteBehind = true, BatchSize = 1, MaxPendingWrites = 1, FlushInterval = TimeSpan.FromHours(1),
            Writer = (_, _) => fail ? ValueTask.FromException(new IOException("full")) : ValueTask.CompletedTask,
        });
        try
        {
            await map.SetAsync(1, 10);
            var nextWrite = () => map.SetAsync(2, 20).AsTask();
            await nextWrite.Should().ThrowAsync<IOException>();
            (await client.Hashes.ExistsAsync(hash, "2")).Should().BeFalse();
        }
        finally { fail = false; await map.DisposeAsync(); }
    }

    [Test]
    public async Task BackgroundWriterRetriesFailedBatch()
    {
        await using var client = await ConnectAsync();
        var attempts = 0;
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var map = Create(client, UniqueKey(), new()
        {
            WriteBehind = true, FlushInterval = TimeSpan.FromMilliseconds(20),
            Writer = (batch, _) =>
            {
                batch[0].Entry.Value.Should().Be(10);
                if (++attempts == 1) throw new IOException("transient");
                written.TrySetResult();
                return ValueTask.CompletedTask;
            },
        });
        await map.SetAsync(1, 10);
        await written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await map.FlushAsync();
        attempts.Should().Be(2);
        map.LastWriterError.Should().BeNull();
    }

    [Test]
    public async Task DisposeWaitsForAcceptedWriteAndRejectsNewOperations()
    {
        await using var client = await ConnectAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var map = Create(client, UniqueKey(), new()
        {
            Writer = async (_, _) => { entered.TrySetResult(); await resume.Task; },
        });
        var write = map.SetAsync(1, 10).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var dispose = map.DisposeAsync().AsTask();
            dispose.IsCompleted.Should().BeFalse();
            var nextWrite = () => map.SetAsync(2, 20).AsTask();
            await nextWrite.Should().ThrowAsync<ObjectDisposedException>();
            resume.TrySetResult();
            await write;
            await dispose;
        }
        finally { resume.TrySetResult(); await map.DisposeAsync(); }
    }

    [Test]
    public async Task DisposeWaitsForEntireAcceptedPreload()
    {
        await using var client = await ConnectAsync();
        var loaded = new List<int>();
        await using var map = Create(client, UniqueKey(), new()
        {
            Loader = (key, _) =>
            {
                loaded.Add(key);
                return ValueTask.FromResult(new RespireGet<int>(true, key * 10));
            },
        });
        Task? dispose = null;
        IEnumerable<int> Keys()
        {
            yield return 1;
            // Start shutdown precisely between entries, after the first read completes.
            dispose = map.DisposeAsync().AsTask();
            dispose.IsCompleted.Should().BeFalse();
            yield return 2;
        }

        (await map.PreloadAsync(Keys())).Should().Be(2);
        await dispose!;
        loaded.Should().Equal(1, 2);
        var nextRead = () => map.GetAsync(3).AsTask();
        await nextRead.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task MutableValuesAreDetachedFromCacheAndQueuedWrites()
    {
        await using var client = await ConnectAsync();
        byte[]? written = null;
        await using var map = new RespireMap<string, byte[]>(client, UniqueKey(), key => key,
            value => value, bytes => bytes.ToArray(), new()
            {
                WriteBehind = true, FlushInterval = TimeSpan.FromHours(1),
                Writer = (batch, _) => { written = batch[0].Entry.Value; return ValueTask.CompletedTask; },
            });
        byte[] value = [1, 2, 3];
        await map.SetAsync("binary", value);
        value[0] = 99;
        (await map.GetAsync("binary")).Value![0] = 88;
        (await map.GetAsync("binary")).Value.Should().Equal(1, 2, 3);
        await map.FlushAsync();
        written.Should().Equal(1, 2, 3);
    }

    [Test]
    public async Task RejectedRedisWritesDoNotReachWriter()
    {
        await using var client = await ConnectAsync();
        var hash = UniqueKey();
        await client.SetAsync(hash, "wrong type");
        var writes = 0;
        await using var map = Create(client, hash, new()
        {
            WriteBehind = true,
            Writer = (_, _) => { writes++; return ValueTask.CompletedTask; },
        });
        var write = () => map.SetAsync(1, 10).AsTask();
        await write.Should().ThrowAsync<RespireServerException>();
        await map.FlushAsync();
        writes.Should().Be(0);
    }

    [Test]
    public async Task CancellationAndInvalidExpiryDoNotMutateRedisOrQueueWrites()
    {
        await using var client = await ConnectAsync();
        var hash = UniqueKey();
        var writes = 0;
        await using var map = Create(client, hash, new()
        {
            WriteBehind = true,
            Writer = (_, _) => { writes++; return ValueTask.CompletedTask; },
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledWrite = () => map.SetAsync(1, 10, cancellationToken: cancellation.Token).AsTask();
        await cancelledWrite.Should().ThrowAsync<OperationCanceledException>();
        var invalidWrite = () => map.SetAsync(1, 10, TimeSpan.Zero).AsTask();
        await invalidWrite.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await map.FlushAsync();
        (await client.Hashes.CountAsync(hash)).Should().Be(0);
        writes.Should().Be(0);
    }

    [Test]
    public async Task InvalidOptionsFailBeforeStartingMap()
    {
        await using var client = await ConnectAsync();
        Action noWriter = () => Create(client, UniqueKey(), new() { WriteBehind = true });
        Action noCapacity = () => Create(client, UniqueKey(), new() { MaxPendingWrites = 0 });
        Action noBatch = () => Create(client, UniqueKey(), new() { BatchSize = 0 });
        Action noInterval = () => Create(client, UniqueKey(), new() { FlushInterval = TimeSpan.Zero });
        Action noExpiry = () => Create(client, UniqueKey(), new() { EntryExpiration = TimeSpan.Zero });
        noWriter.Should().Throw<ArgumentException>();
        noCapacity.Should().Throw<ArgumentOutOfRangeException>();
        noBatch.Should().Throw<ArgumentOutOfRangeException>();
        noInterval.Should().Throw<ArgumentOutOfRangeException>();
        noExpiry.Should().Throw<ArgumentOutOfRangeException>();
        await using var uncached = await RespireClient.ConnectAsync(fixture.ConnectionString);
        Action noCache = () => Create(uncached, UniqueKey());
        noCache.Should().Throw<ArgumentException>();
    }

    private ValueTask<RespireClient> ConnectAsync(RespireClientTrackingMode mode = RespireClientTrackingMode.OptIn, string? prefix = null)
        => RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        {
            Protocol = RespProtocol.Resp3, Connections = 1,
            ClientSideCache = new() { TrackingMode = mode, KeyPrefixes = prefix is null ? [] : [(RespireKey)prefix] },
        });

    private static RespireMap<int, int> Create(IRespireClient client, string hash, RespireMapOptions<int, int>? options = null)
        => new(client, hash, key => key.ToString(CultureInfo.InvariantCulture),
            value => Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture)),
            bytes => int.Parse(Encoding.UTF8.GetString(bytes.Span), CultureInfo.InvariantCulture), options);

    private static string UniqueKey() => $"map:{Guid.NewGuid():N}";

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition()) await Task.Delay(10, timeout.Token);
    }
}
