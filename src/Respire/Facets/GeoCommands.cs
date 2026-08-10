using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>A distance unit accepted by Redis geospatial commands.</summary>
public enum GeoUnit
{
    /// <summary>Meters (<c>m</c>).</summary>
    Meters,

    /// <summary>Kilometers (<c>km</c>).</summary>
    Kilometers,

    /// <summary>Miles (<c>mi</c>).</summary>
    Miles,

    /// <summary>Feet (<c>ft</c>).</summary>
    Feet,
}

/// <summary>The result ordering requested from Redis GEOSEARCH.</summary>
public enum GeoSortOrder
{
    /// <summary>Return results in server-selected order.</summary>
    Unsorted,

    /// <summary>Sort nearest to farthest.</summary>
    Ascending,

    /// <summary>Sort farthest to nearest.</summary>
    Descending,
}

/// <summary>A longitude, latitude, and member tuple supplied to Redis GEOADD.</summary>
public readonly record struct GeoEntry(double Longitude, double Latitude, RespireValue Member);

/// <summary>A longitude and latitude returned by Redis GEOPOS or GEOSEARCH.</summary>
public readonly record struct GeoPosition(double Longitude, double Latitude);

/// <summary>A Redis GEOSEARCH result with the optional details requested by <see cref="GeoSearchOptions"/>.</summary>
public readonly record struct GeoSearchResult(
    string Member,
    double? Distance = null,
    long? Hash = null,
    GeoPosition? Position = null)
{
    private readonly string _member = Member;
    private readonly byte[]? _memberBytes = Member is null ? null : Encoding.UTF8.GetBytes(Member);

    /// <summary>The member decoded as UTF-8 text.</summary>
    public string Member
    {
        get => _member;
        init
        {
            _member = value;
            _memberBytes = value is null ? null : Encoding.UTF8.GetBytes(value);
        }
    }

    /// <summary>The exact Redis member payload, for binary-safe follow-up commands.</summary>
    public ReadOnlyMemory<byte> MemberBytes => _memberBytes ?? Array.Empty<byte>();

    internal GeoSearchResult(
        ReadOnlySpan<byte> memberBytes,
        double? distance = null,
        long? hash = null,
        GeoPosition? position = null)
        : this(Encoding.UTF8.GetString(memberBytes), distance, hash, position)
        => _memberBytes = memberBytes.ToArray();

    /// <inheritdoc/>
    public bool Equals(GeoSearchResult other)
        => Member == other.Member
            && Distance == other.Distance
            && Hash == other.Hash
            && Position == other.Position
            && MemberBytes.Span.SequenceEqual(other.MemberBytes.Span);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Member);
        hash.Add(Distance);
        hash.Add(Hash);
        hash.Add(Position);
        hash.AddBytes(MemberBytes.Span);
        return hash.ToHashCode();
    }
}

/// <summary>The member or coordinates from which Redis GEOSEARCH measures distance.</summary>
public readonly struct GeoSearchOrigin
{
    private GeoSearchOrigin(RespireValue? member, double longitude, double latitude)
    {
        IsInitialized = true;
        Member = member;
        Longitude = longitude;
        Latitude = latitude;
    }

    internal RespireValue? Member { get; }
    internal double Longitude { get; }
    internal double Latitude { get; }
    internal bool IsInitialized { get; }

    /// <summary>Creates a Redis GEOSEARCH <c>FROMMEMBER</c> origin.</summary>
    public static GeoSearchOrigin FromMember(RespireValue member) => new(member, 0, 0);

    /// <summary>Creates a Redis GEOSEARCH <c>FROMLONLAT</c> origin.</summary>
    public static GeoSearchOrigin FromCoordinates(double longitude, double latitude)
        => new(null, longitude, latitude);
}

/// <summary>The circular or rectangular area searched by Redis GEOSEARCH.</summary>
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

    /// <summary>Creates a Redis GEOSEARCH <c>BYRADIUS</c> shape.</summary>
    public static GeoSearchShape Circle(double radius, GeoUnit unit = GeoUnit.Meters)
    {
        ValidatePositiveFinite(radius, nameof(radius));
        return new(radius, 0, 0, unit);
    }

    /// <summary>Creates a Redis GEOSEARCH <c>BYBOX</c> shape.</summary>
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

/// <summary>Sorting, limiting, and optional detail fields for Redis GEOSEARCH.</summary>
public readonly record struct GeoSearchOptions
{
    /// <summary>Gets the requested distance ordering.</summary>
    public GeoSortOrder Sort { get; init; }

    /// <summary>Gets the maximum number of results, or null for no limit.</summary>
    public int? Count { get; init; }

    /// <summary>Gets whether Redis may stop after any <see cref="Count"/> matches.</summary>
    public bool Any { get; init; }

    /// <summary>Gets whether each result includes its distance. Redis: GEOSEARCH WITHDIST.</summary>
    public bool IncludeDistance { get; init; }

    /// <summary>Gets whether each result includes its geohash integer. Redis: GEOSEARCH WITHHASH.</summary>
    public bool IncludeHash { get; init; }

    /// <summary>Gets whether each result includes coordinates. Redis: GEOSEARCH WITHCOORD.</summary>
    public bool IncludeCoordinates { get; init; }
}

/// <summary>Options controlling GEOADD conditional writes and changed-count reporting.</summary>
public readonly record struct GeoAddOptions
{
    /// <summary>Gets the condition under which members are written.</summary>
    public SetWhen When { get; init; }

    /// <summary>Gets whether Redis counts both added and changed members.</summary>
    public bool Changed { get; init; }
}

/// <summary>Redis geospatial index commands.</summary>
public interface IGeoCommands
{
    /// <summary>Adds geospatial members and returns the number added. Redis: GEOADD.</summary>
    ValueTask<long> AddAsync(RespireKey key, params ReadOnlySpan<GeoEntry> entries);

    /// <summary>Adds geospatial members and returns the number added. Redis: GEOADD.</summary>
    ValueTask<long> AddAsync(
        RespireKey key,
        ReadOnlySpan<GeoEntry> entries,
        CancellationToken cancellationToken);

    /// <summary>Adds geospatial members using conditional and count options. Redis: GEOADD.</summary>
    ValueTask<long> AddAsync(
        RespireKey key, GeoAddOptions options, params ReadOnlySpan<GeoEntry> entries);

    /// <summary>Adds geospatial members using conditional and count options. Redis: GEOADD.</summary>
    ValueTask<long> AddAsync(
        RespireKey key,
        GeoAddOptions options,
        ReadOnlySpan<GeoEntry> entries,
        CancellationToken cancellationToken);

    /// <summary>Returns the distance between two members, or null when either is absent. Redis: GEODIST.</summary>
    ValueTask<double?> DistanceAsync(
        RespireKey key, RespireValue firstMember, RespireValue secondMember,
        GeoUnit unit = GeoUnit.Meters, CancellationToken cancellationToken = default);

    /// <summary>Returns geohash strings for members; missing members yield null. Redis: GEOHASH.</summary>
    ValueTask<string?[]> HashAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Returns geohash strings for members; missing members yield null. Redis: GEOHASH.</summary>
    ValueTask<string?[]> HashAsync(
        RespireKey key, ReadOnlySpan<RespireValue> members, CancellationToken cancellationToken);

    /// <summary>Returns coordinates for members; missing members yield null. Redis: GEOPOS.</summary>
    ValueTask<GeoPosition?[]> PositionAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Returns coordinates for members; missing members yield null. Redis: GEOPOS.</summary>
    ValueTask<GeoPosition?[]> PositionAsync(
        RespireKey key, ReadOnlySpan<RespireValue> members, CancellationToken cancellationToken);

    /// <summary>Searches members inside a circle or box. Redis: GEOSEARCH.</summary>
    ValueTask<GeoSearchResult[]> SearchAsync(
        RespireKey key, GeoSearchOrigin origin, GeoSearchShape shape,
        GeoSearchOptions options = default, CancellationToken cancellationToken = default);

    /// <summary>Stores matching members or distances in another sorted set. Redis: GEOSEARCHSTORE.</summary>
    ValueTask<long> SearchStoreAsync(
        RespireKey destination, RespireKey source, GeoSearchOrigin origin, GeoSearchShape shape,
        GeoSearchOptions options = default, bool storeDistance = false,
        CancellationToken cancellationToken = default);
}

internal sealed class GeoCommands(RespireClient client) : IGeoCommands
{
    public ValueTask<long> AddAsync(RespireKey key, params ReadOnlySpan<GeoEntry> entries)
        => AddAsync(key, default, entries, CancellationToken.None);

    public ValueTask<long> AddAsync(
        RespireKey key,
        ReadOnlySpan<GeoEntry> entries,
        CancellationToken cancellationToken)
        => AddAsync(key, default, entries, cancellationToken);

    public ValueTask<long> AddAsync(
        RespireKey key, GeoAddOptions options, params ReadOnlySpan<GeoEntry> entries)
        => AddAsync(key, options, entries, CancellationToken.None);

    public ValueTask<long> AddAsync(
        RespireKey key,
        GeoAddOptions options,
        ReadOnlySpan<GeoEntry> entries,
        CancellationToken cancellationToken)
    {
        ValidateAdd(options.When, entries);
        return client.IntegerAsync(
            "GEOADD",
            new GeoAddCommand(
                RespireCommands.Geo.GEOADD.Verb,
                client.Key(in key),
                options.When,
                options.Changed,
                entries.ToArray()),
            cancellationToken);
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
        => HashAsync(key, members, CancellationToken.None);

    public ValueTask<string?[]> HashAsync(
        RespireKey key, ReadOnlySpan<RespireValue> members, CancellationToken cancellationToken)
    {
        RequireMembers(members);
        return client.NullableStringArrayAsync(
            "GEOHASH",
            new Cmd1N(RespireCommands.Geo.GEOHASH.Verb, client.Key(in key), members.ToArray()),
            cancellationToken);
    }

    public ValueTask<GeoPosition?[]> PositionAsync(
        RespireKey key, params ReadOnlySpan<RespireValue> members)
        => PositionAsync(key, members, CancellationToken.None);

    public ValueTask<GeoPosition?[]> PositionAsync(
        RespireKey key, ReadOnlySpan<RespireValue> members, CancellationToken cancellationToken)
    {
        RequireMembers(members);
        return PositionCoreAsync(key, members.ToArray(), cancellationToken);
    }

    private async ValueTask<GeoPosition?[]> PositionCoreAsync(
        RespireKey key, RespireValue[] members, CancellationToken cancellationToken)
    {
        var reply = await client.SendAsync(
            "GEOPOS",
            new Cmd1N(RespireCommands.Geo.GEOPOS.Verb, client.Key(in key), members),
            cancellationToken).ConfigureAwait(false);
        try
        {
            return ParsePositions(in reply);
        }
        finally
        {
            reply.Dispose();
        }
    }

    /// <summary>GEOPOS replies one [longitude, latitude] pair per member, null when absent.</summary>
    internal static GeoPosition?[] ParsePositions(in RespValue reply)
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
        ValidateSearchStore(options);
        return client.IntegerAsync(
            "GEOSEARCHSTORE",
            new GeoSearchCommand(
                RespireCommands.Geo.GEOSEARCHSTORE.Verb, client.Key(in source), origin, shape, options,
                client.Key(in destination), storeDistance),
            cancellationToken);
    }

    internal static GeoSearchResult[] ParseSearch(in RespValue reply, GeoSearchOptions options)
    {
        var values = reply.AsArray();
        var results = new GeoSearchResult[values.Length];
        var detailed = options.IncludeDistance || options.IncludeHash || options.IncludeCoordinates;
        for (var i = 0; i < values.Length; i++)
        {
            if (!detailed)
            {
                results[i] = new GeoSearchResult(values[i].AsSpan());
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

            results[i] = new GeoSearchResult(item[0].AsSpan(), distance, hash, position);
        }

        return results;
    }

    /// <summary>Shared with the deferred (batch/transaction) facet.</summary>
    internal static void ValidateAdd(SetWhen when, ReadOnlySpan<GeoEntry> entries)
    {
        if (entries.IsEmpty)
        {
            throw new ArgumentException("At least one geo entry is required.", nameof(entries));
        }

        if (!Enum.IsDefined(when))
        {
            throw new ArgumentOutOfRangeException(nameof(when));
        }

        foreach (var entry in entries)
        {
            ValidateCoordinates(entry.Longitude, entry.Latitude);
        }
    }

    /// <summary>Shared with the deferred (batch/transaction) facet.</summary>
    internal static void ValidateSearchStore(GeoSearchOptions options)
    {
        if (options.IncludeDistance || options.IncludeHash || options.IncludeCoordinates)
        {
            throw new ArgumentException("GEOSEARCHSTORE cannot return distance, hash, or coordinates.", nameof(options));
        }
    }

    internal static void RequireMembers(ReadOnlySpan<RespireValue> members)
    {
        if (members.IsEmpty)
        {
            throw new ArgumentException("At least one member is required.", nameof(members));
        }
    }

    internal static void Validate(GeoSearchOrigin origin, GeoSearchShape shape, GeoSearchOptions options)
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
    public bool TryGetClusterSlot(out int slot)
        => (destination ?? source).TryGetClusterSlot(out slot);

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
            member.WriteTo(ref writer);
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
    Verb verb, RespireValue key, SetWhen when, bool changed, GeoEntry[] entries) : IRespCommand
{
    public bool TryGetClusterSlot(out int slot) => key.TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
    {
        var optionCount = (when == SetWhen.Always ? 0 : 1) + (changed ? 1 : 0);
        writer.WriteArrayHeader(verb.Tokens + 1 + optionCount + entries.Length * 3);
        writer.WriteRaw(verb.Bulk);
        key.WriteTo(ref writer);
        if (when != SetWhen.Always)
        {
            writer.WriteBulkString(when == SetWhen.NotExists ? "NX" : "XX");
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
