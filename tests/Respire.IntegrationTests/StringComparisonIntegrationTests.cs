using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class StringComparisonIntegrationTests(VersionedServerFixture servers)
{
    private enum Mode { Immediate, Batch, Transaction }

    [Test]
    [Arguments("redis:8.4-alpine", 2)]
    [Arguments("redis:8.4-alpine", 3)]
    [Arguments("valkey/valkey:8.1-alpine", 2)]
    [Arguments("valkey/valkey:8.1-alpine", 3)]
    [Arguments("valkey/valkey:9.0-alpine", 2)]
    [Arguments("valkey/valkey:9.0-alpine", 3)]
    public async Task SupportedComparisons_PreserveAtomicOutcomesAndExpiry(string image, int protocol)
    {
        var server = await servers.LeaseAsync(image);
        await using var client = await RespireClient.ConnectAsync(server.ConnectionString(protocol));
        var redis = image.StartsWith("redis:", StringComparison.Ordinal);
        var valkeyDelete = image.Contains(":9.0", StringComparison.Ordinal);
        foreach (var mode in Enum.GetValues<Mode>())
        {
            var view = client.WithKeyPrefix($"{mode}:");
            byte[] old = [0xff, 0, 1];
            byte[] next = [0, 0xfe];
            RespireKey key = new byte[] { 0xff, 0, 0x42 };
            await view.SetAsync(key, old);
            var digest = redis ? await Digest(view, mode, key) : null;
            if (redis) digest.Should().HaveLength(16);
            var conditions = new List<(RespireValueCondition Match, RespireValueCondition Mismatch, bool CreatesMissing)>
            {
                (RespireValueCondition.EqualTo(old), RespireValueCondition.EqualTo(next), false),
            };
            if (redis)
            {
                conditions.Add((RespireValueCondition.NotEqualTo(next), RespireValueCondition.NotEqualTo(old), true));
                conditions.Add((RespireValueCondition.DigestEqualTo(digest!), RespireValueCondition.DigestEqualTo("0000000000000000"), false));
                conditions.Add((RespireValueCondition.DigestNotEqualTo("0000000000000000"), RespireValueCondition.DigestNotEqualTo(digest!), true));
            }
            foreach (var (match, mismatch, createsMissing) in conditions)
            {
                await view.SetAsync(key, old, expiry: TimeSpan.FromMinutes(5));
                (await Set(view, mode, key, next, mismatch, TimeSpan.FromSeconds(1))).Should().BeFalse();
                (await view.GetAsync<byte[]>(key)).Should().Equal(old);
                (await view.Keys.ExpiryAsync(key)).TimeToLive.Should().BeGreaterThan(TimeSpan.FromMinutes(4));
                (await GetSet(view, mode, key, next, mismatch)).Should().Equal(old);
                (await view.GetAsync<byte[]>(key)).Should().Equal(old);
                (await view.Keys.ExpiryAsync(key)).HasExpiry.Should().BeTrue();
                (await Set(view, mode, key, next, match, RespireExpiry.Keep)).Should().BeTrue();
                (await view.GetAsync<byte[]>(key)).Should().Equal(next);
                (await view.Keys.ExpiryAsync(key)).HasExpiry.Should().BeTrue();
                await view.SetAsync(key, old);
                (await Set(view, mode, key, next, match, RespireExpiry.At(DateTimeOffset.UtcNow.AddMinutes(5)))).Should().BeTrue();
                (await view.Keys.ExpiryAsync(key)).TimeToLive.Should().BeGreaterThan(TimeSpan.FromMinutes(4));
                await view.SetAsync(key, old);
                (await GetSet(view, mode, key, next, match)).Should().Equal(old);
                (await view.GetAsync<byte[]>(key)).Should().Equal(next);
                (await view.Keys.ExpiryAsync(key)).HasExpiry.Should().BeFalse();
                await view.Keys.DeleteAsync(key);
                (await Set(view, mode, key, next, match)).Should().Be(createsMissing);
                await view.Keys.DeleteAsync(key);
                (await GetSet(view, mode, key, next, match)).Should().BeNull();
                (await view.Keys.ExistsAsync(key)).Should().Be(createsMissing);
                await view.Keys.DeleteAsync(key);
                await view.Lists.RightPushAsync(key, "list");
                await ServerError(async () => { await Set(view, mode, key, next, match); }, "*WRONGTYPE*");
                await ServerError(async () => { await GetSet(view, mode, key, next, match); }, "*WRONGTYPE*");
                if (redis) await ServerError(async () => { await Delete(view, mode, key, match); }, "ERR Key should be of string type if conditions are specified");
                await view.Keys.DeleteAsync(key);
                if (redis)
                {
                    (await Delete(view, mode, key, match)).Should().BeFalse();
                    await view.SetAsync(key, old);
                    (await Delete(view, mode, key, mismatch)).Should().BeFalse();
                    (await view.GetAsync<byte[]>(key)).Should().Equal(old);
                    (await Delete(view, mode, key, match)).Should().BeTrue();
                    (await view.Keys.ExistsAsync(key)).Should().BeFalse();
                }
            }
            var empty = RespireValueCondition.EqualTo("");
            await view.SetAsync("", "");
            (await Set(view, mode, "", "", empty)).Should().BeTrue();
            await view.SetAsync(key, old, expiry: TimeSpan.FromMinutes(5));
            (await Set(view, mode, key, next, RespireValueCondition.EqualTo(old))).Should().BeTrue();
            (await view.Keys.ExpiryAsync(key)).HasExpiry.Should().BeFalse();
            await view.SetAsync(key, old);
            (await Set(view, mode, key, next, RespireValueCondition.EqualTo(old), TimeSpan.FromMinutes(5))).Should().BeTrue();
            (await view.Keys.ExpiryAsync(key)).TimeToLive.Should().BeGreaterThan(TimeSpan.FromMinutes(4));
            // RespireExpiry truncates to milliseconds, matching ordinary SET. PX 0 remains a server error.
            await ServerError(async () => { await Set(view, mode, key, old, RespireValueCondition.EqualTo(next), TimeSpan.FromTicks(1)); }, "*invalid expire time*");
            (await view.GetAsync<byte[]>(key)).Should().Equal(next);
            (await view.Keys.ExpiryAsync(key)).TimeToLive.Should().BeGreaterThan(TimeSpan.FromMinutes(4));
            if (redis)
            {
                (await Digest(view, mode, "missing")).Should().BeNull();
                await view.Lists.RightPushAsync("wrong", "list");
                await ServerError(async () => { await Digest(view, mode, "wrong"); }, "*WRONGTYPE*");
                await ServerError(async () => { await Set(view, mode, key, next, RespireValueCondition.DigestEqualTo("invalid")); });
                await ServerError(async () => { await Delete(view, mode, key, RespireValueCondition.DigestEqualTo("invalid")); });
            }
            if (valkeyDelete)
            {
                await view.SetAsync(key, old);
                (await DeleteEqual(view, mode, key, next)).Should().BeFalse();
                (await DeleteEqual(view, mode, key, old)).Should().BeTrue();
                (await DeleteEqual(view, mode, key, old)).Should().BeFalse();
                await view.Lists.RightPushAsync(key, "list");
                await ServerError(async () => { await DeleteEqual(view, mode, key, old); }, "*WRONGTYPE*");
                await view.Keys.DeleteAsync(key);
                await view.SetAsync("", "");
                (await DeleteEqual(view, mode, "", "")).Should().BeTrue();
            }
        }
    }

    [Test]
    [Arguments("redis:7.0.15", 2)]
    [Arguments("redis:7.0.15", 3)]
    [Arguments("valkey/valkey:8.1-alpine", 2)]
    [Arguments("valkey/valkey:8.1-alpine", 3)]
    public async Task UnsupportedFeatures_RemainServerErrors(string image, int protocol)
    {
        var server = await servers.LeaseAsync(image);
        await using var client = await RespireClient.ConnectAsync(server.ConnectionString(protocol));
        foreach (var mode in Enum.GetValues<Mode>())
        {
            await client.SetAsync("key", "old");
            var conditions = new List<RespireValueCondition>
            {
                RespireValueCondition.NotEqualTo("different"), RespireValueCondition.DigestEqualTo("0000000000000000"),
                RespireValueCondition.DigestNotEqualTo("0000000000000000"),
            };
            if (image.StartsWith("redis:", StringComparison.Ordinal)) conditions.Add(RespireValueCondition.EqualTo("old"));
            foreach (var condition in conditions)
                await ServerError(async () => { await Set(client, mode, "key", "new", condition); });
            await ServerError(async () => { await Delete(client, mode, "key", RespireValueCondition.EqualTo("old")); });
            await ServerError(async () => { await DeleteEqual(client, mode, "key", "old"); });
            await ServerError(async () => { await Digest(client, mode, "key"); });
            (await client.GetStringAsync("key")).Should().Be("old");
        }
    }

    private static Task<bool> Set(IRespireClient client, Mode mode, RespireKey key, RespireValue value, RespireValueCondition condition, RespireExpiry expiry = default)
        => Execute(client, mode, () => client.Strings.SetConditionalAsync(key, value, condition, expiry), s => s.SetConditional(key, value, condition, expiry));
    private static Task<byte[]?> GetSet(IRespireClient client, Mode mode, RespireKey key, byte[] value, RespireValueCondition condition)
        => Execute(client, mode, () => client.Strings.GetAndSetConditionalAsync<byte[]>(key, value, condition), s => s.GetAndSetConditional<byte[]>(key, value, condition));
    private static Task<bool> Delete(IRespireClient client, Mode mode, RespireKey key, RespireValueCondition condition)
        => Execute(client, mode, () => client.Strings.DeleteConditionalAsync(key, condition), s => s.DeleteConditional(key, condition));
    private static Task<bool> DeleteEqual(IRespireClient client, Mode mode, RespireKey key, RespireValue value)
        => Execute(client, mode, () => client.Strings.DeleteIfEqualAsync(key, value), s => s.DeleteIfEqual(key, value));
    private static Task<string?> Digest(IRespireClient client, Mode mode, RespireKey key)
        => Execute(client, mode, () => client.Strings.DigestAsync(key), s => s.Digest(key));

    private static async Task<T> Execute<T>(IRespireClient client, Mode mode, Func<ValueTask<T>> immediate, Func<IBatchStringCommands, RespirePending<T>> enqueue)
    {
        if (mode == Mode.Immediate) return await immediate();
        if (mode == Mode.Batch)
        {
            using var batch = client.CreateBatch();
            var pending = enqueue(batch.Strings);
            await batch.TryExecuteAsync();
            return pending.Result;
        }
        await using var transaction = client.CreateTransaction();
        var result = enqueue(transaction.Strings);
        await transaction.CommitAsync();
        return result.Result;
    }

    private static async Task ServerError(Func<Task> command, string message = "*")
        => await command.Should().ThrowAsync<RespireServerException>().WithMessage(message);
}
