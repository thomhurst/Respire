using Respire.Internal;

namespace Respire.Tests.Networking;

/// <summary>Constructs synthetic coalescer evidence without adding test-only production overloads.</summary>
internal static class SentinelHintBuilder
{
    internal static SentinelHint Create(string Key, RespireEndpoint[] Targets, SentinelSwitchSource[] Sources,
        RespireEndpoint[] Reporters, bool MustRediscover)
        => new(new(Key), Targets, Sources, Reporters, MustRediscover);

    internal static SentinelHint Create(string Key, RespireEndpoint? Target = null, RespireEndpoint? OldPrimary = null,
        bool MustRediscover = false, string[]? OldPrimaryAddresses = null,
        SentinelSwitchSource[]? AdditionalSources = null, RespireEndpoint? ReportingSentinel = null,
        RespireEndpoint[]? AdditionalTargets = null, RespireEndpoint[]? AdditionalReportingSentinels = null)
        => new(new(Key),
            [.. Target is { } target ? new[] { target } : [], .. AdditionalTargets ?? []],
            [.. OldPrimary is { } source ? new[] { new SentinelSwitchSource(source, OldPrimaryAddresses) } : [], .. AdditionalSources ?? []],
            [.. ReportingSentinel is { } reporter ? new[] { reporter } : [], .. AdditionalReportingSentinels ?? []],
            MustRediscover || OldPrimary is not null && Target is null);
}
