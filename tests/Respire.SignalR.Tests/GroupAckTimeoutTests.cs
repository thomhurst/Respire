using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.SignalR.Tests;

public class GroupAckTimeoutTests
{
    private const long MaxTimeoutTicks = (uint.MaxValue - 1L) * TimeSpan.TicksPerMillisecond;

    [Test]
    [Arguments(0L)]
    [Arguments(-1L)]
    [Arguments(MaxTimeoutTicks + 1)]
    public async Task ConstructorRejectsInvalidTimeoutBeforeUsingClient(long ticks)
    {
        var client = RespireClient.Create("redis://127.0.0.1:1");
        // Accessing the client during backplane setup would throw ObjectDisposedException.
        await client.DisposeAsync();

        var error = await Assert.That(() => CreateManager(client, ticks)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(error!.ParamName).IsEqualTo("options");
    }

    [Test]
    [Arguments(1L)]
    [Arguments(MaxTimeoutTicks)]
    public async Task ConstructorAcceptsTimeoutBoundaries(long ticks)
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        await using var manager = CreateManager(client, ticks);
    }

    private static RespireHubLifetimeManager<BackplaneTestHub> CreateManager(RespireClient client, long ticks)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        using var provider = services.BuildServiceProvider();

        return new(client, NullLogger<RespireHubLifetimeManager<BackplaneTestHub>>.Instance,
            Options.Create(new RespireSignalROptions { GroupAckTimeout = TimeSpan.FromTicks(ticks) }),
            provider.GetRequiredService<IHubProtocolResolver>(), globalHubOptions: null, hubOptions: null);
    }
}
