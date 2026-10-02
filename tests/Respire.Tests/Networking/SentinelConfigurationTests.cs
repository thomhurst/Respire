using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelConfigurationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FallbackDiscoveryRejectsSwitchSourceAliasesBeforeRoleValidation(bool resolvedAlias)
    {
        var previous = new RespireEndpoint("old-primary.internal", 6379);
        var source = new RespireEndpoint("127.0.0.1", 6379);
        var target = new RespireEndpoint("127.0.0.1", 6380);
        await using var stale = new FakeRespServer(2, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER")
                ? "*2\r\n+127.0.0.1\r\n+6379\r\n"u8.ToArray() : "*0\r\n"u8.ToArray(),
        };
        await using var fresh = new FakeRespServer(2, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER")
                ? "*2\r\n+127.0.0.1\r\n+6380\r\n"u8.ToArray() : "*0\r\n"u8.ToArray(),
        };
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            SentinelPrimaryName = "mymaster",
            Endpoints = [new("127.0.0.1", stale.Port), new("127.0.0.1", fresh.Port)],
        };
        var hint = new SentinelHint("switch", target, resolvedAlias ? previous : source,
            OldPrimaryAddresses: resolvedAlias ? [source.Host] : null);
        var validated = new List<RespireEndpoint>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await SentinelResolver.ResolveAndConnectPrimaryAsync(options, (candidate, _) =>
        {
            // Both servers would report ROLE master; stale evidence must be rejected first.
            validated.Add(candidate.Endpoints[0]);
            return ValueTask.FromResult(candidate.Endpoints[0]);
        }, timeout.Token, previouslyValidatedPrimary: previous, preferredTarget: target, notificationHint: hint);
        await Assert.That(result).IsEqualTo(target);
        await Assert.That(validated).IsEquivalentTo([target]);
    }
}
