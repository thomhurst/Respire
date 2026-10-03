using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelConfigurationTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task AnnouncedHostnameCannotResolveBackToDemotedSource(bool newerEpoch, bool capturedHostnameSource)
    {
        var source = new RespireEndpoint("127.0.0.1", 6379);
        var target = new RespireEndpoint("primary.example", 6379);
        await using var reporter = new FakeRespServer(2, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER")
                ? "*2\r\n+primary.example\r\n+6379\r\n"u8.ToArray()
                : command == "SENTINEL MASTER mymaster" && newerEpoch
                    ? "*8\r\n+ip\r\n+primary.example\r\n+port\r\n+6379\r\n+config-epoch\r\n+2\r\n+flags\r\n+master\r\n"u8.ToArray()
                    : "*0\r\n"u8.ToArray(),
        };
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp2, SentinelPrimaryName = "mymaster",
            Endpoints = [new("127.0.0.1", reporter.Port)],
        };
        var state = new SentinelDiscoveryState(options.Endpoints);
        if (newerEpoch) state.AcceptConfiguration(source, 1);
        var announcedSource = capturedHostnameSource ? new RespireEndpoint("former.internal", source.Port) : source;
        var hint = SentinelHint.FromSwitchMaster("switch", announcedSource, target, options.Endpoints[0]);
        if (capturedHostnameSource) hint = hint.WithSourceAddresses(announcedSource, [source.Host]);
        var validations = 0;
        var accepted = false;
        try
        {
            await SentinelResolver.ResolveAndConnectPrimaryAsync(options, (candidate, _, _) =>
            {
                validations++;
                return ValueTask.FromResult(candidate.PrimaryEndpoint);
            }, CancellationToken.None, state, previouslyValidatedPrimary: source, preferredTarget: target,
                notificationHint: hint,
                hostResolver: (_, _) => Task.FromResult<System.Net.IPAddress[]>([System.Net.IPAddress.Loopback]));
            accepted = true;
        }
        catch (RespireConnectionException) { }
        await Assert.That(accepted).IsFalse();
        await Assert.That(validations).IsEqualTo(0);
    }

    [Test]
    public async Task SwitchConfirmationUsesCanonicalTargetIdentity()
    {
        await using var reporter = new FakeRespServer(2, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER")
                ? "*2\r\n+2001:0db8:0:0:0:0:0:1\r\n+6379\r\n"u8.ToArray() : "*0\r\n"u8.ToArray(),
        };
        var target = new RespireEndpoint("2001:db8::1", 6379);
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp2, SentinelPrimaryName = "mymaster",
            Endpoints = [new("127.0.0.1", reporter.Port)],
        };
        var hint = SentinelHintBuilder.Create("confirmation", target, target);
        var selected = await SentinelResolver.ResolveAndConnectPrimaryAsync(options,
            (candidate, _, _) => ValueTask.FromResult(candidate.PrimaryEndpoint), CancellationToken.None,
            previouslyValidatedPrimary: target, preferredTarget: target, notificationHint: hint);
        await Assert.That(selected.Port).IsEqualTo(target.Port);
        await Assert.That(SentinelResolver.NormalizeHost(selected.Host)).IsEqualTo(target.Host);
    }

    [Test]
    public async Task ConflictingTargetsStillFenceEveryRetainedSwitchSource()
    {
        var first = new RespireEndpoint("127.0.0.1", 6379);
        var second = new RespireEndpoint("127.0.0.1", 6380);
        var fresh = new RespireEndpoint("127.0.0.1", 6381);
        await using var stale = new FakeRespServer(2, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER")
                ? "*2\r\n+127.0.0.1\r\n+6380\r\n"u8.ToArray() : "*0\r\n"u8.ToArray(),
        };
        await using var reporter = new FakeRespServer(2, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER")
                ? "*2\r\n+127.0.0.1\r\n+6381\r\n"u8.ToArray() : "*0\r\n"u8.ToArray(),
        };
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp2, SentinelPrimaryName = "mymaster",
            Endpoints = [new("127.0.0.1", stale.Port), new("127.0.0.1", reporter.Port)],
        };
        var hint = SentinelNotificationCoalescer.Merge(SentinelHintBuilder.Create("a-b", second, first),
            SentinelHintBuilder.Create("b-a", first, second));
        await Assert.That(hint.Target).IsNull();
        var validated = new List<RespireEndpoint>();
        var selected = await SentinelResolver.ResolveAndConnectPrimaryAsync(options, (candidate, _, _) =>
        {
            validated.Add(candidate.PrimaryEndpoint);
            return ValueTask.FromResult(candidate.PrimaryEndpoint);
        }, CancellationToken.None, previouslyValidatedPrimary: first, notificationHint: hint);
        await Assert.That(selected).IsEqualTo(fresh);
        await Assert.That(validated).IsEquivalentTo([fresh]);
    }

    [Test]
    [Arguments(false, "127.0.0.1", "127.0.0.1")]
    [Arguments(true, "127.0.0.1", "127.0.0.1")]
    [Arguments(false, "0:0:0:0:0:0:0:1", "::1")]
    [Arguments(true, "0:0:0:0:0:0:0:1", "::1")]
    [Arguments(false, "::ffff:127.0.0.1", "127.0.0.1")]
    [Arguments(true, "0:0:0:0:0:ffff:7f00:1", "127.0.0.1")]
    [Arguments(false, "old-primary.internal", "127.0.0.1")]
    [Arguments(true, "candidate.internal", "127.0.0.1")]
    public async Task FallbackDiscoveryRejectsSwitchSourceAliasesBeforeRoleValidation(
        bool resolvedAlias, string candidateHost, string sourceHost)
    {
        var previous = new RespireEndpoint("old-primary.internal", 6379);
        var source = new RespireEndpoint(sourceHost, 6379);
        var target = new RespireEndpoint("127.0.0.1", 6380);
        await using var stale = new FakeRespServer(2, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER")
                ? Encoding.UTF8.GetBytes($"*2\r\n+{candidateHost}\r\n+6379\r\n") : "*0\r\n"u8.ToArray(),
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
        var hint = SentinelHintBuilder.Create("switch", target, resolvedAlias ? previous : source,
            OldPrimaryAddresses: resolvedAlias ? [source.Host] : null);
        var validated = new List<RespireEndpoint>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await SentinelResolver.ResolveAndConnectPrimaryAsync(options, (candidate, _, _) =>
        {
            // Both servers would report ROLE master; stale evidence must be rejected first.
            validated.Add(candidate.Endpoints[0]);
            return ValueTask.FromResult(candidate.Endpoints[0]);
        }, timeout.Token, previouslyValidatedPrimary: previous, preferredTarget: target, notificationHint: hint,
            hostResolver: (_, _) => Task.FromResult<System.Net.IPAddress[]>([System.Net.IPAddress.Loopback]));
        await Assert.That(result).IsEqualTo(target);
        await Assert.That(validated).IsEquivalentTo([target]);
    }
}
