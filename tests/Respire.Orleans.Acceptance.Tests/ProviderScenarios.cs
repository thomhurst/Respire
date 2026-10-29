using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Clustering.Redis;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Persistence;
using Orleans.Persistence.Redis;
using Orleans.Reminders.Redis;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using Respire.StackExchangeCompat;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.Orleans.Acceptance.Tests;

// Named scenarios from Orleans v10.2.2; see NOTICE.md for source and adaptations.
// Both clients must complete the same provider operations successfully.
[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ProviderScenarios(RedisTestContainer redis)
{
    [Test]
    public Task Membership_ReadRow_Insert_Read() => Compare(async connection =>
    {
        await using var table = Membership(connection);
        await table.InitializeMembershipTable(true);
        var data = await table.ReadAll();
        Assert.Empty(data.Members);
        var entry = Entry(1);
        var version = data.Version.Next();
        Assert.True(await table.InsertRow(entry, version));
        Assert.False(await table.InsertRow(entry, version));
        Assert.False(await table.InsertRow(Entry(2), version));
        Assert.False(await table.InsertRow(entry, (await table.ReadAll()).Version.Next()));
        data = await table.ReadRow(entry.SiloAddress);
        Assert.Single(data.Members);
        Assert.Equal(version.Version, data.Version.Version);
        Assert.Equal(entry.SiloAddress, data.Members[0].Item1.SiloAddress);
        Assert.NotNull(data.Members[0].Item2);
    });

    [Test]
    public Task Membership_UpdateRow_UpdateIAmAlive_Cleanup() => Compare(async connection =>
    {
        await using var table = Membership(connection);
        await table.InitializeMembershipTable(true);
        var entry = Entry(1);
        var version = (await table.ReadAll()).Version.Next();
        Assert.True(await table.InsertRow(entry, version));
        var data = await table.ReadRow(entry.SiloAddress);
        Assert.False(await table.UpdateRow(entry, data.Members[0].Item2, version));
        entry.Status = SiloStatus.Active;
        Assert.True(await table.UpdateRow(entry, data.Members[0].Item2, version.Next()));
        var heartbeat = entry.IAmAliveTime.AddSeconds(10);
        await table.UpdateIAmAlive(new MembershipEntry { SiloAddress = entry.SiloAddress, IAmAliveTime = heartbeat });
        data = await table.ReadAll();
        Assert.Equal(heartbeat, Assert.Single(data.Members).Item1.IAmAliveTime);
        Assert.Equal(version.Next().Version, data.Version.Version);
        var dead = Entry(2);
        dead.Status = SiloStatus.Dead;
        dead.StartTime = dead.IAmAliveTime = DateTime.UtcNow.AddDays(-10);
        Assert.True(await table.InsertRow(dead, data.Version.Next()));
        await table.CleanupDefunctSiloEntries(DateTimeOffset.UtcNow.AddDays(-7));
        Assert.Equal(entry.SiloAddress, Assert.Single((await table.ReadAll()).Members).Item1.SiloAddress);
        await table.DeleteMembershipTableEntries("unused");
        Assert.Empty((await table.ReadAll()).Members);
    });

    [Test]
    public Task Membership_GetGateways() => Compare(async connection =>
    {
        await using var table = Membership(connection);
        await table.InitializeMembershipTable(true);
        var entries = Enumerable.Range(0, 10).Select(Entry).ToArray();
        entries[3].Status = entries[5].Status = entries[9].Status = SiloStatus.Active;
        entries[3].ProxyPort = 0;
        var version = (await table.ReadAll()).Version;
        foreach (var entry in entries)
        {
            Assert.True(await table.InsertRow(entry, version.Next()));
            version = (await table.ReadRow(entry.SiloAddress)).Version;
        }
        var gateways = await new RedisGatewayListProvider(table, Options.Create(new GatewayOptions())).GetGateways();
        Assert.Equal(2, gateways.Count);
        Assert.Contains(entries[5].SiloAddress.ToGatewayUri(), gateways);
        Assert.Contains(entries[9].SiloAddress.ToGatewayUri(), gateways);
    });

    [Test]
    public Task Storage_WriteRead() => Compare(async connection =>
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        await using var storage = await Storage(connection, services, deleteStateOnClear: false);
        var grainId = GrainId.Create("assessment", "state");
        var state = new GrainState<int> { State = 42 };
        await storage.WriteStateAsync("integer", grainId, state);
        var read = new GrainState<int>();
        await storage.ReadStateAsync("integer", grainId, read);
        Assert.Equal(state.ETag, read.ETag);
        Assert.Equal(state.State, read.State);
        Assert.True(read.RecordExists);
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task Storage_Clear(bool deleteStateOnClear) => Compare(async connection =>
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        await using var storage = await Storage(connection, services, deleteStateOnClear);
        var grainId = GrainId.Create("assessment", "state");
        var state = new GrainState<int> { State = 42 };
        await storage.WriteStateAsync("integer", grainId, state);
        var writtenETag = state.ETag;
        await storage.ClearStateAsync("integer", grainId, state);
        Assert.NotEqual(writtenETag, state.ETag);
        Assert.False(state.RecordExists);
        var read = new GrainState<int>();
        await storage.ReadStateAsync("integer", grainId, read);
        Assert.False(read.RecordExists);
        Assert.Equal(0, read.State);
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task Storage_StaleETagRejectsWriteAndClear(bool deleteStateOnClear) => Compare(async connection =>
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        await using var storage = await Storage(connection, services, deleteStateOnClear);
        var grainId = GrainId.Create("acceptance", "conflict");
        var current = new GrainState<int> { State = 42 };
        await storage.WriteStateAsync("integer", grainId, current);
        await Assert.ThrowsAsync<InconsistentStateException>(() => storage.WriteStateAsync("integer", grainId, new GrainState<int> { State = 99 }));
        var stale = new GrainState<int> { State = 99, ETag = current.ETag };
        current.State = 43;
        await storage.WriteStateAsync("integer", grainId, current);
        await Assert.ThrowsAsync<InconsistentStateException>(() => storage.WriteStateAsync("integer", grainId, stale));
        await Assert.ThrowsAsync<InconsistentStateException>(() => storage.ClearStateAsync("integer", grainId, stale));
        var read = new GrainState<int>();
        await storage.ReadStateAsync("integer", grainId, read);
        Assert.Equal(43, read.State);
        Assert.Equal(current.ETag, read.ETag);
        await storage.ClearStateAsync("integer", grainId, current);
        current.State = 44;
        await storage.WriteStateAsync("integer", grainId, current);
        await storage.ReadStateAsync("integer", grainId, read);
        Assert.Equal(44, read.State);
    });

    [Test]
    public Task Storage_ConcurrentWritesHaveOneWinner() => Compare(async connection =>
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        await using var storage = await Storage(connection, services, deleteStateOnClear: false);
        var grainId = GrainId.Create("acceptance", "concurrent");
        var original = new GrainState<int> { State = 1 };
        await storage.WriteStateAsync("integer", grainId, original);
        var contenders = Enumerable.Range(2, 16).Select(value => new GrainState<int> { State = value, ETag = original.ETag }).ToArray();
        var outcomes = await Task.WhenAll(contenders.Select(async state =>
        {
            try { await storage.WriteStateAsync("integer", grainId, state); return true; }
            catch (InconsistentStateException) { return false; }
        }));
        Assert.Equal(1, outcomes.Count(static success => success));
        var winner = contenders[Array.IndexOf(outcomes, true)];
        var read = new GrainState<int>();
        await storage.ReadStateAsync("integer", grainId, read);
        Assert.Equal(winner.State, read.State);
        Assert.Equal(winner.ETag, read.ETag);
    });

    [Test]
    public Task Storage_ExpiryIsAppliedAtomically() => Compare(async connection =>
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        await using var storage = await Storage(connection, services, deleteStateOnClear: false, TimeSpan.FromMinutes(1));
        var grainId = GrainId.Create("acceptance", "expiry");
        var state = new GrainState<byte[]> { State = [0xff, 0, 0x80] };
        await storage.WriteStateAsync("bytes", grainId, state);
        var key = $"{_cluster.ServiceId}/state/{grainId}/bytes";
        var ttl = await connection.GetDatabase().KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value.TotalSeconds, 1, 60);
        var read = new GrainState<byte[]>();
        await storage.ReadStateAsync("bytes", grainId, read);
        Assert.Equal(state.State, read.State);
        Assert.Equal(state.ETag, read.ETag);
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task Storage_BinaryStateInteroperatesWithOfficialClient(bool deleteStateOnClear) => Compare(async connection =>
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        await using var storage = await Storage(connection, services, deleteStateOnClear);
        await using var other = await Storage(Other(connection), services, deleteStateOnClear);
        var grainId = GrainId.Create("acceptance", "binary");
        // A list can be activated after clear; the upstream provider cannot activate byte[].
        var state = new GrainState<List<byte>> { State = [0, 0xff, 0x80, 13, 10, 0] };
        await storage.WriteStateAsync("bytes", grainId, state);
        var read = new GrainState<List<byte>>();
        await other.ReadStateAsync("bytes", grainId, read);
        Assert.Equal(state.State, read.State);
        Assert.Equal(state.ETag, read.ETag);
        read.State = [0xfe, 1, 0];
        await other.WriteStateAsync("bytes", grainId, read);
        await Assert.ThrowsAsync<InconsistentStateException>(() => storage.ClearStateAsync("bytes", grainId, state));
        await storage.ReadStateAsync("bytes", grainId, state);
        Assert.Equal(read.State, state.State);
        Assert.Equal(read.ETag, state.ETag);
        await storage.ClearStateAsync("bytes", grainId, state);
        await other.ReadStateAsync("bytes", grainId, read);
        Assert.False(read.RecordExists);
    });

    [Test]
    public Task Reminders_ReminderSimple() => Compare(async connection =>
    {
        await using var table = Reminders(connection);
        await table.Init();
        var reminder = Reminder();
        await table.UpsertRow(reminder);
        var read = await table.ReadRow(reminder.GrainId, reminder.ReminderName);
        var oldETag = reminder.ETag = read.ETag;
        Assert.Equal(reminder.GrainId, read.GrainId);
        Assert.Equal(reminder.Period, read.Period);
        Assert.Equal(reminder.ReminderName, read.ReminderName);
        Assert.Equal(reminder.StartAt, read.StartAt);
        reminder.StartAt = reminder.StartAt.AddSeconds(1);
        reminder.ETag = await table.UpsertRow(reminder);
        Assert.False(await table.RemoveRow(reminder.GrainId, reminder.ReminderName, oldETag));
        Assert.False(await table.RemoveRow(reminder.GrainId, "bla", reminder.ETag));
        Assert.True(await table.RemoveRow(reminder.GrainId, reminder.ReminderName, reminder.ETag));
        Assert.False(await table.RemoveRow(reminder.GrainId, reminder.ReminderName, reminder.ETag));
    });

    [Test]
    public Task Reminders_ReadRows() => Compare(async connection =>
    {
        await using var table = Reminders(connection);
        await table.Init();
        var reminder = Reminder();
        await table.UpsertRow(reminder);
        Assert.Equal(reminder.ReminderName, Assert.Single((await table.ReadRows(reminder.GrainId)).Reminders).ReminderName);
        Assert.Single((await table.ReadRows(0, uint.MaxValue)).Reminders);
        Assert.Single((await table.ReadRows(0, 0)).Reminders);
    });

    [Test]
    public Task Reminders_RemoveRow() => Compare(async connection =>
    {
        await using var table = Reminders(connection);
        await table.Init();
        var reminder = Reminder();
        reminder.ETag = await table.UpsertRow(reminder);
        Assert.False(await table.RemoveRow(reminder.GrainId, reminder.ReminderName, "stale"));
        Assert.True(await table.RemoveRow(reminder.GrainId, reminder.ReminderName, reminder.ETag));
        Assert.False(await table.RemoveRow(reminder.GrainId, reminder.ReminderName, reminder.ETag));
    });

    [Test]
    public Task Reminders_InteroperateWithOfficialClient() => Compare(async connection =>
    {
        await using var table = Reminders(connection);
        await using var other = Reminders(Other(connection));
        await table.Init();
        await other.Init();
        var reminder = Reminder();
        var oldETag = await table.UpsertRow(reminder);
        var read = await other.ReadRow(reminder.GrainId, reminder.ReminderName);
        Assert.Equal(oldETag, read.ETag);
        Assert.Equal(reminder.StartAt, read.StartAt);
        read.Period = TimeSpan.FromMinutes(3);
        var newETag = await other.UpsertRow(read);
        Assert.False(await table.RemoveRow(reminder.GrainId, reminder.ReminderName, oldETag));
        read = await table.ReadRow(reminder.GrainId, reminder.ReminderName);
        Assert.Equal(newETag, read.ETag);
        Assert.Equal(TimeSpan.FromMinutes(3), read.Period);
        Assert.True(await table.RemoveRow(reminder.GrainId, reminder.ReminderName, newETag));
        Assert.Null(await other.ReadRow(reminder.GrainId, reminder.ReminderName));
    });

    [Test]
    public Task Reminders_RangePartitionsAndGrainFilters() => Compare(async connection =>
    {
        await using var table = Reminders(connection);
        await table.Init();
        var reminders = Enumerable.Range(0, 32).Select(index => new ReminderEntry
        {
            GrainId = GrainId.Create("acceptance", $"grain-{index}"),
            ReminderName = "first",
            StartAt = Reminder().StartAt,
            Period = TimeSpan.FromMinutes(1)
        }).ToArray();
        foreach (var reminder in reminders) await table.UpsertRow(reminder);
        var second = Reminder();
        second.GrainId = reminders[0].GrainId;
        second.ReminderName = "second";
        await table.UpsertRow(second);
        Assert.Equal(new[] { "first", "second" }, (await table.ReadRows(second.GrainId)).Reminders.Select(static row => row.ReminderName).Order().ToArray());
        Assert.Empty((await table.ReadRows(GrainId.Create("acceptance", "missing"))).Reminders);
        var all = reminders.Append(second).ToArray();
        foreach (var (begin, end) in new[]
        {
            (0u, uint.MaxValue / 2), (uint.MaxValue / 2, uint.MaxValue),
            (uint.MaxValue / 4, uint.MaxValue / 4 * 3), (uint.MaxValue / 4 * 3, uint.MaxValue / 4),
            (reminders[0].GrainId.GetUniformHashCode(), reminders[1].GrainId.GetUniformHashCode()), (0u, 0u)
        })
        {
            var expected = all.Where(row => begin < end
                ? row.GrainId.GetUniformHashCode() > begin && row.GrainId.GetUniformHashCode() <= end
                : row.GrainId.GetUniformHashCode() > begin || row.GrainId.GetUniformHashCode() <= end);
            Assert.NotEmpty(expected);
            var actual = (await table.ReadRows(begin, end)).Reminders;
            Assert.Equal(expected.Select(static row => (row.GrainId.ToString(), row.ReminderName)).Order().ToArray(),
                actual.Select(static row => (row.GrainId.ToString(), row.ReminderName)).Order().ToArray());
        }
    });

    [Test]
    public Task Reminders_ConcurrentUpsertsLeaveOneReplacement() => Compare(async connection =>
    {
        await using var table = Reminders(connection);
        await table.Init();
        var reminder = Reminder();
        var oldETag = await table.UpsertRow(reminder);
        var tags = await Task.WhenAll(Enumerable.Range(1, 16).Select(_ => table.UpsertRow(Reminder())));
        var read = Assert.Single((await table.ReadRows(reminder.GrainId)).Reminders);
        Assert.Contains(read.ETag, tags);
        Assert.False(await table.RemoveRow(reminder.GrainId, reminder.ReminderName, oldETag));
        foreach (var tag in tags.Where(tag => tag != read.ETag))
            Assert.False(await table.RemoveRow(reminder.GrainId, reminder.ReminderName, tag));
        Assert.True(await table.RemoveRow(reminder.GrainId, reminder.ReminderName, read.ETag));
    });

    private ConnectionMultiplexer? _control;
    private RespireConnectionMultiplexer? _shim;
    private ClusterOptions _cluster = null!;

    private IConnectionMultiplexer Other(IConnectionMultiplexer connection) => ReferenceEquals(connection, _control) ? _shim! : _control!;

    private async Task Compare(Func<IConnectionMultiplexer, Task> scenario)
    {
        var shimOptions = RespireOptions.Parse(redis.IsolatedConnectionString) with { Connections = 1 };
        var controlOptions = ConfigurationOptions.Parse(redis.IsolatedStackExchangeConnectionString);
        controlOptions.Protocol = shimOptions.Protocol switch
        {
            RespireProtocol.Resp2 => RedisProtocol.Resp2,
            RespireProtocol.Resp3 => RedisProtocol.Resp3,
            _ => null
        };
        await using var shim = RespireConnectionMultiplexer.Create(shimOptions);
        using var control = await ConnectionMultiplexer.ConnectAsync(controlOptions);
        if (controlOptions.Protocol is not null)
        {
            var clientInfo = (string)(await control.GetDatabase().ExecuteAsync("CLIENT", "INFO"))!;
            Assert.Contains($"resp={(int)shimOptions.Protocol}", clientInfo.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        _control = control;
        _shim = shim;
        foreach (var connection in new IConnectionMultiplexer[] { control, shim })
        {
            _cluster = new ClusterOptions { ServiceId = Guid.NewGuid().ToString("N"), ClusterId = Guid.NewGuid().ToString("N") };
            await scenario(connection);
        }
    }

    private RedisMembershipTable Membership(IConnectionMultiplexer connection) => new(
        Options.Create(new RedisClusteringOptions
        {
            CreateMultiplexer = _ => Task.FromResult((connection, true)),
            EntryExpiry = TimeSpan.FromHours(1)
        }), Options.Create(_cluster));

    private RedisReminderTable Reminders(IConnectionMultiplexer connection) => new(
        NullLogger<RedisReminderTable>.Instance, Options.Create(_cluster),
        Options.Create(new RedisReminderTableOptions
        {
            CreateMultiplexer = _ => Task.FromResult((connection, true)),
            EntryExpiry = TimeSpan.FromHours(1)
        }));

    private async Task<RedisGrainStorage> Storage(IConnectionMultiplexer connection, IServiceProvider services, bool deleteStateOnClear, TimeSpan? expiry = null)
    {
        var storage = new RedisGrainStorage("assessment", new RedisStorageOptions
        {
            CreateMultiplexer = _ => Task.FromResult((connection, true)),
            DeleteStateOnClear = deleteStateOnClear,
            EntryExpiry = expiry
        }, new OrleansGrainStorageSerializer(services.GetRequiredService<Serializer>()), Options.Create(_cluster),
            services.GetRequiredService<IActivatorProvider>(), NullLogger<RedisGrainStorage>.Instance);
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        storage.Participate(lifecycle);
        await lifecycle.OnStart(CancellationToken.None);
        return storage;
    }

    private static MembershipEntry Entry(int index) => new()
    {
        SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11000 + index), index + 1),
        ProxyPort = 11000 + index,
        HostName = "localhost",
        SiloName = $"assessment-{index}",
        Status = SiloStatus.Joining,
        StartTime = DateTime.UtcNow,
        IAmAliveTime = DateTime.UtcNow
    };

    private static ReminderEntry Reminder() => new()
    {
        GrainId = GrainId.Create("assessment", "reminder"),
        ReminderName = "foo/bar\\#b_a_z?",
        StartAt = new DateTime(2026, 5, 13, 18, 55, 8, DateTimeKind.Utc),
        Period = TimeSpan.FromDays(10)
    };
}
