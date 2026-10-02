using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelFenceTransitionTests
{
    private static readonly RespireEndpoint A = new("127.0.0.1", 6379);
    private static readonly RespireEndpoint B = new("127.0.0.1", 6380);
    private static readonly RespireEndpoint First = new("first", 26379);
    private static readonly RespireEndpoint Second = new("second", 26379);

    // The table enumerates idle, active and active+pending transitions, success/failure,
    // switch evidence and both kinds of wake-up-only evidence. Every row exercises the
    // resolver with missing epochs, equal epochs and strictly newer epochs below.
    private static readonly (string Name, RespireEndpoint[] Sources, RespireEndpoint[] Allowed, bool Bound)[] Rows =
    [
        ("idle-switch", [A], [B], false),
        ("idle-gap", [], [A, B], false),
        ("idle-down", [], [A, B], false),
        ("active-success", [A], [B], false),
        ("active-failure", [A], [B], false),
        ("pending-success", [B], [A], false),
        ("pending-failure", [A, B], [], false),
        ("gap-success", [], [B], true),
        ("gap-failure", [], [A, B], false),
        ("down-success", [], [B], true),
        ("down-failure", [], [A, B], false),
        ("switch-gap-success", [], [B], true),
        ("switch-gap-failure", [A], [B], false),
        ("switch-gap-duplicate-success", [A], [B], false),
        ("switch-gap-duplicate-failure", [A], [B], false),
    ];

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EveryFenceTransitionRequiresConsistentDiscoveryEvidence(bool metadataAvailable)
    {
        foreach (var row in Rows)
        {
            var hint = Transition(row.Name);
            await Assert.That(hint.Sources.Select(source => source.Endpoint)).IsEquivalentTo(row.Sources);
            await Assert.That(hint.ReconciliationPrimary.HasValue).IsEqualTo(row.Bound);
            foreach (var newer in metadataAvailable ? new[] { false, true } : [false])
            foreach (var candidate in new[] { A, B })
            {
                var epoch = newer ? 3 : 2;
                await using var reporter = new FakeRespServer(2, "*0\r\n"u8.ToArray())
                {
                    ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER")
                        ? Encoding.ASCII.GetBytes($"*2\r\n+{candidate.Host}\r\n+{candidate.Port}\r\n")
                        : command == "SENTINEL MASTER mymaster" && metadataAvailable
                            ? Encoding.ASCII.GetBytes($"*8\r\n+ip\r\n+{candidate.Host}\r\n+port\r\n+{candidate.Port}\r\n+config-epoch\r\n+{epoch}\r\n+flags\r\n+master\r\n")
                            : "*0\r\n"u8.ToArray(),
                };
                var options = new RespireOptions
                {
                    Protocol = RespProtocol.Resp2, SentinelPrimaryName = "mymaster",
                    Endpoints = [new("127.0.0.1", reporter.Port)],
                };
                var state = new SentinelDiscoveryState(options.Endpoints);
                if (metadataAvailable) state.AcceptConfiguration(B, 2);
                var validations = 0;
                var accepted = false;
                try
                {
                    await SentinelResolver.ResolveAndConnectPrimaryAsync(options, (primary, _, _) =>
                    {
                        validations++;
                        return ValueTask.FromResult(primary.PrimaryEndpoint);
                    }, CancellationToken.None, discoveryState: state, preferredTarget: hint.Target, notificationHint: hint);
                    accepted = true;
                }
                catch (RespireConnectionException) { }
                var expected = newer || row.Allowed.Contains(candidate) && (!metadataAvailable || candidate == B);
                // Include the row and candidate in assertion values so any failure identifies
                // the exact transition, rather than only reporting an unexpected boolean.
                await Assert.That((row.Name, candidate, newer, accepted)).IsEqualTo((row.Name, candidate, newer, expected));
                await Assert.That(validations).IsEqualTo(expected ? 1 : 0);
            }
        }
    }

    private static SentinelHint Transition(string name)
    {
        var coalescer = new SentinelNotificationCoalescer();
        var outbound = SentinelHint.FromSwitchMaster("a-to-b", A, B, First);
        var hint = name.StartsWith("switch-gap") ? outbound
            : name.Contains("gap") ? SentinelHint.FromGap(First)
            : name.Contains("down") ? SentinelHint.FromDown("down", First) : outbound;
        coalescer.Offer(hint, false);
        if (name.StartsWith("idle")) return coalescer.Active!.Value;
        if (name.StartsWith("switch-gap"))
        {
            coalescer.Offer(SentinelHint.FromGap(Second), false);
            if (name.Contains("duplicate")) coalescer.Offer(outbound, false);
        }
        else if (name.StartsWith("pending"))
        {
            coalescer.Offer(SentinelHint.FromSwitchMaster("b-to-a", B, A, Second), false);
            coalescer.Offer(outbound, false);
        }
        else coalescer.Offer(hint with { Reporters = [Second] }, false);
        var failed = name.EndsWith("failure");
        return coalescer.TakePending(failed, failed ? (RespireEndpoint?)null : B, failed ? (RespireEndpoint?)null : B)!.Value;
    }

    [Test]
    public async Task UnfencedReconciliationUsesValidatedPeerWithoutAcceptingAmbiguousAliases()
    {
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHint.FromGap(First) with { Reporters = [First, Second] }, false);
        var next = coalescer.TakePending(validatedPrimary: new("primary.internal", 6379), validatedPeer: A)!.Value;
        var identity = next.ReconciliationPrimary!.Value;
        await Assert.That(identity.Matches(new("::ffff:127.0.0.1", 6379), null)).IsTrue();
        await Assert.That(identity.Matches(new("alias.internal", 6379), ["127.0.0.1"])).IsTrue();
        await Assert.That(identity.Matches(new("alias.internal", 6379), ["127.0.0.1", "192.0.2.1"])).IsFalse();
        await Assert.That(identity.Matches(new("127.0.0.1", 6380), null)).IsFalse();
        var independent = SentinelHint.FromSwitchMaster("a-to-b", A, B, Second);
        await Assert.That(SentinelNotificationCoalescer.Merge(next, independent).ReconciliationPrimary).IsNull();
    }
}
