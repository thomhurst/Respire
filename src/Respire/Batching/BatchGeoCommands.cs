using Respire.Commands;
using Respire.Internal;

namespace Respire;

/// <summary>
/// Geospatial commands queued on a <see cref="RespireBatch"/> or
/// <see cref="RespireTransaction"/>. Mirrors <see cref="IGeoCommands"/>.
/// </summary>
public interface IBatchGeoCommands
{
    /// <summary>Adds members with coordinates; returns how many were new. Redis: GEOADD.</summary>
    RespirePending<long> Add(
        RespireKey key, SetWhen when = SetWhen.Always, bool changed = false,
        params ReadOnlySpan<GeoEntry> entries);

    /// <summary>The distance between two members, or null when either is absent. Redis: GEODIST.</summary>
    RespirePending<double?> Distance(
        RespireKey key, RespireValue firstMember, RespireValue secondMember, GeoUnit unit = GeoUnit.Meters);

    /// <summary>Geohash strings for members; null for absent members. Redis: GEOHASH.</summary>
    RespirePending<string?[]> Hash(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Coordinates for members; null for absent members. Redis: GEOPOS.</summary>
    RespirePending<GeoPosition?[]> Position(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Members within a shape around an origin. Redis: GEOSEARCH.</summary>
    RespirePending<GeoSearchResult[]> Search(
        RespireKey key, GeoSearchOrigin origin, GeoSearchShape shape, GeoSearchOptions options = default);

    /// <summary>Stores a search into <paramref name="destination"/>; returns its size. Redis: GEOSEARCHSTORE.</summary>
    RespirePending<long> SearchStore(
        RespireKey destination, RespireKey source, GeoSearchOrigin origin, GeoSearchShape shape,
        GeoSearchOptions options = default, bool storeDistance = false);
}

internal sealed class BatchGeoCommands(IPendingSink sink) : IBatchGeoCommands
{
    public RespirePending<long> Add(
        RespireKey key, SetWhen when = SetWhen.Always, bool changed = false,
        params ReadOnlySpan<GeoEntry> entries)
    {
        GeoCommands.ValidateAdd(when, entries);
        return sink.Add<GeoAddCommand, long>(
            "GEOADD",
            new GeoAddCommand(
                RespireCommands.Geo.GEOADD.Verb, sink.Client.Key(in key), when, changed, entries.ToArray()),
            static (c, v) => ResponseReader.Integer(in v));
    }

    public RespirePending<double?> Distance(
        RespireKey key, RespireValue firstMember, RespireValue secondMember, GeoUnit unit = GeoUnit.Meters)
        => sink.Add<Cmd4, double?>(
            "GEODIST",
            new Cmd4(
                RespireCommands.Geo.GEODIST.Verb, sink.Client.Key(in key), firstMember, secondMember,
                GeoCommands.Unit(unit)),
            static (c, v) => ResponseReader.DoubleOrNull(in v));

    public RespirePending<string?[]> Hash(RespireKey key, params ReadOnlySpan<RespireValue> members)
    {
        GeoCommands.RequireMembers(members);
        return sink.Add<Cmd1N, string?[]>(
            "GEOHASH",
            new Cmd1N(RespireCommands.Geo.GEOHASH.Verb, sink.Client.Key(in key), members.ToArray()),
            static (c, v) => ResponseReader.NullableStringArray(in v));
    }

    public RespirePending<GeoPosition?[]> Position(RespireKey key, params ReadOnlySpan<RespireValue> members)
    {
        GeoCommands.RequireMembers(members);
        return sink.Add<Cmd1N, GeoPosition?[]>(
            "GEOPOS",
            new Cmd1N(RespireCommands.Geo.GEOPOS.Verb, sink.Client.Key(in key), members.ToArray()),
            static (c, v) => GeoCommands.ParsePositions(in v));
    }

    public RespirePending<GeoSearchResult[]> Search(
        RespireKey key, GeoSearchOrigin origin, GeoSearchShape shape, GeoSearchOptions options = default)
    {
        GeoCommands.Validate(origin, shape, options);
        return sink.Add<GeoSearchCommand, GeoSearchResult[]>(
            "GEOSEARCH",
            new GeoSearchCommand(
                RespireCommands.Geo.GEOSEARCH.Verb, sink.Client.Key(in key), origin, shape, options,
                destination: null, storeDistance: false),
            // The reply's shape depends on the WITHDIST/WITHHASH/WITHCOORD flags, so this reader
            // must close over them — the one facet method that cannot use a cached static lambda.
            (c, v) => GeoCommands.ParseSearch(in v, options));
    }

    public RespirePending<long> SearchStore(
        RespireKey destination, RespireKey source, GeoSearchOrigin origin, GeoSearchShape shape,
        GeoSearchOptions options = default, bool storeDistance = false)
    {
        GeoCommands.Validate(origin, shape, options);
        GeoCommands.ValidateSearchStore(options);
        return sink.Add<GeoSearchCommand, long>(
            "GEOSEARCHSTORE",
            new GeoSearchCommand(
                RespireCommands.Geo.GEOSEARCHSTORE.Verb, sink.Client.Key(in source), origin, shape, options,
                sink.Client.Key(in destination), storeDistance),
            destination, source,
            static (c, v) => ResponseReader.Integer(in v));
    }
}
