using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public partial class StreamWorkerTests
{
    [Test]
    [Arguments(0xD800)]
    [Arguments(0xD801)]
    [Arguments(0xDC00)]
    [Arguments(0xDFFF)]
    public async Task MalformedStreamsFailBeforeRegistrationChanges(int surrogate)
    {
        // Keep malformed UTF-16 out of discovery metadata, which uses replacement fallback.
        foreach (var stream in new[] { $"events{(char)surrogate}", $"{(char)surrogate}events" })
        foreach (var typed in new[] { false, true })
        {
            var services = new ServiceCollection().AddSingleton(new State());
            var original = services.ToArray();
            await Assert.That(() =>
            {
                if (typed)
                    services.AddRespireStreamWorker<TypedHandler, int>(stream, "workers", _ => 0);
                else
                    services.AddRespireStreamWorker<EntryHandler>(stream, "workers");
            }).Throws<EncoderFallbackException>();
            await Assert.That(services.SequenceEqual(original)).IsTrue();
            // Reuse the telemetry name to prove rejection did not reserve it.
            services.AddRespireStreamWorker<EntryHandler>("events", "workers");
        }
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task UnicodeStreamsRemainDistinctOnTheWire(int protocol, bool typed)
    {
        foreach (var (first, second) in new[]
        {
            ("\U0001F600", "\U0001F601"), ("\uFFFD", "\U0001F600"), ("\u00E9", "e\u0301"),
        })
        {
            await using var server = new RespireFakeServer();
            await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with
            {
                Protocol = (RespireProtocol)protocol,
            });
            var state = new State();
            var services = new ServiceCollection().AddSingleton(state).AddScoped<ScopeProbe>()
                .AddSingleton<IRespireClient>(client);
            var streams = new[] { "events-" + first, "events-" + second };
            for (var i = 0; i < streams.Length; i++)
            {
                await client.Streams.AddAsync(streams[i], ("payload", i.ToString()));
                var options = new RespireStreamWorkerOptions { TelemetryName = $"stream-{i}" };
                if (typed)
                    services.AddRespireStreamWorker<EntryHandler, RespireStreamEntry>(streams[i], "workers", entry => entry, options);
                else
                    services.AddRespireStreamWorker<EntryHandler>(streams[i], "workers", options);
            }
            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var workers = provider.GetServices<IHostedService>().ToArray();
            try
            {
                foreach (var worker in workers) await worker.StartAsync(CancellationToken.None);
                var delivery = await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
                var otherDelivery = await state.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
                var payloads = new[] { delivery.GetString("payload"), otherDelivery.GetString("payload") };
                await Assert.That(payloads.Order().SequenceEqual(new[] { "0", "1" })).IsTrue();
                foreach (var stream in streams)
                {
                    await UntilAsync(async () => (await client.Streams.PendingSummaryAsync(stream, "workers")).Count == 0);
                    var groups = await client.Streams.GroupInfoAsync(stream);
                    await Assert.That(groups.Single().Name).IsEqualTo("workers");
                }
            }
            finally
            {
                using var deadline = new CancellationTokenSource(Deadline);
                foreach (var worker in workers) await worker.StopAsync(deadline.Token);
            }
        }
    }

    [Test]
    [Arguments(0xD800)]
    [Arguments(0xD801)]
    [Arguments(0xDC00)]
    [Arguments(0xDFFF)]
    public async Task MalformedIdentitiesFailBeforeRegistrationChanges(int surrogate)
    {
        // Construct malformed strings at runtime so test discovery does not serialize them with replacement fallback.
        foreach (var name in new[] { $"worker{(char)surrogate}", $"{(char)surrogate}worker" })
        foreach (var group in new[] { false, true })
        foreach (var typed in new[] { false, true })
        {
            var services = new ServiceCollection().AddSingleton(new State());
            var original = services.ToArray();
            var options = new RespireStreamWorkerOptions { ConsumerName = group ? null : name };
            var groupName = group ? name : "workers";
            await Assert.That(() =>
            {
                if (typed)
                    services.AddRespireStreamWorker<TypedHandler, int>("events", groupName, _ => 0, options);
                else
                    services.AddRespireStreamWorker<EntryHandler>("events", groupName, options);
            }).Throws<EncoderFallbackException>();
            await Assert.That(services.SequenceEqual(original)).IsTrue();
            // A failed registration must not reserve the default telemetry name either.
            services.AddRespireStreamWorker<EntryHandler>("events", "workers");
        }
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    public async Task BlankGroupsFailBeforeRegistrationChanges(string group)
    {
        var services = new ServiceCollection();
        await Assert.That(() => services.AddRespireStreamWorker<EntryHandler>("events", group))
            .Throws<ArgumentException>();
        await Assert.That(services.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task UnicodeIdentitiesRemainDistinctDuringStartupReplay(int protocol, bool distinctGroups)
    {
        foreach (var (first, second) in new[]
        {
            ("\U0001F600", "\U0001F601"), ("\uFFFD", "\U0001F600"), ("\u00E9", "e\u0301"),
        })
        {
            var group = "group-" + first;
            var otherGroup = distinctGroups ? "group-" + second : group;
            var consumer = "worker-" + first;
            var otherConsumer = distinctGroups ? consumer + "-0" : "worker-" + second + "-0";
            new ServiceCollection().AddRespireStreamWorker<EntryHandler>("events", otherGroup, new()
            {
                ConsumerName = distinctGroups ? consumer : "worker-" + second,
            });
            await using var fixture = await Fixture.CreateAsync(protocol, group: group, options: new()
            {
                ConsumerName = consumer, MinimumIdleTime = TimeSpan.FromHours(1),
            });
            for (var i = 0; i < 3; i++) await fixture.AddAsync(i);
            await fixture.View.Streams.CreateGroupAsync("events", group, RespireStreamId.Beginning);
            if (distinctGroups)
                await fixture.View.Streams.CreateGroupAsync("events", otherGroup, RespireStreamId.Beginning);
            await fixture.View.Streams.ReadGroupOnceAsync("events", group, consumer + "-0", new() { Count = 1 });
            var other = (await fixture.View.Streams.ReadGroupOnceAsync("events", otherGroup, otherConsumer,
                new() { Count = 1 })).Single();
            var firstIdentity = distinctGroups ? group : consumer + "-0";
            var secondIdentity = distinctGroups ? otherGroup : otherConsumer;
            await Assert.That(Encoding.UTF8.GetBytes(firstIdentity).SequenceEqual(Encoding.UTF8.GetBytes(secondIdentity)))
                .IsFalse();

            await fixture.StartAsync();
            var replay = await fixture.State.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            await Assert.That(replay.GetString("payload")).IsEqualTo("0");
            for (var i = distinctGroups ? 1 : 2; i < 3; i++)
            {
                var fresh = await fixture.State.Deliveries.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
                await Assert.That(fresh.GetString("payload")).IsEqualTo(i.ToString());
            }
            await UntilAsync(async () => (await fixture.View.Streams.PendingSummaryAsync("events", group)).Count
                == (distinctGroups ? 0 : 1));
            await fixture.StopAsync();
            var pending = (await fixture.View.Streams.PendingAsync("events", otherGroup)).Single();
            await Assert.That(pending.Id).IsEqualTo(other.Id);
            await Assert.That(pending.Consumer).IsEqualTo(otherConsumer);
            await Assert.That(pending.DeliveryCount).IsEqualTo(1);
            var groups = await fixture.View.Streams.GroupInfoAsync("events");
            await Assert.That(groups.Any(info => info.Name == group)).IsTrue();
            await Assert.That(groups.Any(info => info.Name == otherGroup)).IsTrue();
        }
    }
}
