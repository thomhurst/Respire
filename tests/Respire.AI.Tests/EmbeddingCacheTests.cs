using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Respire.IntegrationTests;
using TUnit.Core;

namespace Respire.AI.Tests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class EmbeddingCacheTests(ModernRedisTestContainer fixture)
{
    [Test, Arguments(2), Arguments(3)]
    public async Task MixedBatchesPreserveOrderAndGenerateOnlyUniqueMisses(int protocol)
    {
        await using var redis = await Connect(protocol);
        var provider = new Generator();
        using var cache = Build(provider, redis);
        var first = await cache.GenerateAsync(["one", "second", "one"]);
        provider.Batches.Should().ContainSingle().Which.Should().Equal("one", "second");
        first[0].Vector.ToArray().Should().Equal(first[2].Vector.ToArray());
        first[0].Should().NotBeSameAs(first[2]);
        var mixed = await cache.GenerateAsync(["second", "three", "one", "three"]);
        provider.Batches[1].Should().Equal("three");
        mixed.Select(x => x.Vector.Span[0]).Should().Equal(6, 5, 3, 5);
        mixed[0].ModelId.Should().Be(first[1].ModelId);
        mixed[0].CreatedAt.Should().Be(first[1].CreatedAt);
        mixed.Usage!.InputTokenCount.Should().Be(1);
        var hits = await cache.GenerateAsync(["three", "one"]);
        hits.Usage.Should().BeNull();
        provider.Batches.Should().HaveCount(2);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task ExactTextKeysPreserveCaseWhitespaceAndUtf16CodeUnits(int protocol)
    {
        await using var redis = await Connect(protocol);
        var provider = new Generator();
        using var cache = Build(provider, redis);
        string[] inputs = ["text", "Text", " text", "", "\uFFFD", "\uD800"];
        await cache.GenerateAsync(inputs);
        (await Keys(redis)).Should().HaveCount(inputs.Length);
        await cache.GenerateAsync(Enumerable.Reverse(inputs));
        provider.Batches.Should().ContainSingle().Which.Should().Equal(inputs);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task ModelProviderDimensionsAndNamespaceAreIsolated(int protocol)
    {
        await using var redis = await Connect(protocol);
        var name = Guid.NewGuid().ToString("N");
        var provider = new Generator();
        using var cache = Build(provider, redis, new() { CacheName = name });
        await cache.GenerateAsync(["same"]);
        await cache.GenerateAsync(["same"], new() { ModelId = "other" });
        await cache.GenerateAsync(["same"], new() { Dimensions = 3 });
        provider.Model = "new-default";
        await cache.GenerateAsync(["same"]);
        provider.Provider = "other-provider";
        await cache.GenerateAsync(["same"]);
        provider.Uri = new("https://other.example");
        await cache.GenerateAsync(["same"]);
        using var other = Build(provider, redis);
        await other.GenerateAsync(["same"]);
        provider.Batches.Should().HaveCount(7);
        await cache.GenerateAsync(["same"]);
        provider.Batches.Should().HaveCount(7);
        // New decorators can reuse entries from another instance.
        using var shared = Build(provider, redis, new() { CacheName = name });
        await shared.GenerateAsync(["same"]);
        provider.Batches.Should().HaveCount(7);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task TtlExpiresAndHitsDoNotRenewIt(int protocol)
    {
        await using var redis = await Connect(protocol);
        var provider = new Generator();
        using var cache = Build(provider, redis, new() { CacheName = Guid.NewGuid().ToString("N"), TimeToLive = TimeSpan.FromMinutes(5) });
        await cache.GenerateAsync(["ttl"]);
        var key = (await Keys(redis)).Single();
        var ttl = await redis.Keys.ExpiryAsync(key);
        ttl.TimeToLive!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
        await redis.Keys.ExpireAsync(key, TimeSpan.FromMinutes(1));
        await cache.GenerateAsync(["ttl"]);
        (await redis.Keys.ExpiryAsync(key)).TimeToLive!.Value.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
        // Expire deterministically; Redis owns expiry, so no timing-dependent sleep is needed.
        await redis.Keys.ExpireAsync(key, TimeSpan.FromMilliseconds(-1));
        await cache.GenerateAsync(["ttl"]);
        provider.Batches.Should().HaveCount(2);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task BinaryStorageAndCorruptEntryRecovery(int protocol)
    {
        await using var redis = await Connect(protocol);
        var provider = new Generator();
        using var cache = Build(provider, redis);
        await cache.GenerateAsync(["binary"]);
        var key = (await Keys(redis)).Single();
        (await redis.Strings.GetBytesAsync(key))!.Length.Should().BeLessThan(64);
        (await redis.Keys.ExpiryAsync(key)).TimeToLive.Should().BeNull();
        await redis.Strings.SetAsync(key, (RespireValue)new byte[] { 0 });
        await cache.GenerateAsync(["binary"]);
        await cache.GenerateAsync(["binary"]);
        provider.Batches.Should().HaveCount(2);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task DefaultDimensionsValidateTheWholeProviderBatch(int protocol)
    {
        await using var redis = await Connect(protocol);
        var provider = new Generator { WrongDimensions = true };
        using var cache = Build(provider, redis);
        Func<Task> generate = () => cache.GenerateAsync(["invalid"]);
        await generate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*dimensions*");
        (await Keys(redis)).Should().BeEmpty();
        provider.WrongDimensions = false;
        (await cache.GenerateAsync(["invalid"]))[0].Vector.Length.Should().Be(2);
        await cache.GenerateAsync(["invalid"]);
        provider.Batches.Should().HaveCount(2);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task DefaultDimensionsRejectAnOtherwiseValidCachedVector(int protocol)
    {
        await using var redis = await Connect(protocol);
        var provider = new Generator();
        using var cache = Build(provider, redis);
        await cache.GenerateAsync(["default"]);
        var defaultKey = (await Keys(redis)).Single();
        await cache.GenerateAsync(["override"], new() { Dimensions = 3 });
        var overrideKey = (await Keys(redis)).Single(key => key != defaultKey);
        await redis.Strings.SetAsync(defaultKey, (RespireValue)(await redis.Strings.GetBytesAsync(overrideKey))!);
        (await cache.GenerateAsync(["default"]))[0].Vector.Length.Should().Be(2);
        await cache.GenerateAsync(["default"]);
        provider.Batches.Should().HaveCount(3);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task WholeFloatTruncationAndExtensionAreMissesWithoutDimensionMetadata(int protocol)
    {
        await using var redis = await Connect(protocol);
        var provider = new Generator { DefaultDimensions = null };
        using var cache = Build(provider, redis);
        await cache.GenerateAsync(["corrupt"]);
        var key = (await Keys(redis)).Single();
        var original = (await redis.Strings.GetBytesAsync(key))!;
        await redis.Strings.SetAsync(key, (RespireValue)original[..^sizeof(float)]);
        (await cache.GenerateAsync(["corrupt"]))[0].Vector.Length.Should().Be(2);
        await redis.Strings.SetAsync(key, (RespireValue)original.Concat(new byte[sizeof(float)]).ToArray());
        (await cache.GenerateAsync(["corrupt"]))[0].Vector.Length.Should().Be(2);
        await cache.GenerateAsync(["corrupt"]);
        provider.Batches.Should().HaveCount(3);
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task CustomOptionsAndProviderPropertiesBypassStorage(int protocol)
    {
        await using var redis = await Connect(protocol);
        var provider = new Generator();
        using var cache = Build(provider, redis);
        foreach (var options in new EmbeddingGenerationOptions[]
        {
            new() { AdditionalProperties = new() { ["task"] = "query" } },
            new() { RawRepresentationFactory = _ => new object() },
            new CustomOptions(),
        })
        {
            await cache.GenerateAsync(["custom"], options);
            await cache.GenerateAsync(["custom"], options);
            provider.LastOptions.Should().BeSameAs(options);
        }
        provider.Properties = new() { ["custom"] = "value" };
        (await cache.GenerateAsync(["custom"]))[0].AdditionalProperties!["custom"].Should().Be("value");
        await cache.GenerateAsync(["custom"]);
        provider.Batches.Should().HaveCount(8);
        (await Keys(redis)).Should().BeEmpty();
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task CancellationProviderErrorsAndInvalidResponsesDoNotPopulate(int protocol)
    {
        await using var redis = await Connect(protocol);
        var provider = new Generator();
        using var cache = Build(provider, redis);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Func<Task> cancel = () => cache.GenerateAsync(["cancel"], cancellationToken: cancelled.Token);
        await cancel.Should().ThrowAsync<OperationCanceledException>();
        provider.Batches.Should().BeEmpty();
        provider.Error = new InvalidOperationException("provider failed");
        Func<Task> fail = () => cache.GenerateAsync(["failure"]);
        await fail.Should().ThrowAsync<InvalidOperationException>().WithMessage("provider failed");
        provider.Error = null;
        provider.Empty = true;
        await fail.Should().ThrowAsync<InvalidOperationException>();
        provider.Empty = false;
        provider.WrongDimensions = true;
        Func<Task> wrong = () => cache.GenerateAsync(["wrong"], new() { Dimensions = 3 });
        await wrong.Should().ThrowAsync<InvalidOperationException>();
        provider.WrongDimensions = false;
        using var afterProvider = new CancellationTokenSource();
        provider.BeforeReturn = () => { afterProvider.Cancel(); return Task.CompletedTask; };
        Func<Task> cancelAfter = () => cache.GenerateAsync(["cancel-after"], cancellationToken: afterProvider.Token);
        await cancelAfter.Should().ThrowAsync<OperationCanceledException>();
        (await Keys(redis)).Should().BeEmpty();
    }

    [Test, Arguments(2), Arguments(3)]
    public async Task RedisFailuresHonorFailOpenForReadsAndWrites(int protocol)
    {
        await using var redis = await Connect(protocol);
        var provider = new Generator();
        using var cache = Build(provider, redis);
        provider.BeforeReturn = async () => await redis.DisposeAsync();
        (await cache.GenerateAsync(["write-failure"])).Should().ContainSingle();
        (await cache.GenerateAsync(["read-failure"])).Should().ContainSingle();
        using var strict = Build(provider, redis, new() { FailOpen = false });
        Func<Task> fail = () => strict.GenerateAsync(["strict"]);
        await fail.Should().ThrowAsync<Exception>();
        provider.Batches.Should().HaveCount(2);
        await using var writeRedis = await Connect(protocol);
        var writeProvider = new Generator { BeforeReturn = async () => await writeRedis.DisposeAsync() };
        using var strictWrite = Build(writeProvider, writeRedis, new() { FailOpen = false });
        Func<Task> writeFail = () => strictWrite.GenerateAsync(["strict-write"]);
        await writeFail.Should().ThrowAsync<Exception>();
        writeProvider.Batches.Should().ContainSingle();
    }

    [Test, NotInParallel]
    public async Task MetricsCountInputPositionsWithoutHighCardinalityTags()
    {
        await using var redis = await Connect(3);
        var provider = new Generator();
        using var cache = Build(provider, redis);
        var counts = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Respire.AI") meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            tags.Length.Should().Be(0);
            counts[instrument.Name] = counts.GetValueOrDefault(instrument.Name) + value;
        });
        listener.Start();
        await cache.GenerateAsync(["a", "a", "b"]);
        await cache.GenerateAsync(["b", "c", "a"]);
        counts["respire.ai.embedding_cache.hits"].Should().Be(2);
        counts["respire.ai.embedding_cache.misses"].Should().Be(4);
        provider.Error = new InvalidOperationException("provider failed");
        Func<Task> fail = () => cache.GenerateAsync(["a", "failure"]);
        await fail.Should().ThrowAsync<InvalidOperationException>();
        provider.Error = null;
        using var cancellation = new CancellationTokenSource();
        provider.BeforeReturn = () => { cancellation.Cancel(); return Task.CompletedTask; };
        Func<Task> cancel = () => cache.GenerateAsync(["a", "cancel"], cancellationToken: cancellation.Token);
        await cancel.Should().ThrowAsync<OperationCanceledException>();
        counts["respire.ai.embedding_cache.hits"].Should().Be(4);
        counts["respire.ai.embedding_cache.misses"].Should().Be(6);
    }

    [Test]
    public async Task EmptyInputValidationAndOwnership()
    {
        await using var redis = await Connect(3);
        var provider = new Generator();
        var cache = Build(provider, redis);
        (await cache.GenerateAsync(Array.Empty<string>())).Should().BeEmpty();
        provider.Batches.Should().BeEmpty();
        foreach (var invalid in new RespireEmbeddingCacheOptions[]
        {
            new() { CacheName = " " }, new() { TimeToLive = TimeSpan.Zero },
            new() { TimeToLive = TimeSpan.FromTicks(1) }, new() { TimeToLive = TimeSpan.FromSeconds(-1) },
        })
        {
            Action build = () => Build(provider, redis, invalid);
            build.Should().Throw<ArgumentException>();
        }
        cache.GetService(typeof(EmbeddingGeneratorMetadata)).Should().BeOfType<EmbeddingGeneratorMetadata>();
        cache.Dispose();
        provider.Disposed.Should().BeTrue();
        await redis.Strings.SetAsync(Guid.NewGuid().ToString("N"), "still caller owned");
    }

    private async Task<RespireClient> Connect(int protocol) => await RespireClient.ConnectAsync(
        RespireOptions.Parse(fixture.ConnectionString) with
        {
            Protocol = (RespireProtocol)protocol,
            KeyPrefix = Guid.NewGuid().ToString("N") + ":",
        });

    private static IEmbeddingGenerator<string, Embedding<float>> Build(Generator provider, IRespireClient redis,
        RespireEmbeddingCacheOptions? options = null) => new EmbeddingGeneratorBuilder<string, Embedding<float>>(provider)
            .UseRespireEmbeddingCache(redis, options ?? new() { CacheName = Guid.NewGuid().ToString("N") }).Build();

    private static async Task<string[]> Keys(IRespireClient redis)
    {
        var keys = new List<string>();
        await foreach (var key in redis.Keys.ScanAsync(match: "respire:embedding:v1:*")) keys.Add(key.ToString());
        return keys.ToArray();
    }

    private sealed class CustomOptions : EmbeddingGenerationOptions;

    private sealed class Generator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<string[]> Batches { get; } = [];
        public string Model { get; set; } = "model";
        public string Provider { get; set; } = "provider";
        public Uri Uri { get; set; } = new("https://provider.example");
        public bool Empty { get; set; }
        public bool WrongDimensions { get; set; }
        public int? DefaultDimensions { get; set; } = 2;
        public bool Disposed { get; private set; }
        public Exception? Error { get; set; }
        public AdditionalPropertiesDictionary? Properties { get; set; }
        public EmbeddingGenerationOptions? LastOptions { get; private set; }
        public Func<Task>? BeforeReturn { get; set; }

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Error is { } error) throw error;
            var inputs = values.ToArray();
            Batches.Add(inputs);
            LastOptions = options;
            if (BeforeReturn is { } before) await before();
            return new(Empty ? [] : inputs.Select(input => new Embedding<float>(
                Enumerable.Repeat((float)input.Length, WrongDimensions ? 1 : options?.Dimensions ?? 2).ToArray())
            {
                ModelId = options?.ModelId ?? Model,
                CreatedAt = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.FromHours(2)),
                AdditionalProperties = Properties,
            })) { Usage = new() { InputTokenCount = inputs.Length } };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(EmbeddingGeneratorMetadata)
            ? new EmbeddingGeneratorMetadata(Provider, Uri, Model, DefaultDimensions) : null;

        public void Dispose() => Disposed = true;
    }
}
