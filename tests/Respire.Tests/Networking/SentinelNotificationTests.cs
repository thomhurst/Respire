using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelNotificationTests
{
    private static readonly RespireEndpoint OldPrimary = new("10.0.0.1", 6379);
    private static readonly RespireEndpoint NewPrimary = new("10.0.0.2", 6380);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SwitchEvidenceRejectsAnnouncedAndResolvedSourceAliases(bool resolved)
    {
        var hint = SentinelHintBuilder.Create("switch", NewPrimary,
            resolved ? new("old.internal", OldPrimary.Port) : OldPrimary,
            OldPrimaryAddresses: resolved ? [OldPrimary.Host] : null);
        await Assert.That(SentinelResolver.MatchesSwitchSource(OldPrimary, in hint)).IsTrue();
        await Assert.That(SentinelResolver.MatchesSwitchSource(NewPrimary, in hint)).IsFalse();
    }

    [Test]
    [Arguments("0:0:0:0:0:0:0:1", "::1")]
    [Arguments("::ffff:192.0.2.1", "192.0.2.1")]
    public async Task CoalescedSourceAliasesUseCanonicalAddresses(string candidate, string resolved)
    {
        var hint = SentinelHintBuilder.Create("switch", NewPrimary, OldPrimary,
            AdditionalSources: [new(new("other.internal", 6379), [resolved])]);
        await Assert.That(SentinelResolver.MatchesSwitchSource(new(candidate, 6379), in hint)).IsTrue();
        await Assert.That(SentinelResolver.MatchesSwitchSource(new(candidate, 6380), in hint)).IsFalse();
        await Assert.That(SentinelResolver.MatchesSwitchSource(new("192.0.2.2", 6379), in hint)).IsFalse();
    }

    [Test]
    public async Task ConfigurationEpochNeverMovesBackwardOrChangesOwnerAtTheSameEpoch()
    {
        var state = new SentinelDiscoveryState([]);
        state.AcceptConfiguration(OldPrimary, 10);
        await Assert.That(state.IsCurrentConfiguration(NewPrimary, 9)).IsFalse();
        await Assert.That(state.IsCurrentConfiguration(NewPrimary, 10)).IsFalse();
        await Assert.That(state.IsCurrentConfiguration(NewPrimary, null)).IsFalse();
        await Assert.That(state.IsCurrentConfiguration(OldPrimary, null)).IsTrue();
        await Assert.That(state.IsCurrentConfiguration(NewPrimary, 11)).IsTrue();
        state.AcceptConfiguration(NewPrimary, 11);
        await Assert.That(state.IsCurrentConfiguration(OldPrimary, 10)).IsFalse();
        await Assert.That(state.IsCurrentConfiguration(OldPrimary, 12)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConfigurationEpochRejectsAmbiguousDnsOverlap(bool candidateIsNumeric)
    {
        var state = new SentinelDiscoveryState([]);
        state.AcceptConfiguration(new("active.internal", 6379), 10, ["192.0.2.1", "192.0.2.2"]);
        var candidate = new RespireEndpoint(candidateIsNumeric ? "192.0.2.1" : "stale.internal", 6379);
        var addresses = candidateIsNumeric ? null : new[] { "192.0.2.1", "192.0.2.3" };
        await Assert.That(state.TryObserveConfiguration(candidate, 10, addresses)).IsFalse();
        await Assert.That(state.TryObserveConfiguration(candidate, null, addresses)).IsFalse();
        await Assert.That(state.TryObserveConfiguration(candidate, 11, addresses)).IsTrue();
    }

    [Test]
    public async Task RandomMergeOrdersPreserveSourceAddressesAndEveryReporter()
    {
        var random = new Random(678);
        var hints = Enumerable.Range(1, 12).Select(index => SentinelHintBuilder.Create($"hint-{index}",
            NewPrimary, new($"source-{index}", 6379), MustRediscover: true,
            OldPrimaryAddresses: [$"10.0.0.{index}"], ReportingSentinel: new($"sentinel-{index}", 26379))).ToArray();
        for (var attempt = 0; attempt < 128; attempt++)
        {
            var shuffled = hints.Concat(hints).ToArray();
            random.Shuffle(shuffled);
            SentinelHint? merged = null;
            foreach (var hint in shuffled) merged = SentinelNotificationCoalescer.Merge(merged, in hint);
            var result = merged!.Value;
            var sources = result.Sources.ToDictionary(source => source.Endpoint, source => source.Addresses);
            var reporters = result.Reporters;
            await Assert.That(sources.Count).IsEqualTo(hints.Length);
            await Assert.That(reporters).IsEquivalentTo(hints.Select(hint => hint.ReportingSentinel!.Value));
            foreach (var hint in hints)
                await Assert.That(sources[hint.OldPrimary!.Value]).IsEquivalentTo(hint.Sources[0].Addresses!);
            await Assert.That(result.MustRediscover).IsTrue();
        }
    }

    private static async Task AssertHintEvidence(SentinelHint actual, SentinelHint expected)
    {
        await Assert.That(actual.Key).IsEqualTo(expected.Key);
        await Assert.That(actual.MustRediscover).IsEqualTo(expected.MustRediscover);
        await Assert.That(actual.Targets).IsEquivalentTo(expected.Targets);
        await Assert.That(actual.Sources).IsEquivalentTo(expected.Sources);
        await Assert.That(actual.Reporters).IsEquivalentTo(expected.Reporters);
    }

    [Test]
    public async Task OfferedEvidenceIsAlwaysQueuedOrDiscoveredAcrossWorkerTransitions()
    {
        var random = new Random(727);
        for (var run = 0; run < 64; run++)
        {
            var coalescer = new SentinelNotificationCoalescer();
            var offered = new HashSet<RespireEndpoint>();
            var discovered = new HashSet<RespireEndpoint>();
            var resolution = coalescer.BeginSourceResolution(SentinelHintBuilder.Create("lookup"));
            try
            {
                for (var step = 0; step < 64; step++)
                {
                    if (coalescer.Active is null || random.Next(3) != 0)
                    {
                        var source = new RespireEndpoint($"source-{run}-{step}", 6379);
                        offered.Add(source);
                        coalescer.Offer(SentinelHintBuilder.Create($"hint-{step}", OldPrimary: source, MustRediscover: true), false);
                    }
                    else
                    {
                        discovered.UnionWith(coalescer.Active.Value.Sources.Select(source => source.Endpoint));
                        var next = random.Next(2) == 0 ? coalescer.TakePending() : coalescer.SupersedeActive();
                        if (next is null) coalescer.Complete();
                    }
                    var retained = new HashSet<RespireEndpoint>(discovered);
                    if (coalescer.Active is { } active) retained.UnionWith(active.Sources.Select(source => source.Endpoint));
                    if (coalescer.Pending is { } pending) retained.UnionWith(pending.Sources.Select(source => source.Endpoint));
                    await Assert.That(retained).IsEquivalentTo(offered);
                    await Assert.That(resolution.Hint.Sources.Select(source => source.Endpoint)).IsEquivalentTo(offered);
                }
            }
            finally { coalescer.EndSourceResolution(resolution); }
        }
    }

    [Test]
    public async Task MergeUnionsAreCommutativeIdempotentAndKeepTheFaultFlag()
    {
        var random = new Random(549);
        var endpoints = Enumerable.Range(0, 8).Select(i => new RespireEndpoint($"10.0.0.{i + 1}", 6379)).ToArray();
        for (var iteration = 0; iteration < 256; iteration++)
        {
            var left = CreateHint();
            var right = CreateHint();
            var forward = SentinelNotificationCoalescer.Merge(left, in right);
            var reverse = SentinelNotificationCoalescer.Merge(right, in left);
            var duplicate = SentinelNotificationCoalescer.Merge(forward, in forward);
            await Assert.That(Evidence(forward)).IsEqualTo(Evidence(reverse));
            await Assert.That(Evidence(duplicate)).IsEqualTo(Evidence(forward));
            if (left.MustRediscover || right.MustRediscover) await Assert.That(forward.MustRediscover).IsTrue();
        }

        SentinelHint CreateHint() => SentinelHintBuilder.Create("property", endpoints[random.Next(endpoints.Length)],
            endpoints[random.Next(endpoints.Length)], random.Next(2) == 0,
            OldPrimaryAddresses: [$"192.0.2.{random.Next(4) + 1}"],
            ReportingSentinel: endpoints[random.Next(endpoints.Length)]);

        static string Evidence(SentinelHint hint) => string.Join("|",
            hint.MustRediscover,
            string.Join(",", hint.Targets.Select(endpoint => endpoint.ToString()).Order()),
            string.Join(",", hint.Sources.Select(source => $"{source.Endpoint}={string.Join(";", (source.Addresses ?? []).Order())}").Order()),
            string.Join(",", hint.Reporters.Select(endpoint => endpoint.ToString()).Order()));
    }

    private static SentinelEvent Parse(string channel, string text, string service = "mymaster")
        => SentinelEvent.Parse(Encoding.UTF8.GetBytes(channel), Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetBytes(service));

    [Test]
    public async Task ParsesSwitchMasterPayload()
    {
        var parsed = Parse("+switch-master", "mymaster 10.0.0.1 6379 10.0.0.2 6380");

        await Assert.That(parsed.Kind).IsEqualTo(SentinelEventKind.SwitchMaster);
        await Assert.That(parsed.OldPrimary).IsEqualTo(OldPrimary);
        await Assert.That(parsed.NewPrimary).IsEqualTo(NewPrimary);
    }

    [Test]
    [Arguments("mymaster 10.0.0.1 port 10.0.0.2 6380")]
    [Arguments("mymaster 10.0.0.1")]
    [Arguments("mymaster")]
    public async Task MalformedSwitchForServiceStillRequestsUntargetedDiscovery(string text)
    {
        var parsed = Parse("+switch-master", text);

        await Assert.That(parsed.Kind).IsEqualTo(SentinelEventKind.SwitchMaster);
        await Assert.That(parsed.OldPrimary).IsNull();
    }

    [Test]
    [Arguments("+sdown", "master mymaster 10.0.0.1 6379")]
    [Arguments("+odown", "master mymaster 10.0.0.1 6379 #quorum 2/2")]
    public async Task ParsesMasterDownPayloads(string channel, string text)
    {
        var parsed = Parse(channel, text);
        await Assert.That(parsed.Kind).IsEqualTo(SentinelEventKind.MasterDown);
        await Assert.That(parsed.OldPrimary).IsEqualTo(OldPrimary);
    }

    [Test]
    public async Task DownReportBindingRetainsMixedOutagesButAllowsCurrentOutagesAndGaps()
    {
        var current = new SentinelValidatedPrimary(NewPrimary, NewPrimary);
        var first = SentinelHint.FromDown("old-down", OldPrimary, OldPrimary);
        var other = SentinelHint.FromDown("other-down", OldPrimary, new("10.0.0.3", 6379));
        var merged = SentinelNotificationCoalescer.Merge(first, in other);
        await Assert.That(merged.DownKey).IsNull();
        await Assert.That(merged.DownPrimaries.Length).IsEqualTo(2);
        await Assert.That(merged.BindSupersededDownReports(current).ReconciliationPrimary).IsEqualTo(current);

        var currentDown = SentinelHint.FromDown("current-down", OldPrimary, NewPrimary);
        var withCurrent = SentinelNotificationCoalescer.Merge(merged, in currentDown);
        await Assert.That(withCurrent.BindSupersededDownReports(current).ReconciliationPrimary).IsNull();
        var gap = SentinelHint.FromGap(OldPrimary);
        var withGap = SentinelNotificationCoalescer.Merge(merged, in gap);
        await Assert.That(withGap.DownPrimaries).IsEmpty();
        await Assert.That(withGap.BindSupersededDownReports(current).ReconciliationPrimary).IsNull();

        var duplicate = SentinelNotificationCoalescer.Merge(first, in first);
        await Assert.That(ReferenceEquals(duplicate.DownPrimaries, first.DownPrimaries)).IsTrue();
    }

    [Test]
    public async Task DuplicateWakeupsReuseEvidenceAndAddressUpdatesPreserveUnchangedSources()
    {
        var hint = SentinelHint.FromDown("down", OldPrimary);
        var duplicate = SentinelHint.FromDown("down", OldPrimary);
        var merged = SentinelNotificationCoalescer.Merge(hint, in duplicate);
        await Assert.That(ReferenceEquals(merged.Reporters, hint.Reporters)).IsTrue();
        await Assert.That(ReferenceEquals(merged.Targets, hint.Targets)).IsTrue();
        await Assert.That(ReferenceEquals(merged.Sources, hint.Sources)).IsTrue();

        var source = SentinelHint.FromSwitchMaster("switch", OldPrimary, NewPrimary, OldPrimary);
        var updated = source.WithSourceAddresses(OldPrimary, ["10.0.0.1"]);
        await Assert.That(source.Sources[0].Addresses).IsNull();
        await Assert.That(updated.Sources[0].Addresses).IsEquivalentTo(["10.0.0.1"]);
        await Assert.That(ReferenceEquals(updated.WithSourceAddresses(OldPrimary, ["10.0.0.1"]).Sources,
            updated.Sources)).IsTrue();
        await Assert.That(ReferenceEquals(updated.WithSourceAddresses(NewPrimary, ["10.0.0.2"]).Sources,
            updated.Sources)).IsTrue();
        var withWakeup = SentinelNotificationCoalescer.Merge(updated, in hint);
        await Assert.That(ReferenceEquals(withWakeup.Sources, updated.Sources)).IsTrue();
    }

    [Test]
    [Arguments("+sdown", "slave 10.0.0.3:6379 10.0.0.3 6379 @ mymaster 10.0.0.1 6379")]
    [Arguments("+odown", "slave 10.0.0.3:6379 10.0.0.3 6379 @ mymaster 10.0.0.1 6379")]
    public async Task ParsesRealReplicaDownPayloads(string channel, string text)
        => await Assert.That(Parse(channel, text).Kind).IsEqualTo(SentinelEventKind.ReplicaDown);

    [Test]
    [Arguments("+switch-master", "othermaster 10.0.0.1 6379 10.0.0.2 6380")]
    [Arguments("+switch-master", "mymaster2 10.0.0.1 6379 10.0.0.2 6380")]
    [Arguments("+sdown", "master othermaster 10.0.0.1 6379")]
    [Arguments("+odown", "master othermaster 10.0.0.1 6379 #quorum 2/2")]
    [Arguments("+sdown", "slave 10.0.0.3:6379 10.0.0.3 6379 @ othermaster 10.0.0.1 6379")]
    // The off-by-one shape (master name at index 4) must not match a replica event.
    [Arguments("+sdown", "slave 10.0.0.3 6379 @ mymaster 10.0.0.1 6379")]
    [Arguments("+sdown", "sentinel 0123abcd 10.0.0.4 26379 @ mymaster 10.0.0.1 6379")]
    [Arguments("+sdown", "")]
    [Arguments("+tilt", "mymaster")]
    public async Task IgnoresEventsForOtherServicesAndShapes(string channel, string text)
        => await Assert.That(Parse(channel, text).Kind).IsEqualTo(SentinelEventKind.None);

    [Test]
    public async Task FirstHintStartsWorkerAndDuplicateCoalesces()
    {
        var coalescer = new SentinelNotificationCoalescer();
        var hint = SentinelHintBuilder.Create("switch", NewPrimary, OldPrimary);

        await Assert.That(coalescer.Offer(in hint, targetIsCurrent: false)).IsTrue();
        await Assert.That(coalescer.ActiveKey).IsEqualTo("switch");
        await Assert.That(coalescer.Offer(in hint, targetIsCurrent: false)).IsFalse();
        await Assert.That(coalescer.Pending).IsNull();
    }

    [Test]
    public async Task DuplicateFaultHintOutlivesTheActiveAttempt()
    {
        var coalescer = new SentinelNotificationCoalescer();
        var down = SentinelHintBuilder.Create("master-down", MustRediscover: true);
        coalescer.Offer(in down, targetIsCurrent: false);

        await Assert.That(coalescer.Offer(in down, targetIsCurrent: false)).IsFalse();
        await AssertHintEvidence(coalescer.Pending!.Value, down);
        await AssertHintEvidence(coalescer.TakePending()!.Value, down);
        await Assert.That(coalescer.ActiveKey).IsEqualTo("master-down");
        await Assert.That(coalescer.TakePending()).IsNull();
    }

    [Test]
    public async Task HintForCurrentPrimaryIsIgnoredUnlessItReportsAFault()
    {
        var coalescer = new SentinelNotificationCoalescer();

        await Assert.That(coalescer.Offer(SentinelHintBuilder.Create("switch", NewPrimary), targetIsCurrent: true)).IsFalse();
        await Assert.That(coalescer.ActiveKey).IsNull();
        await Assert.That(coalescer.Offer(SentinelHintBuilder.Create("gap", MustRediscover: true), targetIsCurrent: true)).IsTrue();
    }

    [Test]
    public async Task CurrentTargetSwitchRemainsPendingDuringUntargetedDiscovery()
    {
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("gap", MustRediscover: true), false);
        var hint = SentinelHintBuilder.Create("switch", NewPrimary, OldPrimary,
            ReportingSentinel: new("127.0.0.1", 26380));

        await Assert.That(coalescer.Offer(in hint, targetIsCurrent: true)).IsFalse();
        await Assert.That(coalescer.Pending.HasValue).IsTrue();
        await AssertHintEvidence(coalescer.TakePending(validatedPrimary: OldPrimary)!.Value, hint);
    }

    [Test]
    public async Task LaterDownHintCannotEraseAPendingSwitch()
    {
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("first"), targetIsCurrent: false);
        var pendingSwitch = SentinelHintBuilder.Create("switch", NewPrimary, OldPrimary);
        coalescer.Offer(in pendingSwitch, targetIsCurrent: false);

        coalescer.Offer(SentinelHintBuilder.Create("master-down", MustRediscover: true), targetIsCurrent: false);

        await AssertHintEvidence(coalescer.Pending!.Value, pendingSwitch with { MustRediscover = true });
    }

    [Test]
    public async Task SwitchReplacesPendingDownAndKeepsTheFault()
    {
        var pendingDown = SentinelHintBuilder.Create("master-down", MustRediscover: true);
        var later = SentinelHintBuilder.Create("switch", OldPrimary: OldPrimary);

        var merged = SentinelNotificationCoalescer.Merge(pendingDown, in later);

        await AssertHintEvidence(merged, later with { MustRediscover = true });
    }

    [Test]
    public async Task UntargetedSwitchKeepsTheEarlierTargetAndRequiresFreshDiscovery()
    {
        var pending = SentinelHintBuilder.Create("a", NewPrimary, OldPrimary);
        var later = SentinelHintBuilder.Create("b", OldPrimary: new RespireEndpoint("10.0.0.9", 6379));

        var merged = SentinelNotificationCoalescer.Merge(pending, in later);

        await Assert.That(merged.Key).IsEqualTo("b");
        await Assert.That(merged.Target).IsEqualTo(NewPrimary);
        await Assert.That(merged.OldPrimary).IsEqualTo(later.OldPrimary);
        await Assert.That(merged.MustRediscover).IsTrue();
    }

    [Test]
    public async Task DelayedEarlierSwitchCannotSatisfyAPendingLaterSwitchByTarget()
    {
        // A→B→C. B→C is pending when another Sentinel's delayed A→B copy arrives.
        var c = new RespireEndpoint("10.0.0.3", 6381);
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("active"), targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("b-to-c", c, NewPrimary), targetIsCurrent: false);

        coalescer.Offer(SentinelHintBuilder.Create("a-to-b", NewPrimary, OldPrimary), targetIsCurrent: false);

        // B may still report ROLE master briefly, so the target shortcut must not consume C's hint.
        await Assert.That(coalescer.Pending!.Value.MustRediscover).IsTrue();
        await Assert.That(coalescer.Pending!.Value.Sources.Select(static source => source.Endpoint)).Contains(NewPrimary);
    }

    [Test]
    public async Task DelayedSwitchCannotEraseRetirementOrReporterForSurvivingTarget()
    {
        var c = new RespireEndpoint("10.0.0.3", 6381);
        var sentinelForC = new RespireEndpoint("10.0.1.3", 26379);
        var sentinelForB = new RespireEndpoint("10.0.1.2", 26379);
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("active"), targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("b-to-c", c, NewPrimary, ReportingSentinel: sentinelForC), targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("a-to-b", NewPrimary, OldPrimary, ReportingSentinel: sentinelForB), targetIsCurrent: false);

        var pending = coalescer.Pending!.Value;
        await Assert.That(pending.Target).IsEqualTo(c);
        await Assert.That(pending.ReportingSentinel).IsEqualTo(sentinelForC);
        await Assert.That(pending.Sources.Select(static source => source.Endpoint)).Contains(NewPrimary);
    }

    [Test]
    public async Task ConflictingFailbackOrderingRequiresUntargetedDiscovery()
    {
        var a = OldPrimary;
        var bToAReporter = new RespireEndpoint("10.0.1.1", 26379);
        var delayedReporter = new RespireEndpoint("10.0.1.2", 26379);
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("active"), targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("b-to-a", a, NewPrimary, ReportingSentinel: bToAReporter), targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("a-to-b-delayed", NewPrimary, a, ReportingSentinel: delayedReporter), targetIsCurrent: false);

        var pending = coalescer.Pending!.Value;
        await Assert.That(pending.MustRediscover).IsTrue();
        await Assert.That(pending.Target).IsNull();
        await Assert.That(pending.ReportingSentinel).IsEqualTo(bToAReporter);
        await Assert.That(pending.Reporters.Skip(1)).IsEquivalentTo([delayedReporter]);
        await Assert.That(pending.OldPrimary).IsEqualTo(a);
        await Assert.That(pending.Sources.Select(static source => source.Endpoint)).Contains(NewPrimary);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConflictingFailbackReportersRemainAvailableForEpochReconciliation(bool activeFailed)
    {
        var first = new RespireEndpoint("10.0.1.1", 26379);
        var delayed = new RespireEndpoint("10.0.1.2", 26379);
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("active"), false);
        coalescer.Offer(SentinelHintBuilder.Create("b-to-a", OldPrimary, NewPrimary, ReportingSentinel: first), false);
        coalescer.Offer(SentinelHintBuilder.Create("a-to-b-delayed", NewPrimary, OldPrimary, ReportingSentinel: delayed), false);
        var recovery = coalescer.TakePending()!.Value;
        await Assert.That(recovery.ReportingSentinel).IsEqualTo(first);

        var next = coalescer.TakePending(activeFailed, validatedPrimary: activeFailed ? (RespireEndpoint?)null : OldPrimary);

        await Assert.That(next!.Value.ReportingSentinel).IsEqualTo(delayed);
        if (!activeFailed)
        {
            await Assert.That(next.Value.Sources.Select(source => source.Endpoint)).IsEquivalentTo([NewPrimary]);
            await Assert.That(next.Value.Targets).IsEquivalentTo([OldPrimary, NewPrimary]);
        }
    }

    [Test]
    public async Task SuccessfulFailbackKeepsUnqueriedReporterAlongsideNewHint()
    {
        var first = new RespireEndpoint("10.0.1.1", 26379);
        var delayed = new RespireEndpoint("10.0.1.2", 26379);
        var currentReporter = new RespireEndpoint("10.0.1.3", 26379);
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("active"), false);
        coalescer.Offer(SentinelHintBuilder.Create("b-to-a", OldPrimary, NewPrimary, ReportingSentinel: first), false);
        coalescer.Offer(SentinelHintBuilder.Create("a-to-b-delayed", NewPrimary, OldPrimary, ReportingSentinel: delayed), false);
        coalescer.TakePending();
        var fresh = SentinelHintBuilder.Create("gap", MustRediscover: true, ReportingSentinel: currentReporter);
        coalescer.Offer(in fresh, false);

        var next = coalescer.TakePending(activeFailed: false)!.Value;
        await Assert.That(next.ReportingSentinel).IsEqualTo(delayed);
        await Assert.That(next.Reporters.Skip(1)).Contains(currentReporter);
        await Assert.That(next.MustRediscover).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReconciliationUsesValidatedAliasesWithoutConsumingOtherPorts(bool activeFailed)
    {
        var first = new RespireEndpoint("first", 26379);
        var delayed = new RespireEndpoint("delayed", 26379);
        var primary = new RespireEndpoint("primary.internal", 6379);
        var numeric = new RespireEndpoint("192.0.2.1", 6379);
        var otherPort = new RespireEndpoint("192.0.2.1", 6380);
        var ambiguous = new RespireEndpoint("ambiguous.internal", 6379);
        var stale = new RespireEndpoint("192.0.2.9", 6379);
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("switch", [primary],
            [new(numeric, null), new(otherPort, null), new(ambiguous, ["192.0.2.1", "192.0.2.9"]), new(stale, null)],
            [first, delayed], true), false);

        var next = coalescer.TakePending(activeFailed, primary, new("::ffff:192.0.2.1", 6379))!.Value;

        await Assert.That(next.Sources.Select(source => source.Endpoint))
            .IsEquivalentTo(activeFailed ? new[] { numeric, otherPort, ambiguous, stale } : new[] { otherPort, ambiguous, stale });
        await Assert.That(next.ReportingSentinel).IsEqualTo(delayed);
    }

    [Test]
    public async Task MergeKeepsReporterForNewTargetWhenItIsAlsoAnEarlierSwitchSource()
    {
        var third = new RespireEndpoint("10.0.0.3", 6381);
        var reporterForB = new RespireEndpoint("10.0.1.2", 26379);
        var reporterForA = new RespireEndpoint("10.0.1.1", 26379);
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("active"), targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("a-to-b", NewPrimary, OldPrimary, ReportingSentinel: reporterForB), targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("c-to-a", OldPrimary, third, ReportingSentinel: reporterForA), targetIsCurrent: false);

        var merged = coalescer.Pending!.Value;

        await Assert.That(merged.ReportingSentinel).IsEqualTo(reporterForB);
        await Assert.That(merged.Reporters.Skip(1)).IsEquivalentTo([reporterForA]);
        await Assert.That(merged.MustRediscover).IsTrue();
    }

    [Test]
    public async Task PendingSwitchRetainsSourceAddressesResolvedBeforeItBecomesActive()
    {
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("active"), targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("b-to-c", new("10.0.0.3", 6381), NewPrimary), targetIsCurrent: false);

        coalescer.RetainResolvedOldPrimaryAddresses(NewPrimary, ["192.0.2.2"]);
        coalescer.Offer(SentinelHintBuilder.Create("a-to-b", NewPrimary, OldPrimary), targetIsCurrent: false);
        var pending = coalescer.TakePending();

        await Assert.That(pending!.Value.Sources.Select(static source => source.Endpoint)).Contains(NewPrimary);
        await Assert.That(pending.Value.Sources.Single(source => source.Endpoint == NewPrimary).Addresses).IsEquivalentTo(["192.0.2.2"]);
    }

    [Test]
    public async Task LaterSwitchRemovesEarlierTargetWhenItBecomesAFormerPrimary()
    {
        var c = new RespireEndpoint("10.0.0.3", 6381);
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("active"), targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("a-to-b", NewPrimary, OldPrimary), targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("b-to-c", c, NewPrimary), targetIsCurrent: false);

        await Assert.That(coalescer.Pending!.Value.Target).IsEqualTo(c);
        await Assert.That(coalescer.Pending!.Value.Targets).IsEquivalentTo([NewPrimary, c]);
    }

    [Test]
    public async Task CoalescedFailbackDoesNotInferChronologyFromSwitchEdges()
    {
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("active"), targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("a-to-b", NewPrimary, OldPrimary), targetIsCurrent: false);
        var a = new RespireEndpoint("10.0.0.1", 6379);
        coalescer.Offer(SentinelHintBuilder.Create("b-to-a", a, NewPrimary), targetIsCurrent: false);

        await Assert.That(coalescer.Pending!.Value.MustRediscover).IsTrue();
        await Assert.That(coalescer.Pending!.Value.Target).IsNull();
        await Assert.That(coalescer.Pending!.Value.MustRediscover).IsTrue();
        await Assert.That(coalescer.Pending!.Value.Targets).IsEquivalentTo([NewPrimary, a]);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task RepeatedActiveSwitchCannotInvertIndependentPendingSourceFence(bool unqueriedActiveReporter, bool aliasSpelling)
    {
        var aToB = SentinelHintBuilder.Create("a-to-b", NewPrimary, OldPrimary) with
        {
            Reporters = unqueriedActiveReporter ? [new("first", 26379), new("second", 26379)] : [],
        };
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(in aToB, targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("b-to-a", OldPrimary, NewPrimary), targetIsCurrent: false);
        var repeated = aliasSpelling ? aToB with
        {
            Key = "same-switch-with-mapped-address",
            Sources = [new(new("::ffff:10.0.0.1", OldPrimary.Port), null)],
        } : aToB;
        coalescer.Offer(in repeated, targetIsCurrent: false);

        await Assert.That(coalescer.Pending!.Value.MustRediscover).IsTrue();
        var next = coalescer.TakePending(validatedPrimary: NewPrimary, validatedPeer: NewPrimary)!.Value;
        await Assert.That(next.Target).IsEqualTo(OldPrimary);
        await Assert.That(next.Sources.Select(static source => source.Endpoint)).IsEquivalentTo([NewPrimary]);
    }

    [Test]
    public async Task DuplicateDeliveryGapRetainsEveryReportingSentinelForCatchUpDiscovery()
    {
        var first = new RespireEndpoint("10.0.1.1", 26379);
        var second = new RespireEndpoint("10.0.1.2", 26379);
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("gap", MustRediscover: true, ReportingSentinel: first), targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("gap", MustRediscover: true, ReportingSentinel: second), targetIsCurrent: false);

        var catchUp = coalescer.TakePending();

        await Assert.That(catchUp!.Value.ReportingSentinel).IsEqualTo(first);
        await Assert.That(catchUp.Value.Reporters.Skip(1)).IsEquivalentTo([second]);
        var secondPass = coalescer.TakePending();
        await Assert.That(secondPass!.Value.ReportingSentinel).IsEqualTo(second);
        await Assert.That(secondPass.Value.Reporters.Skip(1)).IsEmpty();
    }

    [Test]
    public async Task SuccessfulCatchUpCarriesUnqueriedReportersIntoNewPendingHint()
    {
        var first = new RespireEndpoint("10.0.1.1", 26379);
        var second = new RespireEndpoint("10.0.1.2", 26379);
        var third = new RespireEndpoint("10.0.1.3", 26379);
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("gap", MustRediscover: true, ReportingSentinel: first), targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("gap", MustRediscover: true, ReportingSentinel: second), targetIsCurrent: false);
        coalescer.TakePending();
        coalescer.Offer(SentinelHintBuilder.Create("switch", NewPrimary, ReportingSentinel: third), targetIsCurrent: false);

        var next = coalescer.TakePending(activeFailed: false)!.Value;
        await Assert.That(next.MustRediscover).IsTrue();
        await Assert.That(next.ReportingSentinel).IsEqualTo(second);
        await Assert.That(next.Reporters.Skip(1)).IsEquivalentTo([third]);
    }

    [Test]
    public async Task EveryOrderingOfSwitchDownAndGapHintsKeepsTheMergeInvariants()
    {
        var c = new RespireEndpoint("10.0.0.3", 6381);
        SentinelHint[] hints =
        [
            SentinelHintBuilder.Create("a-to-b", NewPrimary, OldPrimary),
            SentinelHintBuilder.Create("b-to-c", c, NewPrimary),
            // Built as the router builds them: a switch without a parsed target must rediscover.
            SentinelHintBuilder.Create("untargeted-switch", OldPrimary: OldPrimary, MustRediscover: true),
            SentinelHintBuilder.Create("master-down", MustRediscover: true),
            SentinelHintBuilder.Create("gap", MustRediscover: true),
        ];
        var orderings = 0;
        foreach (var subset in Subsets(hints.Length))
        foreach (var order in Permutations(subset))
        {
            orderings++;
            var offered = order.Select(index => hints[index]).ToArray();
            var coalescer = new SentinelNotificationCoalescer();
            coalescer.Offer(SentinelHintBuilder.Create("active"), targetIsCurrent: false);
            foreach (var hint in offered) coalescer.Offer(in hint, targetIsCurrent: false);
            var pending = coalescer.Pending!.Value;
            var label = string.Join(" > ", offered.Select(hint => hint.Key));

            // A fault, a gap or an untargeted switch can never be satisfied by an earlier discovery.
            if (offered.Any(hint => hint.MustRediscover))
                await Assert.That(pending.MustRediscover).IsTrue().Because(label);
            // Arrival order cannot rank two different targets, so neither may end the worker early.
            if (offered.Select(hint => hint.Target).OfType<RespireEndpoint>().Distinct().Count() > 1)
                await Assert.That(pending.MustRediscover).IsTrue().Because(label);
            // A down event or a gap never erases a pending switch source.
            if (offered.Any(hint => hint.OldPrimary is not null))
                await Assert.That(pending.OldPrimary).IsNotNull().Because(label);
            // A target shortcut is only possible for a hint whose target was actually offered.
            if (!pending.MustRediscover)
                await Assert.That(offered.Any(hint => hint.Target == pending.Target)).IsTrue().Because(label);
        }
        await Assert.That(orderings).IsEqualTo(325);

        static IEnumerable<int[]> Subsets(int count)
        {
            for (var mask = 1; mask < 1 << count; mask++)
                yield return Enumerable.Range(0, count).Where(index => (mask & (1 << index)) != 0).ToArray();
        }

        static IEnumerable<int[]> Permutations(int[] items)
        {
            if (items.Length <= 1) { yield return items; yield break; }
            for (var index = 0; index < items.Length; index++)
                foreach (var rest in Permutations([.. items[..index], .. items[(index + 1)..]]))
                    yield return [items[index], .. rest];
        }
    }

    [Test]
    public async Task MatchingPendingAndLaterTargetsDoNotForceRediscovery()
    {
        var pending = SentinelHintBuilder.Create("a", NewPrimary, OldPrimary);
        var later = SentinelHintBuilder.Create("b", NewPrimary, new RespireEndpoint("10.0.0.9", 6379));

        var merged = SentinelNotificationCoalescer.Merge(pending, in later);

        await Assert.That(merged.MustRediscover).IsFalse();
    }

    [Test]
    public async Task LaterSwitchAwayFromThePendingTargetDoesNotInheritIt()
    {
        var pending = SentinelHintBuilder.Create("a", NewPrimary, OldPrimary);
        var later = SentinelHintBuilder.Create("b", OldPrimary: NewPrimary);

        var merged = SentinelNotificationCoalescer.Merge(pending, in later);

        await Assert.That(merged.Target).IsNull();
        await Assert.That(merged.OldPrimary).IsEqualTo(NewPrimary);
        await Assert.That(merged.MustRediscover).IsTrue();
    }

    [Test]
    public async Task FailedActiveSwitchSurvivesANewerPendingHint()
    {
        var coalescer = new SentinelNotificationCoalescer();
        var failedSwitch = SentinelHintBuilder.Create("switch", NewPrimary, OldPrimary);
        coalescer.Offer(in failedSwitch, targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("master-down", MustRediscover: true), targetIsCurrent: false);

        var next = coalescer.TakePending(activeFailed: true);

        // The down hint has no switch source, so the failed switch is kept and must be retried.
        await AssertHintEvidence(next!.Value, failedSwitch with { MustRediscover = true });
        await Assert.That(coalescer.ActiveKey).IsEqualTo("switch");
    }

    [Test]
    public async Task FailedActiveHintMakesANewerTargetedHintRediscover()
    {
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("gap", MustRediscover: true), targetIsCurrent: false);
        var laterSwitch = SentinelHintBuilder.Create("switch", NewPrimary, OldPrimary);
        coalescer.Offer(in laterSwitch, targetIsCurrent: false);

        var next = coalescer.TakePending(activeFailed: true);

        // A newer target that happens to be current must not end the worker before the failed hint is retried.
        await AssertHintEvidence(next!.Value, laterSwitch with { MustRediscover = true });
    }

    [Test]
    public async Task FailedReporterIsNotRetriedAfterItsReplacementSucceeds()
    {
        var first = new RespireEndpoint("10.0.1.1", 26379);
        var second = new RespireEndpoint("10.0.1.2", 26379);
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("gap", MustRediscover: true, ReportingSentinel: first), false);
        coalescer.Offer(SentinelHintBuilder.Create("gap", MustRediscover: true, ReportingSentinel: second), false);

        var replacement = coalescer.TakePending(activeFailed: true)!.Value;
        var afterSuccess = coalescer.TakePending(activeFailed: false);

        await Assert.That(replacement.ReportingSentinel).IsEqualTo(second);
        await Assert.That(replacement.Reporters.Skip(1)).IsEmpty();
        await Assert.That(afterSuccess).IsNull();
    }

    [Test]
    public async Task SuccessfulActiveHintLeavesTheNewerHintUnchanged()
    {
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("gap", MustRediscover: true), targetIsCurrent: false);
        var laterSwitch = SentinelHintBuilder.Create("switch", NewPrimary, OldPrimary);
        coalescer.Offer(in laterSwitch, targetIsCurrent: false);

        await Assert.That(coalescer.TakePending()).IsEqualTo(laterSwitch);
    }

    [Test]
    public async Task DuplicateSwitchFromAnotherSentinelRetainsReporter()
    {
        var first = new RespireEndpoint("10.0.0.11", 26379);
        var second = new RespireEndpoint("10.0.0.12", 26379);
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("switch", NewPrimary, OldPrimary, ReportingSentinel: first), false);
        coalescer.Offer(SentinelHintBuilder.Create("switch", NewPrimary, OldPrimary, ReportingSentinel: second), false);

        var pending = coalescer.TakePending(activeFailed: true);

        await Assert.That(pending!.Value.ReportingSentinel).IsEqualTo(second);
        await Assert.That(pending.Value.MustRediscover).IsTrue();
    }

    [Test]
    public async Task CompleteClearsActiveAndPendingHints()
    {
        var coalescer = new SentinelNotificationCoalescer();
        coalescer.Offer(SentinelHintBuilder.Create("first"), targetIsCurrent: false);
        coalescer.Offer(SentinelHintBuilder.Create("second"), targetIsCurrent: false);

        coalescer.Complete();

        await Assert.That(coalescer.ActiveKey).IsNull();
        await Assert.That(coalescer.Pending).IsNull();
        await Assert.That(coalescer.Offer(SentinelHintBuilder.Create("first"), targetIsCurrent: false)).IsTrue();
    }

    [Test]
    public async Task DiscoveryStateSignalsOnlyNewEndpoints()
    {
        var state = new SentinelDiscoveryState([new RespireEndpoint("sentinel-a", 26379)]);
        state.Snapshot(out var changed);

        await Assert.That(state.TryAdd(new RespireEndpoint("SENTINEL-A", 26379))).IsFalse();
        await Assert.That(changed.IsCompleted).IsFalse();
        await Assert.That(state.TryAdd(new RespireEndpoint("sentinel-b", 26379))).IsTrue();
        await changed.WaitAsync(TimeSpan.FromSeconds(5));

        var endpoints = state.Snapshot(out var next);
        await Assert.That(endpoints.Length).IsEqualTo(2);
        await Assert.That(next.IsCompleted).IsFalse();
    }
}
