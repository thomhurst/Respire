using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

// Pin the minimum feature family rather than allowing redis:8 to advance past the compatibility floor.
public sealed class StreamReferenceRedisContainer : IAsyncInitializer, IAsyncDisposable
{
    private IContainer? _container;
    private IContainer Container => _container ?? throw new InvalidOperationException("Redis container has not been initialized.");
    public string ConnectionString => $"redis://{Container.Hostname}:{Container.GetMappedPublicPort(6379)}";
    public async Task InitializeAsync() => _container = await ContainerStartRetry.StartAsync(() => new ContainerBuilder("redis:8.2-alpine")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build(), [6379]);
    public ValueTask DisposeAsync() => _container?.DisposeAsync() ?? ValueTask.CompletedTask;
}

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<StreamReferenceRedisContainer>(Shared = SharedType.PerTestSession)]
public class StreamReferenceIntegrationTests(StreamReferenceRedisContainer fixture)
{
    [Test]
    [MatrixDataSource]
    public async Task DeletionPreservesPerIdResultsAndGroupReferences(
        [Matrix(2, 3)] int protocol, [Matrix(0, 1, 2)] int surface,
        [Matrix(RespireStreamReferencePolicy.KeepReferences, RespireStreamReferencePolicy.DeleteReferences, RespireStreamReferencePolicy.Acknowledged)] RespireStreamReferencePolicy policy,
        [Matrix(false, true)] bool acknowledge)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespireProtocol)protocol });
        var view = client.WithKeyPrefix($"references:{Guid.NewGuid():N}:");
        try
        {
            await SeedAsync(view, 2);
            await view.Streams.AcknowledgeAsync("events", "b", "1-0");
            if (!acknowledge) await view.Streams.AcknowledgeAsync("events", "a", "1-0");
            RespireStreamId[] ids = ["1-0", "1-0", "0-1", "2-0"];
            RespireStreamDeletionResult[] results;
            if (surface == 0)
                results = acknowledge
                    ? await view.Streams.AcknowledgeAndRemoveAsync("events", "a", policy, ids)
                    : await view.Streams.RemoveAsync("events", policy, ids);
            else
            {
                using var batch = surface == 1 ? view.CreateBatch() : null;
                await using var transaction = surface == 2 ? view.CreateTransaction() : null;
                IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
                var pending = acknowledge ? queue.Streams.AcknowledgeAndRemove("events", "a", policy, ids)
                    : queue.Streams.Remove("events", policy, ids);
                if (transaction is not null) await transaction.CommitAsync();
                else await batch!.ExecuteAsync();
                results = pending.Result;
            }
            var retained = policy == RespireStreamReferencePolicy.Acknowledged;
            results.Should().Equal(RespireStreamDeletionResult.Deleted, RespireStreamDeletionResult.NotFound,
                RespireStreamDeletionResult.NotFound, retained ? RespireStreamDeletionResult.Retained : RespireStreamDeletionResult.Deleted);
            (await view.Streams.CountAsync("events")).Should().Be(retained ? 1 : 0);
            (await view.Streams.PendingSummaryAsync("events", "a")).Count.Should()
                .Be(acknowledge || policy == RespireStreamReferencePolicy.DeleteReferences ? 0 : 1);
            (await view.Streams.PendingSummaryAsync("events", "b")).Count.Should()
                .Be(policy == RespireStreamReferencePolicy.DeleteReferences ? 0 : 1);
            if (retained)
            {
                await view.Streams.AcknowledgeAsync("events", "a", "2-0");
                await view.Streams.AcknowledgeAsync("events", "b", "2-0");
                (await view.Streams.RemoveAsync("events", policy, "2-0")).Should().Equal(RespireStreamDeletionResult.Deleted);
            }
        }
        finally { await view.Keys.DeleteAsync("events"); }
    }

    [Test]
    [MatrixDataSource]
    public async Task TrimmingAppliesReferencePolicyOnAllSurfaces(
        [Matrix(2, 3)] int protocol, [Matrix(0, 1, 2)] int surface,
        [Matrix(RespireStreamReferencePolicy.KeepReferences, RespireStreamReferencePolicy.DeleteReferences, RespireStreamReferencePolicy.Acknowledged)] RespireStreamReferencePolicy policy,
        [Matrix(false, true)] bool append)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespireProtocol)protocol });
        var view = client.WithKeyPrefix($"reference-trim:{Guid.NewGuid():N}:");
        try
        {
            await SeedAsync(view, 3);
            await view.Streams.AcknowledgeAsync("events", "a", "1-0", "2-0");
            await view.Streams.AcknowledgeAsync("events", "b", "1-0");
            var add = new RespireStreamAddOptions { Id = "4-0", MinId = "4-0", ApproximateTrim = false, ReferencePolicy = policy };
            var trim = new RespireStreamTrimOptions { MaxLength = 0, ReferencePolicy = policy };
            long removed = 0;
            if (surface == 0)
            {
                if (append) (await view.Streams.AddAsync("events", add, ("f", "v"))).Should().Be((RespireStreamId)"4-0");
                else removed = await view.Streams.TrimAsync("events", trim);
            }
            else
            {
                using var batch = surface == 1 ? view.CreateBatch() : null;
                await using var transaction = surface == 2 ? view.CreateTransaction() : null;
                IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
                var added = append ? queue.Streams.Add("events", add, ("f", "v")) : null;
                var trimmed = append ? null : queue.Streams.Trim("events", trim);
                if (transaction is not null) await transaction.CommitAsync();
                else await batch!.ExecuteAsync();
                if (append) added!.Result.Should().Be((RespireStreamId)"4-0");
                else removed = trimmed!.Result;
            }
            var acknowledgedOnly = policy == RespireStreamReferencePolicy.Acknowledged;
            if (!append) removed.Should().Be(acknowledgedOnly ? 1 : 3);
            (await view.Streams.CountAsync("events")).Should().Be((acknowledgedOnly ? 2 : 0) + (append ? 1 : 0));
            (await view.Streams.PendingSummaryAsync("events", "a")).Count.Should().Be(policy == RespireStreamReferencePolicy.DeleteReferences ? 0 : 1);
            (await view.Streams.PendingSummaryAsync("events", "b")).Count.Should().Be(policy == RespireStreamReferencePolicy.DeleteReferences ? 0 : 2);
        }
        finally { await view.Keys.DeleteAsync("events"); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MissingPendingEntriesAndDanglingReferencesRemainDistinct(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespireProtocol)protocol });
        var view = client.WithKeyPrefix($"reference-missing:{Guid.NewGuid():N}:");
        try
        {
            (await view.Streams.RemoveAsync("events", RespireStreamReferencePolicy.KeepReferences, "1-0")).Should().Equal(RespireStreamDeletionResult.NotFound);
            (await view.Streams.AcknowledgeAndRemoveAsync("events", "a", RespireStreamReferencePolicy.KeepReferences, "1-0")).Should().Equal(RespireStreamDeletionResult.NotFound);
            await SeedAsync(view, 2);
            (await view.Streams.AcknowledgeAndRemoveAsync("events", "absent", RespireStreamReferencePolicy.DeleteReferences, "1-0")).Should().Equal(RespireStreamDeletionResult.NotFound);
            await view.Streams.AcknowledgeAsync("events", "a", "1-0");
            (await view.Streams.AcknowledgeAndRemoveAsync("events", "a", RespireStreamReferencePolicy.DeleteReferences, "1-0")).Should().Equal(RespireStreamDeletionResult.NotFound);
            (await view.Streams.CountAsync("events")).Should().Be(2);
            await view.Streams.RemoveAsync("events", "1-0", "2-0");
            (await view.Streams.RemoveAsync("events", RespireStreamReferencePolicy.DeleteReferences, "1-0")).Should().Equal(RespireStreamDeletionResult.NotFound);
            (await view.Streams.PendingSummaryAsync("events", "b")).Count.Should().Be(1);
            (await view.Streams.AcknowledgeAndRemoveAsync("events", "a", RespireStreamReferencePolicy.DeleteReferences, "2-0")).Should().Equal(RespireStreamDeletionResult.Deleted);
            (await view.Streams.PendingSummaryAsync("events", "b")).Count.Should().Be(0);
        }
        finally { await view.Keys.DeleteAsync("events"); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AcknowledgedPolicyAllowsRemovalWhenNoGroupsExist(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespireProtocol)protocol });
        var key = $"reference-no-groups:{Guid.NewGuid():N}";
        try
        {
            await client.Streams.AddAsync(key, new RespireStreamAddOptions { Id = "1-0" }, ("f", "v"));
            (await client.Streams.RemoveAsync(key, RespireStreamReferencePolicy.Acknowledged, "1-0"))
                .Should().Equal(RespireStreamDeletionResult.Deleted);
            await client.Streams.AddAsync(key, new RespireStreamAddOptions { Id = "2-0" }, ("f", "v"));
            (await client.Streams.TrimAsync(key, new() { MaxLength = 0, ReferencePolicy = RespireStreamReferencePolicy.Acknowledged }))
                .Should().Be(1);
        }
        finally { await client.Keys.DeleteAsync(key); }
    }

    private static async Task SeedAsync(IRespireClient view, int count)
    {
        for (var id = 1; id <= count; id++)
            await view.Streams.AddAsync("events", new RespireStreamAddOptions { Id = $"{id}-0" }, ("f", "v"));
        foreach (var group in new[] { "a", "b" })
        {
            await view.Streams.CreateGroupAsync("events", group, RespireStreamId.Beginning);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = 0;
            await foreach (var entry in view.Streams.ReadGroupAsync("events", group, "consumer", cancellationToken: timeout.Token))
                if (++received == count) break;
        }
    }
}

[ClassDataSource<Redis7ReferenceContainer>(Shared = SharedType.PerTestSession)]
public class LegacyStreamReferenceIntegrationTests(Redis7ReferenceContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task UnsupportedOptionsDoNotSilentlyFallBackOrReplay(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespireProtocol)protocol });
        var key = $"legacy-references:{Guid.NewGuid():N}";
        try
        {
            await client.Streams.AddAsync(key, new RespireStreamAddOptions { Id = "1-0" }, ("f", "v"));
            Func<Task>[] unsupported =
            [
                async () => await client.Streams.RemoveAsync(key, RespireStreamReferencePolicy.KeepReferences, "1-0"),
                async () => await client.Streams.AcknowledgeAndRemoveAsync(key, "g", RespireStreamReferencePolicy.DeleteReferences, "1-0"),
                async () => await client.Streams.AddAsync(key, new RespireStreamAddOptions { ReferencePolicy = RespireStreamReferencePolicy.KeepReferences }, ("f", "v")),
                async () => await client.Streams.TrimAsync(key, new() { MaxLength = 0, ReferencePolicy = RespireStreamReferencePolicy.KeepReferences }),
            ];
            foreach (var action in unsupported) await action.Should().ThrowAsync<RespireServerException>();
            (await client.Streams.CountAsync(key)).Should().Be(1);
        }
        finally { await client.Keys.DeleteAsync(key); }
    }
}
