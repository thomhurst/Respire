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
// Both clients run the same provider operations. Known gaps must fail with the
// specific unsupported member, while the upstream client must complete successfully.
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
    }, "ScriptEvaluateAsync", typeof(RedisStorageException));

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task Storage_Clear(bool deleteStateOnClear) => Compare(async connection =>
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        await using var storage = await Storage(connection, services, deleteStateOnClear);
        var grainId = GrainId.Create("assessment", "state");
        var state = new GrainState<int> { State = 42 };
        // Seed through the control so the clear script is reached even though shim writes fail.
        await using var seed = await Storage(_control!, services, deleteStateOnClear);
        await seed.WriteStateAsync("integer", grainId, state);
        var writtenETag = state.ETag;
        await storage.ClearStateAsync("integer", grainId, state);
        Assert.NotEqual(writtenETag, state.ETag);
        Assert.False(state.RecordExists);
        var read = new GrainState<int>();
        await storage.ReadStateAsync("integer", grainId, read);
        Assert.False(read.RecordExists);
        Assert.Equal(0, read.State);
    }, "ScriptEvaluateAsync", typeof(RedisStorageException));

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
    }, "ScriptEvaluateAsync", typeof(RedisRemindersException));

    [Test]
    public Task Reminders_ReadRows() => Compare(async connection =>
    {
        await using var table = Reminders(connection);
        await table.Init();
        await using var seed = Reminders(_control!);
        await seed.Init();
        var reminder = Reminder();
        await seed.UpsertRow(reminder);
        Assert.Equal(reminder.ReminderName, Assert.Single((await table.ReadRows(reminder.GrainId)).Reminders).ReminderName);
        Assert.Single((await table.ReadRows(0, uint.MaxValue)).Reminders);
        Assert.Single((await table.ReadRows(0, 0)).Reminders);
    }, "SortedSetRangeByValueAsync", typeof(RedisRemindersException));

    [Test]
    public Task Reminders_RemoveRow() => Compare(async connection =>
    {
        await using var table = Reminders(connection);
        await table.Init();
        await using var seed = Reminders(_control!);
        await seed.Init();
        var reminder = Reminder();
        reminder.ETag = await seed.UpsertRow(reminder);
        Assert.False(await table.RemoveRow(reminder.GrainId, reminder.ReminderName, "stale"));
        Assert.True(await table.RemoveRow(reminder.GrainId, reminder.ReminderName, reminder.ETag));
        Assert.False(await table.RemoveRow(reminder.GrainId, reminder.ReminderName, reminder.ETag));
    }, "SortedSetRemoveRangeByValueAsync", typeof(RedisRemindersException));

    private ConnectionMultiplexer? _control;
    private ClusterOptions _cluster = null!;

    private async Task Compare(Func<IConnectionMultiplexer, Task> scenario, string? gap = null, Type? exceptionType = null)
    {
        var shimOptions = RespireOptions.Parse(redis.ConnectionString) with { Connections = 1 };
        var controlOptions = ConfigurationOptions.Parse(redis.StackExchangeConnectionString);
        controlOptions.Protocol = shimOptions.Protocol switch
        {
            RespProtocol.Resp2 => RedisProtocol.Resp2,
            RespProtocol.Resp3 => RedisProtocol.Resp3,
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
        foreach (var connection in new IConnectionMultiplexer[] { control, shim })
        {
            _cluster = new ClusterOptions { ServiceId = Guid.NewGuid().ToString("N"), ClusterId = Guid.NewGuid().ToString("N") };
            if (ReferenceEquals(connection, control) || gap is null)
            {
                await scenario(connection);
            }
            else
            {
                var exception = await Assert.ThrowsAsync(exceptionType!, () => scenario(connection));
                Assert.Contains("System.NotSupportedException", exception.Message);
                Assert.Contains(gap, exception.Message);
            }
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

    private async Task<RedisGrainStorage> Storage(IConnectionMultiplexer connection, IServiceProvider services, bool deleteStateOnClear)
    {
        var storage = new RedisGrainStorage("assessment", new RedisStorageOptions
        {
            CreateMultiplexer = _ => Task.FromResult((connection, true)),
            DeleteStateOnClear = deleteStateOnClear
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
