using System.Buffers.Binary;
using Microsoft.Extensions.Caching.Hybrid;

namespace Respire.Caching.Hybrid;

internal sealed partial class RespireCoherentHybridCache
{
    // Microsoft's L2 key prefix for tag invalidation markers, and its reserved wildcard tag.
    private const string TagMarkerPrefix = "__MSFT_HCT__";
    private const string WildcardTag = "*";
    private readonly Dictionary<string, List<FallbackRead>> _fallbackReads = new(StringComparer.Ordinal);
    private readonly Guid _tagSender = Guid.NewGuid();
    private readonly CancellationTokenSource _tagLifetime = new();
    private readonly TaskCompletionSource<bool> _tagStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TagInvalidationMessage? _tagMessage;
    private string? _tagChannel;
    private Task _tagWorker = Task.CompletedTask;
    // 0 = starting, 1 = ready, 2 = reconnecting, 3 = stopped.
    private int _tagReady;
    private int _tagContinuityLost;
    private int _maxTagsPerEntry;
    private long _tagGapsConsumed;

    /// <summary>Gap messages the tag worker has fully applied. Tests synchronize on it.</summary>
    internal long TagGapsConsumed => Interlocked.Read(ref _tagGapsConsumed);

    /// <summary>Delays the tag marker reads that Microsoft's contexts issue, so tests can hold them pending.</summary>
    internal Func<CancellationToken, Task>? TagMarkerReadDelayForTesting
    {
        get; set;
    }

    private void StartTagPropagation(RespireHybridCacheCoherenceOptions options)
    {
        if (options.TagInvalidationChannel is null)
        {
            return;
        }

        _tagChannel = options.TagInvalidationChannel;
        _maxTagsPerEntry = options.MaxTagsPerEntry;
        _tagMessage = new(options.TagInvalidationNamespace!, options.MaxTagInvalidationMessageBytes);
        TrackingClient.ConnectionStateChanged += TagConnectionStateChanged;
        _tagWorker = ConsumeTagInvalidationsAsync(options);
    }

    private async Task ConsumeTagInvalidationsAsync(RespireHybridCacheCoherenceOptions options)
    {
        try
        {
            await using var subscription = await TrackingClient.SubscribeAsync(_tagChannel!,
                new RespireSubscriptionOptions(options.TagInvalidationBufferSize, RespireSubscriptionOverflow.DropOldest)
                { MaxPayloadBytes = options.MaxTagInvalidationMessageBytes }, _tagLifetime.Token).ConfigureAwait(false);
            subscription.DeliveryGap += TagDeliveryGap;

            try
            {
                // An acknowledgment must not overwrite a newer reconnect transition.
                Interlocked.CompareExchange(ref _tagReady, 1, 0);
                _tagStarted.TrySetResult(true);

                await foreach (var message in subscription.WithCancellation(_tagLifetime.Token).ConfigureAwait(false))
                {
                    if (message.Kind == RespireMessageKind.Gap)
                    {
                        ResetTagContinuity();
                        // Published after the reset retires observations, so a reader that sees
                        // the count knows this gap can no longer retire later observations.
                        Interlocked.Increment(ref _tagGapsConsumed);
                        continue;
                    }

                    if (!_tagMessage!.TryDecode(message.Payload.Span, out var sender, out var tag, out var timestamp)
                        || sender == _tagSender)
                    {
                        continue;
                    }

                    Task[]? updates = null;

                    try
                    {
                        lock (_gate)
                        {
                            if (_disposed != 0)
                            {
                                return;
                            }

                            ResetTagContinuityUnderGate();
                            updates = ApplyLocalTagInvalidation(tag, timestamp);
                        }
                    }
                    finally
                    {
                        try
                        {
                            DrainRetiredObservations();
                        }
                        catch (Exception error) when (updates is not null)
                        {
                            updates = [.. updates, Task.FromException(error)];
                        }
                    }

                    await Task.WhenAll(updates).ConfigureAwait(false);
                }
            }
            finally { subscription.DeliveryGap -= TagDeliveryGap; }
        }
        catch (OperationCanceledException) when (_tagLifetime.IsCancellationRequested) { }
        catch
        {
            // Startup, replay, and terminal subscription failures fail closed for L1.
            // Cache reads still use the original L2 backend; no background fault is abandoned.
        }
        finally
        {
            Volatile.Write(ref _tagReady, 3);
            SignalTagContinuityLoss();
            _tagStarted.TrySetResult(false);
        }
    }

    private void TagConnectionStateChanged(RespireConnectionStateChange change)
    {
        if (change.ReconnectSource != RespireReconnectSource.PubSub)
        {
            return;
        }

        var ready = (change.SourceState ?? change.State) == RespireConnectionState.Connected;
        int previous;

        do
        {
            previous = Volatile.Read(ref _tagReady);

            if (previous == 3)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref _tagReady, ready ? 1 : 2, previous) != previous);

        if (!ready)
        {
            SignalTagContinuityLoss();
        }
    }

    // Receive-path callbacks only fence local admission/publication. Cleanup runs on a cache
    // request or the consumer, never on the socket's receive loop or under its routing gate.
    private void TagDeliveryGap(RespireSubscriptionGap _) => SignalTagContinuityLoss();

    private void SignalTagContinuityLoss() => Interlocked.Exchange(ref _tagContinuityLost, 1);

    private void ResetTagContinuity()
    {
        SignalTagContinuityLoss();

        lock (_gate)
        {
            ResetTagContinuityUnderGate();
        }

        DrainRetiredObservations();
    }

    private void ResetTagContinuityUnderGate()
    {
        if (Interlocked.Exchange(ref _tagContinuityLost, 0) == 0)
        {
            return;
        }

        _removedTags.Clear();
        _fallbackReads.Clear();
        RetireAll();
        // New contexts refetch authoritative L2 tag metadata after an unreplayable gap.
        // History exhaustion remains permanent, matching the existing admission contract.
    }

    private bool CanUseTagLocalCache => _tagMessage is null
        || (Volatile.Read(ref _tagReady) == 1 && Volatile.Read(ref _tagContinuityLost) == 0);

    private async ValueTask<T> ReadFallbackAsync<TState, T>(string key, TState state,
        Func<TState, CancellationToken, ValueTask<T>> factory, HybridCacheEntryOptions options,
        IEnumerable<string>? tags, CancellationToken cancellationToken)
    {
        if (_tagMessage is null)
        {
            return await _unobserved.GetOrCreateAsync(key, state, factory, options, tags, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!IsFallbackJoinable(key, tags))
        {
            HybridCache cache = CreateContext(null, new TagReplayBackend(this));
            await SeedTagMetadataAsync(cache, tags).WaitAsync(cancellationToken).ConfigureAwait(false);
            return await cache.GetOrCreateAsync(key, state, factory, options, tags, cancellationToken)
                .ConfigureAwait(false);
        }

        string[] seeded = GetSeedTags(tags);
        Array.Sort(seeded, 1, seeded.Length - 1, StringComparer.Ordinal);
        HybridCacheEntryFlags flags = EffectiveFlags(options);
        DateTimeOffset[] markers = await ReadTagMarkersAsync(seeded, cancellationToken).ConfigureAwait(false);
        bool readsL2 = (flags & HybridCacheEntryFlags.DisableDistributedCacheRead) == 0;
        HybridCacheEntryOptions fillOptions = readsL2
            ? WithFlags(flags | HybridCacheEntryFlags.DisableDistributedCacheRead)
            : options;

        if (!readsL2)
        {
            return await ReadFillAsync(cancellationToken).ConfigureAwait(false);
        }

        // Each caller checks L2 independently so it can see a remote replacement. Let
        // HybridCache validate the payload and join the fill only from its miss callback.
        HybridCache lookup = CreateContext(null, new TagReplayBackend(this));
        await SeedTagMetadataAsync(lookup, seeded, markers).WaitAsync(cancellationToken).ConfigureAwait(false);
        return await lookup.GetOrCreateAsync(key, ReadFillAsync,
            WithFlags(flags | HybridCacheEntryFlags.DisableDistributedCacheWrite), tags, cancellationToken)
            .ConfigureAwait(false);

        async ValueTask<T> ReadFillAsync(CancellationToken token)
        {
            FallbackRead read = AcquireFallbackRead(key, flags, seeded, markers);

            try
            {
                await read.TagReplay.WaitAsync(token).ConfigureAwait(false);
                return await read.Cache.GetOrCreateAsync(key, state, factory, fillOptions, tags, token)
                    .ConfigureAwait(false);
            }
            finally
            {
                ReleaseFallbackRead(key, read);
            }
        }

        HybridCacheEntryOptions WithFlags(HybridCacheEntryFlags updatedFlags)
        {
            return new HybridCacheEntryOptions
            {
                Flags = updatedFlags,
                Expiration = options.Expiration,
                LocalCacheExpiration = options.LocalCacheExpiration,
            };
        }
    }

    private bool IsFallbackJoinable(string key, IEnumerable<string>? tags)
    {
        // Leave HybridCache's validation and uncached factory behavior to an isolated context.
        return !string.IsNullOrWhiteSpace(key) && key.Length <= _hybridOptions.MaximumKeyLength
            && !HasControlCharacter(key)
            && (tags is null || !tags.Any(tag => tag == WildcardTag || string.IsNullOrWhiteSpace(tag)));
    }

    private FallbackRead AcquireFallbackRead(string key, HybridCacheEntryFlags flags, string[] tags,
        DateTimeOffset[] markers)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _tagContinuityLost) != 0)
            {
                _fallbackReads.Clear();
            }

            MergeRememberedTagMarkers(tags, markers);

            _fallbackReads.TryGetValue(key, out List<FallbackRead>? reads);

            if (reads is not null)
            {
                foreach (FallbackRead read in reads)
                {
                    if (!read.TagReplay.IsFaulted && read.Flags == flags && tags.AsSpan().SequenceEqual(read.Tags)
                        && markers.AsSpan().SequenceEqual(read.Markers))
                    {
                        read.ActiveCalls++;
                        return read;
                    }
                }
            }

            FallbackRead newRead = CreateFallbackRead(flags, tags, markers);

            if (reads is null)
            {
                _fallbackReads.Add(key, [newRead]);
            }
            else
            {
                reads.Add(newRead);
            }

            return newRead;
        }
    }

    private FallbackRead CreateFallbackRead(HybridCacheEntryFlags flags, string[] tags, DateTimeOffset[] markers)
    {
        HybridCache cache = CreateContext(null, new TagReplayBackend(this));
        Task tagReplay = ReplayTagMarkers(cache, tags, markers);
        return new FallbackRead(cache, tagReplay, flags, tags, markers);
    }

    private void ReleaseFallbackRead(string key, FallbackRead read)
    {
        lock (_gate)
        {
            if (--read.ActiveCalls == 0 && _fallbackReads.TryGetValue(key, out List<FallbackRead>? reads)
                && reads.Remove(read) && reads.Count == 0)
            {
                _fallbackReads.Remove(key);
            }
        }
    }

    private sealed class FallbackRead(HybridCache cache, Task tagReplay, HybridCacheEntryFlags flags,
        string[] tags, DateTimeOffset[] markers)
    {
        internal readonly HybridCache Cache = cache;
        internal readonly Task TagReplay = tagReplay;
        internal readonly HybridCacheEntryFlags Flags = flags;
        internal readonly string[] Tags = tags;
        internal readonly DateTimeOffset[] Markers = markers;
        internal int ActiveCalls = 1;
    }

    private static string[] GetSeedTags(IEnumerable<string>? tags)
    {
        return [WildcardTag, .. (tags ?? []).Where(tag => tag != WildcardTag).Distinct(StringComparer.Ordinal)];
    }

    // Microsoft.Extensions.Caching.Hybrid 10.10.0 checks a payload tag whose invalidation time is
    // still loading against the reader's start time instead of the payload's creation time, and it
    // ignores a wildcard marker that is still loading (dotnet/extensions#7786). A new context that
    // reads L2 before its own marker reads complete can then return an entry already removed by tag.
    // New contexts are how this cache reloads authoritative tag metadata, so load the request tags'
    // and the wildcard's markers first and replay them. The context then has completed metadata.
    private async Task SeedTagMetadataAsync(HybridCache cache, IEnumerable<string>? tags)
    {
        string[] seeded = GetSeedTags(tags);
        DateTimeOffset[] markers = await ReadTagMarkersAsync(seeded, CancellationToken.None).ConfigureAwait(false);
        await SeedTagMetadataAsync(cache, seeded, markers).ConfigureAwait(false);
    }

    private Task SeedTagMetadataAsync(HybridCache cache, string[] tags, DateTimeOffset[] markers)
    {
        lock (_gate)
        {
            MergeRememberedTagMarkers(tags, markers);
            return ReplayTagMarkers(cache, tags, markers);
        }
    }

    private void MergeRememberedTagMarkers(string[] tags, DateTimeOffset[] markers)
    {
        // A replay overwrites the context's time for its tag. Under the gate, a newer
        // invalidation recorded by the worker or a local removal is never lost.
        for (int i = 0; i < tags.Length; i++)
        {
            if (_removedTags.TryGetValue(tags[i], out DateTimeOffset remembered) && remembered > markers[i])
            {
                markers[i] = remembered;
            }
        }
    }

    private Task ReplayTagMarkers(HybridCache cache, string[] tags, DateTimeOffset[] markers)
    {
        var replays = new Task[tags.Length];

        for (int i = 0; i < tags.Length; i++)
        {
            replays[i] = ReplayTagAsync(cache, tags[i], markers[i]);
        }

        return Task.WhenAll(replays);
    }

    private Task<DateTimeOffset[]> ReadTagMarkersAsync(string[] tags, CancellationToken cancellationToken)
    {
        return Task.WhenAll(tags.Select(tag => ReadTagMarkerAsync(tag, cancellationToken)));
    }

    private async Task<DateTimeOffset> ReadTagMarkerAsync(string tag, CancellationToken cancellationToken)
    {
        try
        {
            var marker = await _distributed.GetAsync(TagMarkerPrefix + tag, cancellationToken).ConfigureAwait(false);

            if (marker is null)
            {
                return DateTimeOffset.MinValue;
            }

            if (marker.Length == sizeof(long))
            {
                var ticks = BinaryPrimitives.ReadInt64LittleEndian(marker);

                if (ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks)
                {
                    return new DateTimeOffset(ticks, TimeSpan.Zero);
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Fall through to the fail-closed result below.
        }
        // Like Microsoft's own reader, treat a malformed or unreadable marker as removed now.
        return _clock.GetUtcNow();
    }

    private IEnumerable<string>? PrepareTags(IEnumerable<string>? tags)
    {
        if (_tagMessage is null || tags is null)
        {
            return tags;
        }

        var snapshot = new List<string>();

        foreach (var tag in tags)
        {
            if (snapshot.Count == _maxTagsPerEntry)
            {
                throw new ArgumentException("The entry exceeds MaxTagsPerEntry.", nameof(tags));
            }

            _tagMessage.ValidateTag(tag);
            snapshot.Add(tag);
        }

        return snapshot.ToArray();
    }

    private Task[] ApplyLocalTagInvalidation(string tag, DateTimeOffset timestamp)
    {
        if (_removedTags.TryGetValue(tag, out var previous) && previous >= timestamp)
        {
            return [];
        }

        if (!_tagHistoryExhausted)
        {
            if (!_removedTags.ContainsKey(tag) && _removedTags.Count >= _maxRememberedTags)
            {
                _tagHistoryExhausted = true;
                _removedTags.Clear();
                RetireAll();
            }
            else
            {
                _removedTags[tag] = timestamp;
            }
        }

        var observations = _observations.Values.ToArray();
        var updates = observations.Select(observation => ReplayTagAsync(observation.Cache, tag, timestamp)).ToArray();

        if (_tagMessage is not null)
        {
            // The public HybridCache API keeps stored tags opaque. Retire all active fills
            // conservatively, including callers whose tag list differs from the stored L2 tags.
            // Idle unrelated L1 entries remain available through their updated tag metadata.
            _fallbackReads.Clear();

            foreach (var observation in observations)
            {
                if (tag == "*" || observation.ActiveCalls != 0)
                {
                    Retire(observation);
                }
            }
        }

        return updates;
    }

    private async Task WriteTagAndPublishAsync(string tag, DateTimeOffset timestamp, byte[]? message,
        CancellationToken cancellationToken)
    {
        // A fresh, L1-disabled writer avoids retaining an unbounded second tag dictionary.
        var writer = _tagMessage is null ? _unobserved : CreateContext(null, _distributed);

        using (_clock.Replay(timestamp))
        {
            await writer.RemoveByTagAsync(tag, cancellationToken).ConfigureAwait(false);
        }

        if (message is not null)
        {
            await TrackingClient.PublishAsync(_tagChannel!, message, cancellationToken).ConfigureAwait(false);
        }
    }
}
