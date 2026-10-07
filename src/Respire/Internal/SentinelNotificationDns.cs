using System.Collections.Immutable;
using System.Net;

namespace Respire.Internal;

internal readonly partial record struct SentinelNotificationState
{
    /// <summary>
    /// Source DNS completion carries the collected target DNS answers and snapshots of the current and
    /// lookup generations. Filters unambiguous target overlap while keeping a known demoted peer, applies
    /// intervening-generation protection, and decides whether to queue rediscovery.
    /// </summary>
    /// <remarks>
    /// The ledger keeps the actual DNS answers separately from filtered source fences. An empty filtered
    /// result never manufactures demotion evidence, and an ambiguous target answer cannot prove the source
    /// is the promoted owner.
    /// </remarks>
    private SentinelNotificationTransition ResolveSourceEvidence(in SentinelNotificationEvent notification,
        in SentinelNotificationContext context)
    {
        var filtered = FilterSourceEvidence(in notification, context.LookupGeneration?.ValidatedPeer);
        var resolved = this with
        {
            Active = RetainDns(Active, in notification, filtered),
            Pending = RetainDns(Pending, in notification, filtered),
        };
        var hasLookup = SourceResolutions.TryGetValue(notification.ResolutionId, out var lookup);
        if (hasLookup)
            resolved = resolved with
            {
                SourceResolutions = SourceResolutions.SetItem(notification.ResolutionId,
                    RetainDns(lookup, in notification, filtered)!.Value),
            };
        if (filtered is null || !hasLookup || context.CurrentEvidence is not { } current
            || context.LookupGeneration is not { } arrived)
            return new(resolved, SentinelNotificationAction.None);
        // A later generation for the same endpoint or known peer is a legitimate
        // failback. An earlier lookup cannot retire it using old source evidence.
        if (!ReferenceEquals(current.Identity, arrived.Identity)
            && (SentinelEndpointIdentity.EndpointComparer.Instance.Equals(current.Endpoint, arrived.Endpoint)
                || arrived.ValidatedPeer is { } arrivedPeer && current.HasPeer(arrivedPeer)))
            return new(resolved, SentinelNotificationAction.None);
        var retained = resolved.SourceResolutions[notification.ResolutionId];
        if (!current.ShouldRetire(in retained)) return new(resolved, SentinelNotificationAction.None);
        var rediscover = resolved.Transition(new(SentinelNotificationEventKind.Offer,
            retained with { MustRediscover = true }), in context);
        return rediscover with { RetireGeneration = current.Identity };
    }

    private static SentinelHint? RetainDns(SentinelHint? hint, in SentinelNotificationEvent notification,
        SentinelAddressEvidence? filtered)
    {
        if (hint is not { } retained) return null;
        // Preserve actual DNS answers in the ledger. Filtered demotion candidates are
        // effective fences, not another completed DNS answer.
        retained = retained with { Ledger = retained.Ledger.WithDnsEvidence(notification.AddressEvidence) };
        if (!notification.TargetAddresses.IsDefaultOrEmpty)
            foreach (var target in notification.TargetAddresses)
                retained = retained with { Ledger = retained.Ledger.WithDnsEvidence(target) };
        return filtered is { } evidence ? retained.WithSourceEvidence(evidence, retainObservation: false) : retained;
    }

    private static SentinelAddressEvidence? FilterSourceEvidence(in SentinelNotificationEvent notification,
        RespireEndpoint? knownPeer)
    {
        var evidence = notification.AddressEvidence;
        if (evidence.Addresses.IsDefault || IPAddress.TryParse(evidence.Endpoint.Host, out _)
            || notification.TargetAddresses.IsDefaultOrEmpty) return evidence;
        var targets = ImmutableArray.CreateBuilder<string>();
        foreach (var target in notification.TargetAddresses)
            if (target.Endpoint.Port == evidence.Endpoint.Port && !IPAddress.TryParse(target.Endpoint.Host, out _)
                && target.SingleAddress is { } address) targets.Add(address);
        if (targets.Count == 0) return evidence;
        var filtered = ImmutableArray.CreateBuilder<string>();
        foreach (var address in evidence.Addresses)
        {
            var overlap = false;
            foreach (var target in targets)
                if (StringComparer.OrdinalIgnoreCase.Equals(address, target)) { overlap = true; break; }
            // Fresh alias overlap cannot erase a known demoted peer that still answers ROLE master.
            if (!overlap || knownPeer is { } peer && peer.Port == evidence.Endpoint.Port
                && SentinelEndpointIdentity.AddressComparer.Instance.Equals(address, peer.Host)) filtered.Add(address);
        }
        return filtered.Count == 0 ? null : SentinelAddressEvidence.FromSnapshot(evidence.Endpoint, filtered.ToImmutable());
    }
}
