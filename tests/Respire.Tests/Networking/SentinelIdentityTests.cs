using System.Runtime.CompilerServices;
using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelIdentityTests
{
    [Test]
    public async Task DefaultAddressEvidenceCannotMatchAnObservation()
    {
        var missing = default(SentinelAddressEvidence);
        var observed = new SentinelAddressEvidence(new("primary.test", 6379), ["192.0.2.1"]);
        await Assert.That(missing.CouldMatch(missing)).IsFalse();
        await Assert.That(missing.CouldMatch(observed)).IsFalse();
        await Assert.That(observed.CouldMatch(missing)).IsFalse();
        await Assert.That(missing.ConfirmsPeer(observed.Endpoint)).IsFalse();
        await Assert.That(missing.SingleAddress).IsNull();
        await Assert.That(observed.CouldMatch(observed)).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task SwitchSourceEqualityAndHashingDoNotAllocate()
    {
        var source = new SentinelSwitchSource(new("primary.test", 6379), ["192.0.2.1"]);
        var same = SentinelSwitchSource.FromSnapshot(source.Endpoint, source.Addresses);
        var other = SentinelSwitchSource.FromSnapshot(new("primary.test", 6380), source.Addresses);
        await Assert.That(source.Equals(same)).IsTrue();
        await Assert.That(source.Equals(other)).IsFalse();
        _ = MeasureSourceEquality(source, same, false);
        _ = MeasureSourceEquality(source, same, true);
        var (allocated, control) = AllocationMeasurement.WithoutConcurrentGc(() =>
            (MeasureSourceEquality(source, same, false), MeasureSourceEquality(source, same, true)));
        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(control).IsGreaterThanOrEqualTo(37_000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureSourceEquality(SentinelSwitchSource source, SentinelSwitchSource same, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            if (!source.Equals(same) || source.GetHashCode() != same.GetHashCode())
                throw new InvalidOperationException("Equivalent source snapshots must remain equal.");
            if (allocate) GC.KeepAlive(AllocateControl());
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public async Task RuntimeShorthandUsesTheSameIdentityAsTheConnectedPeer()
    {
        await using var server = new FakeRespServer(1);
        using var socket = new System.Net.Sockets.TcpClient();
        await socket.ConnectAsync("127.1", server.Port).WaitAsync(TimeSpan.FromSeconds(5));
        var remote = (System.Net.IPEndPoint)socket.Client.RemoteEndPoint!;
        await Assert.That(new SentinelEndpointIdentity(new("127.1", server.Port)))
            .IsEqualTo(new SentinelEndpointIdentity(new(remote.Address.ToString(), remote.Port)));
    }

    [Test]
    public async Task DefaultIdentityRetainsEqualityAndHashing()
    {
        var first = default(SentinelEndpointIdentity);
        var second = default(SentinelEndpointIdentity);
        await Assert.That(first.Equals(second)).IsTrue();
        await Assert.That(first.GetHashCode()).IsEqualTo(second.GetHashCode());
    }

    [Test]
    [NotInParallel]
    public async Task RepeatedNormalizedEvidenceMatchingDoesNotAllocate()
    {
        var sourceAddresses = Enumerable.Range(1, 16).Select(index => $"::ffff:192.0.2.{index}").ToArray();
        var source = new SentinelAddressEvidence(new("former.test", 6379), sourceAddresses);
        var candidate = new SentinelAddressEvidence(new("primary.test", 6379),
            [.. Enumerable.Range(1, 15).Select(index => $"198.51.100.{index}"), "192.0.2.16"]);
        await Assert.That(sourceAddresses[15]).IsEqualTo("::ffff:192.0.2.16");
        await Assert.That(source.Addresses![15]).IsEqualTo("192.0.2.16");
        sourceAddresses[15] = "203.0.113.99";
        await Assert.That(source.Addresses[15]).IsEqualTo("192.0.2.16");
        var alreadyNormalized = SentinelAddressEvidence.FromSnapshot(source.Endpoint, source.Addresses);
        await Assert.That(alreadyNormalized.Addresses == source.Addresses).IsTrue();
        _ = MeasureMatching(source, candidate, false);
        _ = MeasureMatching(source, candidate, true);
        var (allocated, control) = AllocationMeasurement.WithoutConcurrentGc(() =>
            (MeasureMatching(source, candidate, false), MeasureMatching(source, candidate, true)));
        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(control).IsGreaterThanOrEqualTo(37_000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureMatching(SentinelAddressEvidence source, SentinelAddressEvidence candidate, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            if (!source.CouldMatch(candidate)) throw new InvalidOperationException("The final address must match.");
            if (allocate) GC.KeepAlive(AllocateControl());
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public async Task ConfigurationSnapshotAndConfirmationUseCanonicalNumericIdentity()
    {
        var addressReads = 0;
        await using var reporter = new FakeRespServer(2, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER")
                ? Encoding.UTF8.GetBytes($"*2\r\n+{(Interlocked.Increment(ref addressReads) == 1 ? "192.0.2.1" : "::ffff:192.0.2.1")}\r\n+6379\r\n")
                : command == "SENTINEL MASTER mymaster"
                    ? "*8\r\n+ip\r\n+::ffff:c000:201\r\n+port\r\n+6379\r\n+config-epoch\r\n+2\r\n+flags\r\n+master\r\n"u8.ToArray()
                    : "*0\r\n"u8.ToArray(),
        };
        var options = new RespireOptions
        {
            Protocol = RespProtocol.Resp2, SentinelPrimaryName = "mymaster",
            Endpoints = [new("127.0.0.1", reporter.Port)],
        };
        var selected = await SentinelResolver.ResolveAndConnectPrimaryAsync(options,
            (candidate, _, _) => ValueTask.FromResult(candidate.PrimaryEndpoint), CancellationToken.None);
        await Assert.That(selected).IsEqualTo(new RespireEndpoint("192.0.2.1", 6379));
        await Assert.That(addressReads).IsEqualTo(2);
    }

    [Test]
    [NotInParallel]
    public async Task DuplicateAddressEvidenceDoesNotAllocate()
    {
        var source = new RespireEndpoint("former.test", 6379);
        var hint = SentinelHint.FromSwitchMaster("switch", source, new("primary.test", 6379), new("sentinel.test", 26379))
            .WithSourceAddresses(source, ["192.0.2.1"]);
        // Equal text in a different string ensures the fast path does not rely on interning.
        string[] addresses = [new("192.0.2.1".ToCharArray())];
        _ = MeasureDuplicate(hint, source, addresses, false);
        _ = MeasureDuplicate(hint, source, addresses, true);
        var (allocated, control) = AllocationMeasurement.WithoutConcurrentGc(() =>
            (MeasureDuplicate(hint, source, addresses, false), MeasureDuplicate(hint, source, addresses, true)));
        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(control).IsGreaterThanOrEqualTo(37_000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureDuplicate(SentinelHint hint, RespireEndpoint source, string[] addresses, bool allocate)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            var duplicate = hint.WithSourceAddresses(source, addresses);
            GC.KeepAlive(duplicate.Sources[0].Endpoint.Host);
            if (allocate) GC.KeepAlive(AllocateControl());
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object AllocateControl() => new byte[37];

    [Test]
    [Arguments("PRIMARY.test", "primary.TEST", true)]
    [Arguments("192.0.2.1", "::ffff:192.0.2.1", true)]
    [Arguments("::ffff:c000:201", "192.0.2.1", true)]
    [Arguments("2001:0db8:0:0:0:0:0:1", "2001:db8::1", true)]
    [Arguments("primary.test", "other.test", false)]
    [Arguments("192.0.2.1", "192.0.2.2", false)]
    [Arguments("1", "0.0.0.1", true)]
    [Arguments("127.1", "127.0.0.1", true)]
    [Arguments("fe80:0:0:0:0:0:0:1%3", "fe80::1%3", true)]
    [Arguments("fe80::1%3", "fe80::1%4", false)]
    public async Task TextualIdentityIsConsistentAcrossKeysOwnersAndDiscovery(string leftHost, string rightHost, bool same)
    {
        var left = new RespireEndpoint(leftHost, 6379);
        var right = new RespireEndpoint(rightHost, 6379);
        var first = new SentinelEndpointIdentity(left);
        var second = new SentinelEndpointIdentity(right);
        await Assert.That(first.Equals(second)).IsEqualTo(same);
        await Assert.That(second.Equals(first)).IsEqualTo(same);
        await Assert.That(SentinelEndpointIdentity.EndpointComparer.Instance.Equals(left, right)).IsEqualTo(same);
        await Assert.That(new SentinelHintKey("down", left) == new SentinelHintKey("down", right)).IsEqualTo(same);
        await Assert.That(new SentinelValidatedPrimary(left, left) == new SentinelValidatedPrimary(right, right)).IsEqualTo(same);
        if (same)
        {
            await Assert.That(first.GetHashCode()).IsEqualTo(second.GetHashCode());
            await Assert.That(new SentinelValidatedPrimary(left, left).GetHashCode())
                .IsEqualTo(new SentinelValidatedPrimary(right, right).GetHashCode());
        }
        var state = new SentinelDiscoveryState([left]);
        await Assert.That(state.TryAdd(right)).IsEqualTo(!same);
        await Assert.That(state.TryAdd(new(rightHost, 6380))).IsTrue();
        await Assert.That(first.Equals(new SentinelEndpointIdentity(new(leftHost, 6380)))).IsFalse();
    }

    [Test]
    public async Task SeededAddressSetsSeparatePossibleDemotionFromOwnershipProof()
    {
        var random = new Random(794);
        for (var iteration = 0; iteration < 128; iteration++)
        {
            var port = random.Next(1000, 60000);
            var host = $"192.0.2.{random.Next(1, 200)}";
            var peer = new RespireEndpoint(host, port);
            var mapped = "::ffff:" + host;
            var candidates = Enumerable.Range(0, random.Next(1, 6))
                .Select(_ => random.Next(2) == 0 ? host : mapped).ToArray();
            var evidence = new SentinelAddressEvidence(new("primary.test", port), candidates);
            await Assert.That(evidence.ConfirmsPeer(peer)).IsTrue();
            await Assert.That(evidence.ConfirmsPeer(new(mapped, port))).IsTrue();
            await Assert.That(evidence.ConfirmsPeer(new(host, port + 1))).IsFalse();
            await Assert.That(evidence.ConfirmsSameAddress(new(new("alias.test", port), [mapped]))).IsTrue();

            var ambiguous = new SentinelAddressEvidence(evidence.Endpoint, [.. candidates, "198.51.100.1"]);
            await Assert.That(ambiguous.SingleAddress).IsNull();
            await Assert.That(ambiguous.CouldMatch(new(peer, null))).IsTrue();
            await Assert.That(ambiguous.ConfirmsPeer(peer)).IsFalse();
            await Assert.That(ambiguous.ConfirmsSameAddress(evidence)).IsFalse();
            await Assert.That(evidence.ConfirmsSameAddress(ambiguous)).IsFalse();
            await Assert.That(ambiguous.CouldMatch(new(new(host, port + 1), null))).IsFalse();
            await Assert.That(new SentinelValidatedPrimary(evidence.Endpoint, peer).Matches(evidence.Endpoint, ambiguous.Addresses.ToArray())).IsFalse();
        }
    }

    [Test]
    public async Task CanonicalUnionsRetainSingleReporterSourceTargetAndAddress()
    {
        var source = new RespireEndpoint("former.test", 6379);
        var target = new RespireEndpoint("192.0.2.2", 6379);
        var reporter = new RespireEndpoint("SENTINEL.test", 26379);
        var first = SentinelHint.FromSwitchMaster("switch", source, target, reporter)
            .WithSourceAddresses(source, ["192.0.2.1"]);
        var second = SentinelHint.FromSwitchMaster("switch", new("FORMER.TEST", 6379),
            new("::ffff:192.0.2.2", 6379), new("sentinel.TEST", 26379))
            .WithSourceAddresses(source, ["::ffff:192.0.2.1"]);
        var merged = SentinelNotificationCoalescer.Merge(first, in second);
        await Assert.That(merged.Sources.Length).IsEqualTo(1);
        await Assert.That(merged.Targets.Length).IsEqualTo(1);
        await Assert.That(merged.Reporters.Length).IsEqualTo(1);
        await Assert.That(merged.Sources[0].Addresses!.Length).IsEqualTo(1);
        await Assert.That(merged.Sources[0].Evidence.ConfirmsPeer(new("192.0.2.1", 6379))).IsTrue();
        await Assert.That(first.Sources ==
            first.WithSourceAddresses(source, ["::ffff:192.0.2.1"]).Sources).IsTrue();
        await Assert.That(first.Targets == merged.Targets).IsTrue();
        await Assert.That(first.Reporters == merged.Reporters).IsTrue();
    }

    [Test]
    public async Task ObservedOwnerAliasesDeduplicateWithoutRebindingToNextOwner()
    {
        var endpoint = new RespireEndpoint("primary.test", 6379);
        var reporter = new RespireEndpoint("sentinel.test", 26379);
        var owner = new SentinelValidatedPrimary(endpoint, new("192.0.2.1", 6379));
        var alias = new SentinelValidatedPrimary(new("PRIMARY.TEST", 6379), new("::ffff:192.0.2.1", 6379));
        var nextOwner = new SentinelValidatedPrimary(endpoint, new("192.0.2.2", 6379));
        var first = SentinelHint.FromDown("down", reporter, endpoint, owner);
        var duplicate = SentinelHint.FromDown("down", reporter, endpoint, alias);
        var merged = SentinelNotificationCoalescer.Merge(first, in duplicate);
        await Assert.That(first.DownReports == merged.DownReports).IsTrue();
        var later = SentinelHint.FromDown("down", reporter, endpoint, nextOwner);
        merged = SentinelNotificationCoalescer.Merge(merged, in later);
        await Assert.That(merged.DownReports.Length).IsEqualTo(2);
        await Assert.That(merged.DownReports[0].OwnerAtObservation).IsEqualTo(owner);
        await Assert.That(owner.Matches(endpoint, ["192.0.2.2"])).IsFalse();
        await Assert.That(nextOwner.Matches(endpoint, ["192.0.2.2"])).IsTrue();
        await Assert.That(owner.Matches(new("::ffff:192.0.2.1", 6379), null)).IsTrue();
        await Assert.That(nextOwner.Matches(owner.Peer!.Value, null)).IsFalse();
    }
}
