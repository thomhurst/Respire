using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

public enum GeoUnit
{
    Meters,
    Kilometers,
    Miles,
    Feet,
}

public enum GeoSortOrder
{
    Unsorted,
    Ascending,
    Descending,
}

public enum GeoAddCondition
{
    Always,
    NotExists,
    Exists,
}

public readonly record struct GeoEntry(double Longitude, double Latitude, RespireValue Member);

public readonly record struct GeoPosition(double Longitude, double Latitude);

public readonly record struct GeoSearchResult(
    string Member,
    double? Distance = null,
    long? Hash = null,
    GeoPosition? Position = null);

public readonly struct GeoSearchOrigin
{
    private GeoSearchOrigin(string? member, double longitude, double latitude)
    {
        IsInitialized = true;
        Member = member;
        Longitude = longitude;
        Latitude = latitude;
    }

    internal string? Member { get; }
    internal double Longitude { get; }
    internal double Latitude { get; }
    internal bool IsInitialized { get; }

    public static GeoSearchOrigin FromMember(string member)
    {
        ArgumentException.ThrowIfNullOrEmpty(member);
        return new(member, 0, 0);
    }

    public static GeoSearchOrigin FromCoordinates(double longitude, double latitude)
        => new(null, longitude, latitude);
}

public readonly struct GeoSearchShape
{
    private GeoSearchShape(double radius, double width, double height, GeoUnit unit)
    {
        Radius = radius;
        Width = width;
        Height = height;
        Unit = unit;
    }

    internal double Radius { get; }
    internal double Width { get; }
    internal double Height { get; }
    internal GeoUnit Unit { get; }
    internal bool IsRadius => Radius > 0;

    public static GeoSearchShape Circle(double radius, GeoUnit unit = GeoUnit.Meters)
    {
        ValidatePositiveFinite(radius, nameof(radius));
        return new(radius, 0, 0, unit);
    }

    public static GeoSearchShape Box(double width, double height, GeoUnit unit = GeoUnit.Meters)
    {
        ValidatePositiveFinite(width, nameof(width));
        ValidatePositiveFinite(height, nameof(height));
        return new(0, width, height, unit);
    }

    internal static void ValidatePositiveFinite(double value, string parameterName)
    {
        if (value <= 0 || !double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value must be positive and finite.");
        }
    }
}

public readonly record struct GeoSearchOptions
{
    public GeoSortOrder Sort { get; init; }
    public int? Count { get; init; }
    public bool Any { get; init; }
    public bool IncludeDistance { get; init; }
    public bool IncludeHash { get; init; }
    public bool IncludeCoordinates { get; init; }
}

public interface IGeoCommands
{
    ValueTask<long> AddAsync(
        RespireKey key, GeoAddCondition condition = GeoAddCondition.Always, bool changed = false,
        params ReadOnlySpan<GeoEntry> entries);
    ValueTask<double?> DistanceAsync(
        RespireKey key, RespireValue firstMember, RespireValue secondMember,
        GeoUnit unit = GeoUnit.Meters, CancellationToken cancellationToken = default);
    ValueTask<string?[]> HashAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);
    ValueTask<GeoPosition?[]> PositionAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);
    ValueTask<GeoSearchResult[]> SearchAsync(
        RespireKey key, GeoSearchOrigin origin, GeoSearchShape shape,
        GeoSearchOptions options = default, CancellationToken cancellationToken = default);
    ValueTask<long> SearchStoreAsync(
        RespireKey destination, RespireKey source, GeoSearchOrigin origin, GeoSearchShape shape,
        GeoSearchOptions options = default, bool storeDistance = false,
        CancellationToken cancellationToken = default);
}

internal sealed class GeoCommands(RespireClient client) : IGeoCommands
{
    public ValueTask<long> AddAsync(
        RespireKey key, GeoAddCondition condition = GeoAddCondition.Always, bool changed = false,
        params ReadOnlySpan<GeoEntry> entries)
    {
        if (entries.IsEmpty)
        {
            throw new ArgumentException("At least one geo entry is required.", nameof(entries));
        }

        if (!Enum.IsDefined(condition))
        {
            throw new ArgumentOutOfRangeException(nameof(condition));
        }

        foreach (var entry in entries)
        {
            ValidateCoordinates(entry.Longitude, entry.Latitude);
        }

        return client.IntegerAsync(
            "GEOADD",
            new GeoAddCommand(
                RespireCommands.Geo.GEOADD.Verb, client.Key(in key), condition, changed, entries.ToArray()),
            CancellationToken.None);
    }

    public ValueTask<double?> DistanceAsync(
        RespireKey key, RespireValue firstMember, RespireValue secondMember,
        GeoUnit unit = GeoUnit.Meters, CancellationToken cancellationToken = default)
    {
        var unitToken = Unit(unit);
        return client.DoubleOrNullAsync(
            "GEODIST",
            new Cmd4(
                RespireCommands.Geo.GEODIST.Verb, client.Key(in key), firstMember, secondMember, unitToken),
            cancellationToken);
    }

    public ValueTask<string?[]> HashAsync(RespireKey key, params ReadOnlySpan<RespireValue> members)
    {
        RequireMembers(members);
        return client.NullableStringArrayAsync(
            "GEOHASH",
            new Cmd1N(RespireCommands.Geo.GEOHASH.Verb, client.Key(in key), members.ToArray()),
            CancellationToken.None);
    }

    public ValueTask<GeoPosition?[]> PositionAsync(
        RespireKey key, params ReadOnlySpan<RespireValue> members)
    {
        RequireMembers(members);
        return PositionCoreAsync(key, members.ToArray());
    }

    private async ValueTask<GeoPosition?[]> PositionCoreAsync(RespireKey key, RespireValue[] members)
    {
        var reply = await client.SendAsync(
            "GEOPOS",
            new Cmd1N(RespireCommands.Geo.GEOPOS.Verb, client.Key(in key), members),
            CancellationToken.None).ConfigureAwait(false);
        try
        {
            var values = reply.AsArray();
            var result = new GeoPosition?[values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i].IsNull)
                {
                    continue;
                }

                var coordinates = values[i].AsArray();
                result[i] = new GeoPosition(
                    ResponseReader.Double(in coordinates[0]), ResponseReader.Double(in coordinates[1]));
            }

            return result;
        }
        finally
        {
            reply.Dispose();
        }
    }

    public async ValueTask<GeoSearchResult[]> SearchAsync(
        RespireKey key, GeoSearchOrigin origin, GeoSearchShape shape,
        GeoSearchOptions options = default, CancellationToken cancellationToken = default)
    {
        Validate(origin, shape, options);
        var reply = await client.SendAsync(
            "GEOSEARCH",
            new GeoSearchCommand(
                RespireCommands.Geo.GEOSEARCH.Verb, client.Key(in key), origin, shape, options,
                destination: null, storeDistance: false),
            cancellationToken).ConfigureAwait(false);
        try
        {
            return ParseSearch(in reply, options);
        }
        finally
        {
            reply.Dispose();
        }
    }

    public ValueTask<long> SearchStoreAsync(
        RespireKey destination, RespireKey source, GeoSearchOrigin origin, GeoSearchShape shape,
        GeoSearchOptions options = default, bool storeDistance = false,
        CancellationToken cancellationToken = default)
    {
        Validate(origin, shape, options);
        if (options.IncludeDistance || options.IncludeHash || options.IncludeCoordinates)
        {
            throw new ArgumentException("GEOSEARCHSTORE cannot return distance, hash, or coordinates.", nameof(options));
        }

        return client.IntegerAsync(
            "GEOSEARCHSTORE",
            new GeoSearchCommand(
                RespireCommands.Geo.GEOSEARCHSTORE.Verb, client.Key(in source), origin, shape, options,
                client.Key(in destination), storeDistance),
            cancellationToken);
    }

    private static GeoSearchResult[] ParseSearch(in RespValue reply, GeoSearchOptions options)
    {
        var values = reply.AsArray();
        var results = new GeoSearchResult[values.Length];
        var detailed = options.IncludeDistance || options.IncludeHash || options.IncludeCoordinates;
        for (var i = 0; i < values.Length; i++)
        {
            if (!detailed)
            {
                results[i] = new GeoSearchResult(values[i].AsString());
                continue;
            }

            var item = values[i].AsArray();
            var index = 1;
            double? distance = options.IncludeDistance ? ResponseReader.Double(in item[index++]) : null;
            long? hash = options.IncludeHash ? item[index++].AsInteger() : null;
            GeoPosition? position = null;
            if (options.IncludeCoordinates)
            {
                var coordinates = item[index].AsArray();
                position = new GeoPosition(
                    ResponseReader.Double(in coordinates[0]), ResponseReader.Double(in coordinates[1]));
            }

            results[i] = new GeoSearchResult(item[0].AsString(), distance, hash, position);
        }

        return results;
    }

    private static void RequireMembers(ReadOnlySpan<RespireValue> members)
    {
        if (members.IsEmpty)
        {
            throw new ArgumentException("At least one member is required.", nameof(members));
        }
    }

    private static void Validate(GeoSearchOrigin origin, GeoSearchShape shape, GeoSearchOptions options)
    {
        if (!origin.IsInitialized)
        {
            throw new ArgumentException(
                "Origin must be created with FromMember or FromCoordinates.", nameof(origin));
        }

        if (origin.Member is null)
        {
            ValidateCoordinates(origin.Longitude, origin.Latitude);
        }

        if (shape.IsRadius)
        {
            GeoSearchShape.ValidatePositiveFinite(shape.Radius, nameof(shape));
        }
        else
        {
            GeoSearchShape.ValidatePositiveFinite(shape.Width, nameof(shape));
            GeoSearchShape.ValidatePositiveFinite(shape.Height, nameof(shape));
        }

        _ = Unit(shape.Unit);
        if (!Enum.IsDefined(options.Sort))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Sort is invalid.");
        }

        if (options.Count is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Count must be positive.");
        }

        if (options.Any && options.Count is null)
        {
            throw new ArgumentException("Any requires Count.", nameof(options));
        }

        if (options.Any && options.Sort != GeoSortOrder.Unsorted)
        {
            throw new ArgumentException("Any cannot be combined with sorting.", nameof(options));
        }
    }

    private static void ValidateCoordinates(double longitude, double latitude)
    {
        if (longitude is < -180 or > 180 || double.IsNaN(longitude))
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), "Longitude must be between -180 and 180.");
        }

        if (latitude is < -85.05112878 or > 85.05112878 || double.IsNaN(latitude))
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), "Latitude must be between -85.05112878 and 85.05112878.");
        }
    }

    internal static string Unit(GeoUnit unit) => unit switch
    {
        GeoUnit.Meters => "m",
        GeoUnit.Kilometers => "km",
        GeoUnit.Miles => "mi",
        GeoUnit.Feet => "ft",
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };
}

internal readonly struct GeoSearchCommand(
    Verb verb, RespireValue source, GeoSearchOrigin origin, GeoSearchShape shape,
    GeoSearchOptions options, RespireValue? destination, bool storeDistance) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        var argumentCount = (destination.HasValue ? 1 : 0)
            + 1
            + (origin.Member is null ? 3 : 2)
            + (shape.IsRadius ? 3 : 4)
            + (options.Sort == GeoSortOrder.Unsorted ? 0 : 1)
            + (options.Count.HasValue ? 2 + (options.Any ? 1 : 0) : 0)
            + (options.IncludeDistance ? 1 : 0)
            + (options.IncludeHash ? 1 : 0)
            + (options.IncludeCoordinates ? 1 : 0)
            + (storeDistance ? 1 : 0);
        writer.WriteArrayHeader(verb.Tokens + argumentCount);
        writer.WriteRaw(verb.Bulk);
        if (destination is { } destinationKey)
        {
            destinationKey.WriteTo(ref writer);
        }

        source.WriteTo(ref writer);
        if (origin.Member is { } member)
        {
            writer.WriteBulkString("FROMMEMBER"u8);
            writer.WriteBulkString(member);
        }
        else
        {
            writer.WriteBulkString("FROMLONLAT"u8);
            ((RespireValue)origin.Longitude).WriteTo(ref writer);
            ((RespireValue)origin.Latitude).WriteTo(ref writer);
        }

        if (shape.IsRadius)
        {
            writer.WriteBulkString("BYRADIUS"u8);
            ((RespireValue)shape.Radius).WriteTo(ref writer);
        }
        else
        {
            writer.WriteBulkString("BYBOX"u8);
            ((RespireValue)shape.Width).WriteTo(ref writer);
            ((RespireValue)shape.Height).WriteTo(ref writer);
        }

        writer.WriteBulkString(GeoCommands.Unit(shape.Unit));
        if (options.Sort != GeoSortOrder.Unsorted)
        {
            writer.WriteBulkString(options.Sort == GeoSortOrder.Ascending ? "ASC"u8 : "DESC"u8);
        }

        if (options.Count is { } count)
        {
            writer.WriteBulkString("COUNT"u8);
            writer.WriteBulkInteger(count);
            if (options.Any)
            {
                writer.WriteBulkString("ANY"u8);
            }
        }

        WriteFlag(ref writer, options.IncludeDistance, "WITHDIST"u8);
        WriteFlag(ref writer, options.IncludeHash, "WITHHASH"u8);
        WriteFlag(ref writer, options.IncludeCoordinates, "WITHCOORD"u8);
        WriteFlag(ref writer, storeDistance, "STOREDIST"u8);
    }

    private static void WriteFlag(ref RespWriter writer, bool enabled, ReadOnlySpan<byte> value)
    {
        if (enabled)
        {
            writer.WriteBulkString(value);
        }
    }
}

internal readonly struct GeoAddCommand(
    Verb verb, RespireValue key, GeoAddCondition condition, bool changed, GeoEntry[] entries) : IRespCommand
{
    public void Write(ref RespWriter writer)
    {
        var optionCount = (condition == GeoAddCondition.Always ? 0 : 1) + (changed ? 1 : 0);
        writer.WriteArrayHeader(verb.Tokens + 1 + optionCount + entries.Length * 3);
        writer.WriteRaw(verb.Bulk);
        key.WriteTo(ref writer);
        if (condition != GeoAddCondition.Always)
        {
            writer.WriteBulkString(condition == GeoAddCondition.NotExists ? "NX" : "XX");
        }

        if (changed)
        {
            writer.WriteBulkString("CH"u8);
        }

        foreach (var entry in entries)
        {
            ((RespireValue)entry.Longitude).WriteTo(ref writer);
            ((RespireValue)entry.Latitude).WriteTo(ref writer);
            entry.Member.WriteTo(ref writer);
        }
    }
}
