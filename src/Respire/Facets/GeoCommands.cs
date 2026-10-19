using System.Runtime.CompilerServices;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>A distance unit accepted by Redis geospatial commands.</summary>
public enum RespireGeoUnit
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
public enum RespireGeoSortOrder
{
    /// <summary>Return results in server-selected order.</summary>
    Unsorted,

    /// <summary>Sort nearest to farthest.</summary>
    Ascending,

    /// <summary>Sort farthest to nearest.</summary>
    Descending,
}

/// <summary>A longitude, latitude, and member tuple supplied to Redis GEOADD.</summary>
public readonly record struct RespireGeoEntry(double Longitude, double Latitude, RespireValue Member);

/// <summary>A longitude and latitude returned by GEOPOS/GEOSEARCH or supplied as a polygon vertex.</summary>
public readonly record struct RespireGeoPosition(double Longitude, double Latitude);

/// <summary>A Redis GEOSEARCH result with the optional details requested by <see cref="RespireGeoSearchOptions"/>.</summary>
public readonly record struct RespireGeoSearchResult
{
    private readonly string _member;
    private readonly byte[]? _memberBytes;

    /// <summary>Creates a result from a UTF-8 text member and optional details.</summary>
    public RespireGeoSearchResult(
        string Member,
        double? Distance = null,
        long? Hash = null,
        RespireGeoPosition? Position = null)
    {
        _member = Member;
        _memberBytes = EncodeMember(Member);
        this.Distance = Distance;
        this.Hash = Hash;
        this.Position = Position;
    }

    /// <summary>The requested distance from the search origin, or null when omitted.</summary>
    public double? Distance { get; init; }

    /// <summary>The requested geohash integer, or null when omitted.</summary>
    public long? Hash { get; init; }

    /// <summary>The requested coordinates, or null when omitted.</summary>
    public RespireGeoPosition? Position { get; init; }

    /// <summary>The member decoded as UTF-8 text.</summary>
    public string Member
    {
        get => _member;
        init
        {
            _member = value;
            _memberBytes = EncodeMember(value);
        }
    }

    private static byte[]? EncodeMember(string? member)
        => member is null ? null : Encoding.UTF8.GetBytes(member);

    /// <summary>The exact Redis member payload, for binary-safe follow-up commands.</summary>
    public ReadOnlyMemory<byte> MemberBytes => _memberBytes ?? Array.Empty<byte>();

    internal RespireGeoSearchResult(
        ReadOnlySpan<byte> memberBytes,
        double? distance = null,
        long? hash = null,
        RespireGeoPosition? position = null)
    {
        _member = Encoding.UTF8.GetString(memberBytes);
        _memberBytes = memberBytes.ToArray();
        Distance = distance;
        Hash = hash;
        Position = position;
    }

    // Keep this signature aligned with the public text constructor to preserve positional-record usage.
    /// <summary>Deconstructs the member and optional details.</summary>
    public void Deconstruct(out string Member, out double? Distance, out long? Hash, out RespireGeoPosition? Position)
    {
        Member = this.Member;
        Distance = this.Distance;
        Hash = this.Hash;
        Position = this.Position;
    }

    /// <inheritdoc/>
    public bool Equals(RespireGeoSearchResult other)
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
public readonly struct RespireGeoSearchOrigin
{
    private RespireGeoSearchOrigin(RespireValue? member, double longitude, double latitude)
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
    public static RespireGeoSearchOrigin FromMember(RespireValue member) => new(member, 0, 0);

    /// <summary>Creates a Redis GEOSEARCH <c>FROMLONLAT</c> origin.</summary>
    public static RespireGeoSearchOrigin FromCoordinates(double longitude, double latitude)
        => new(null, longitude, latitude);
}

/// <summary>The circular, rectangular, or polygon area searched by GEOSEARCH.</summary>
public readonly struct RespireGeoSearchShape
{
    private RespireGeoSearchShape(double radius, double width, double height, RespireGeoUnit unit,
        RespireGeoPosition[]? vertices = null)
    {
        Radius = radius;
        Width = width;
        Height = height;
        Unit = unit;
        _vertices = vertices;
    }

    private readonly RespireGeoPosition[]? _vertices;
    internal ReadOnlySpan<RespireGeoPosition> Vertices => _vertices;
    internal bool IsPolygon => _vertices is not null;
    internal double Radius { get; }
    internal double Width { get; }
    internal double Height { get; }
    internal RespireGeoUnit Unit { get; }
    internal bool IsRadius => Radius > 0;

    /// <summary>Creates a Redis GEOSEARCH <c>BYRADIUS</c> shape.</summary>
    public static RespireGeoSearchShape Circle(double radius, RespireGeoUnit unit = RespireGeoUnit.Meters)
    {
        ValidatePositiveFinite(radius, nameof(radius));
        return new(radius, 0, 0, unit);
    }

    /// <summary>Creates a Redis GEOSEARCH <c>BYBOX</c> shape.</summary>
    public static RespireGeoSearchShape Box(double width, double height, RespireGeoUnit unit = RespireGeoUnit.Meters)
    {
        ValidatePositiveFinite(width, nameof(width));
        ValidatePositiveFinite(height, nameof(height));
        return new(0, width, height, unit);
    }

    /// <summary>Creates a Valkey 9.0+ <c>BYPOLYGON</c> shape from longitude/latitude vertices.</summary>
    /// <remarks>Copies the vertices and removes a repeated closing vertex. At least three distinct
    /// vertices are required. The server closes the ring and measures distances in meters.</remarks>
    public static RespireGeoSearchShape Polygon(params ReadOnlySpan<RespireGeoPosition> vertices)
    {
        while (vertices.Length > 1 && vertices[0] == vertices[^1])
        {
            vertices = vertices[..^1];
        }

        if (vertices.Length < 3)
        {
            throw new ArgumentException("A polygon requires at least three distinct vertices.", nameof(vertices));
        }

        var distinct = new HashSet<RespireGeoPosition>();
        foreach (var vertex in vertices)
        {
            GeoCommands.ValidateCoordinates(vertex.Longitude, vertex.Latitude);
            distinct.Add(vertex);
        }

        if (distinct.Count < 3)
        {
            throw new ArgumentException("A polygon requires at least three distinct vertices.", nameof(vertices));
        }

        return new(0, 0, 0, RespireGeoUnit.Meters, vertices.ToArray());
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
public readonly record struct RespireGeoSearchOptions
{
    /// <summary>Gets the requested distance ordering.</summary>
    public RespireGeoSortOrder Sort { get; init; }

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
public readonly record struct RespireGeoAddOptions
{
    /// <summary>Gets the condition under which members are written.</summary>
    public RespireSetWhen When { get; init; }

    /// <summary>Gets whether Redis counts both added and changed members.</summary>
    public bool Changed { get; init; }
}

/// <summary>Redis geospatial index commands.</summary>
public interface IRespireGeoCommands
{
    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    ValueTask<long> AddAsync(RespireKey key, RespireGeoEntry entry, CancellationToken cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    ValueTask<long> AddAsync(RespireKey key, RespireGeoAddOptions options, RespireGeoEntry entry, CancellationToken cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    ValueTask<string?[]> HashAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    ValueTask<RespireGeoPosition?[]> PositionAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken);

    /// <summary>Adds geospatial members and returns the number added. Redis: GEOADD.</summary>
    ValueTask<long> AddAsync(RespireKey key, params ReadOnlySpan<RespireGeoEntry> entries);

    /// <summary>Adds geospatial members and returns the number added. Redis: GEOADD.</summary>
    ValueTask<long> AddAsync(
        RespireKey key,
        ReadOnlySpan<RespireGeoEntry> entries,
        CancellationToken cancellationToken);

    /// <summary>Adds geospatial members using conditional and count options. Redis: GEOADD.</summary>
    ValueTask<long> AddAsync(
        RespireKey key, RespireGeoAddOptions options, params ReadOnlySpan<RespireGeoEntry> entries);

    /// <summary>Adds geospatial members using conditional and count options. Redis: GEOADD.</summary>
    ValueTask<long> AddAsync(
        RespireKey key,
        RespireGeoAddOptions options,
        ReadOnlySpan<RespireGeoEntry> entries,
        CancellationToken cancellationToken);

    /// <summary>Returns the distance between two members, or null when either is absent. Redis: GEODIST.</summary>
    ValueTask<double?> DistanceAsync(
        RespireKey key, RespireValue firstMember, RespireValue secondMember,
        RespireGeoUnit unit = RespireGeoUnit.Meters, CancellationToken cancellationToken = default);

    /// <summary>Returns geohash strings for members; missing members yield null. Redis: GEOHASH.</summary>
    ValueTask<string?[]> HashAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Returns geohash strings for members; missing members yield null. Redis: GEOHASH.</summary>
    ValueTask<string?[]> HashAsync(
        RespireKey key, ReadOnlySpan<RespireValue> members, CancellationToken cancellationToken);

    /// <summary>Returns coordinates for members; missing members yield null. Redis: GEOPOS.</summary>
    ValueTask<RespireGeoPosition?[]> PositionAsync(RespireKey key, params ReadOnlySpan<RespireValue> members);

    /// <summary>Returns coordinates for members; missing members yield null. Redis: GEOPOS.</summary>
    ValueTask<RespireGeoPosition?[]> PositionAsync(
        RespireKey key, ReadOnlySpan<RespireValue> members, CancellationToken cancellationToken);

    /// <summary>Searches members inside a circle or box. Redis: GEOSEARCH.</summary>
    ValueTask<RespireGeoSearchResult[]> SearchAsync(
        RespireKey key, RespireGeoSearchOrigin origin, RespireGeoSearchShape shape,
        RespireGeoSearchOptions options = default, CancellationToken cancellationToken = default);

    /// <summary>Searches a polygon without an explicit origin. Requires Valkey 9.0+.</summary>
    ValueTask<RespireGeoSearchResult[]> SearchAsync(
        RespireKey key, RespireGeoSearchShape shape,
        RespireGeoSearchOptions options = default, CancellationToken cancellationToken = default);

    /// <summary>Stores matching members or distances in another sorted set. Redis: GEOSEARCHSTORE.</summary>
    ValueTask<long> SearchStoreAsync(
        RespireKey destination, RespireKey source, RespireGeoSearchOrigin origin, RespireGeoSearchShape shape,
        RespireGeoSearchOptions options = default, bool storeDistance = false,
        CancellationToken cancellationToken = default);

    /// <summary>Stores a polygon search without an explicit origin. Requires Valkey 9.0+.</summary>
    ValueTask<long> SearchStoreAsync(
        RespireKey destination, RespireKey source, RespireGeoSearchShape shape,
        RespireGeoSearchOptions options = default, bool storeDistance = false,
        CancellationToken cancellationToken = default);
}

internal sealed class GeoCommands(RespireClient client) : IRespireGeoCommands
{
    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    public ValueTask<long> AddAsync(RespireKey key, RespireGeoEntry entry, CancellationToken cancellationToken)
        => AddAsync(key, [entry], cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    public ValueTask<long> AddAsync(RespireKey key, RespireGeoAddOptions options, RespireGeoEntry entry, CancellationToken cancellationToken)
        => AddAsync(key, options, [entry], cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    public ValueTask<string?[]> HashAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken)
        => HashAsync(key, [member], cancellationToken);

    /// <summary>Runs the command for one item with cancellation; preserves the multi-item result shape.</summary>
    public ValueTask<RespireGeoPosition?[]> PositionAsync(RespireKey key, RespireValue member, CancellationToken cancellationToken)
        => PositionAsync(key, [member], cancellationToken);


    public ValueTask<long> AddAsync(RespireKey key, params ReadOnlySpan<RespireGeoEntry> entries)
        => AddAsync(key, default, entries, CancellationToken.None);

    public ValueTask<long> AddAsync(
        RespireKey key,
        ReadOnlySpan<RespireGeoEntry> entries,
        CancellationToken cancellationToken)
        => AddAsync(key, default, entries, cancellationToken);

    public ValueTask<long> AddAsync(
        RespireKey key, RespireGeoAddOptions options, params ReadOnlySpan<RespireGeoEntry> entries)
        => AddAsync(key, options, entries, CancellationToken.None);

    public ValueTask<long> AddAsync(
        RespireKey key,
        RespireGeoAddOptions options,
        ReadOnlySpan<RespireGeoEntry> entries,
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
        RespireGeoUnit unit = RespireGeoUnit.Meters, CancellationToken cancellationToken = default)
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

    public ValueTask<RespireGeoPosition?[]> PositionAsync(
        RespireKey key, params ReadOnlySpan<RespireValue> members)
        => PositionAsync(key, members, CancellationToken.None);

    public ValueTask<RespireGeoPosition?[]> PositionAsync(
        RespireKey key, ReadOnlySpan<RespireValue> members, CancellationToken cancellationToken)
    {
        RequireMembers(members);
        return PositionCoreAsync(key, members.ToArray(), cancellationToken);
    }

    private ValueTask<RespireGeoPosition?[]> PositionCoreAsync(
        RespireKey key, RespireValue[] members, CancellationToken cancellationToken)
        => client.ConvertResponseAsync(
            "GEOPOS",
            new Cmd1N(RespireCommands.Geo.GEOPOS.Verb, client.Key(in key), members),
            cancellationToken,
            this,
            static (GeoCommands _, in RespValue value) => ParsePositions(in value));

    /// <summary>GEOPOS replies one [longitude, latitude] pair per member, null when absent.</summary>
    internal static RespireGeoPosition?[] ParsePositions(in RespValue reply)
    {
        var values = reply.AsArray();
        var result = new RespireGeoPosition?[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i].IsNull)
            {
                continue;
            }

            var coordinates = values[i].AsArray();
            result[i] = new RespireGeoPosition(
                ResponseReader.Double(in coordinates[0]), ResponseReader.Double(in coordinates[1]));
        }

        return result;
    }

    public ValueTask<RespireGeoSearchResult[]> SearchAsync(
        RespireKey key, RespireGeoSearchShape shape,
        RespireGeoSearchOptions options = default, CancellationToken cancellationToken = default)
        => SearchAsync(key, default, shape, options, cancellationToken);

    public ValueTask<long> SearchStoreAsync(
        RespireKey destination, RespireKey source, RespireGeoSearchShape shape,
        RespireGeoSearchOptions options = default, bool storeDistance = false,
        CancellationToken cancellationToken = default)
        => SearchStoreAsync(destination, source, default, shape, options, storeDistance, cancellationToken);

    public ValueTask<RespireGeoSearchResult[]> SearchAsync(
        RespireKey key, RespireGeoSearchOrigin origin, RespireGeoSearchShape shape,
        RespireGeoSearchOptions options = default, CancellationToken cancellationToken = default)
    {
        Validate(origin, shape, options);
        var response = client.ConvertResponseAsync(
            "GEOSEARCH",
            new GeoSearchCommand(
                RespireCommands.Geo.GEOSEARCH.Verb, client.Key(in key), origin, shape, options,
                destination: null, storeDistance: false),
            cancellationToken,
            options,
            static (RespireGeoSearchOptions state, in RespValue value) => ParseSearch(in value, state));
        return shape.IsPolygon ? CompletePolygonAsync(response) : response;
    }

    public ValueTask<long> SearchStoreAsync(
        RespireKey destination, RespireKey source, RespireGeoSearchOrigin origin, RespireGeoSearchShape shape,
        RespireGeoSearchOptions options = default, bool storeDistance = false,
        CancellationToken cancellationToken = default)
    {
        Validate(origin, shape, options);
        ValidateSearchStore(options);
        var response = client.IntegerAsync(
            "GEOSEARCHSTORE",
            new GeoSearchCommand(
                RespireCommands.Geo.GEOSEARCHSTORE.Verb, client.Key(in source), origin, shape, options,
                client.Key(in destination), storeDistance),
            cancellationToken);
        return shape.IsPolygon ? CompletePolygonAsync(response) : response;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<T> CompletePolygonAsync<T>(ValueTask<T> response)
    {
        try { return await response.ConfigureAwait(false); }
        catch (RespireServerException error) when (error.Message == "ERR syntax error")
        {
            throw PolygonError(error);
        }
    }

    internal static Exception PolygonError(Exception error)
        => error is RespireServerException { Message: "ERR syntax error", CommandName: "GEOSEARCH" or "GEOSEARCHSTORE" } server
            ? new RespireServerException(server.Message + " Check server support for the requested options; BYPOLYGON requires Valkey 9.0+.", server.CommandName)
                { Endpoint = server.Endpoint }
            : error;

    internal static RespireGeoSearchResult[] ParseSearch(in RespValue reply, RespireGeoSearchOptions options)
    {
        var values = reply.AsArray();
        var results = new RespireGeoSearchResult[values.Length];
        var detailed = options.IncludeDistance || options.IncludeHash || options.IncludeCoordinates;
        for (var i = 0; i < values.Length; i++)
        {
            if (!detailed)
            {
                results[i] = new RespireGeoSearchResult(values[i].AsSpan());
                continue;
            }

            var item = values[i].AsArray();
            var index = 1;
            double? distance = options.IncludeDistance ? ResponseReader.Double(in item[index++]) : null;
            long? hash = options.IncludeHash ? item[index++].AsInteger() : null;
            RespireGeoPosition? position = null;
            if (options.IncludeCoordinates)
            {
                var coordinates = item[index].AsArray();
                position = new RespireGeoPosition(
                    ResponseReader.Double(in coordinates[0]), ResponseReader.Double(in coordinates[1]));
            }

            results[i] = new RespireGeoSearchResult(item[0].AsSpan(), distance, hash, position);
        }

        return results;
    }

    /// <summary>Shared with the deferred (batch/transaction) facet.</summary>
    internal static void ValidateAdd(RespireSetWhen when, ReadOnlySpan<RespireGeoEntry> entries)
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
    internal static void ValidateSearchStore(RespireGeoSearchOptions options)
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

    internal static void Validate(RespireGeoSearchOrigin origin, RespireGeoSearchShape shape, RespireGeoSearchOptions options)
    {
        if (shape.IsPolygon && origin.IsInitialized)
        {
            throw new ArgumentException("Polygon searches cannot specify an origin.", nameof(origin));
        }

        if (!shape.IsPolygon && !origin.IsInitialized)
        {
            throw new ArgumentException(
                "Origin must be created with FromMember or FromCoordinates.", nameof(origin));
        }

        if (!shape.IsPolygon && origin.Member is null)
        {
            ValidateCoordinates(origin.Longitude, origin.Latitude);
        }

        if (shape.IsRadius)
        {
            RespireGeoSearchShape.ValidatePositiveFinite(shape.Radius, nameof(shape));
        }
        else if (!shape.IsPolygon)
        {
            RespireGeoSearchShape.ValidatePositiveFinite(shape.Width, nameof(shape));
            RespireGeoSearchShape.ValidatePositiveFinite(shape.Height, nameof(shape));
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

        if (options.Any && options.Sort != RespireGeoSortOrder.Unsorted)
        {
            throw new ArgumentException("Any cannot be combined with sorting.", nameof(options));
        }
    }

    internal static void ValidateCoordinates(double longitude, double latitude)
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

    internal static string Unit(RespireGeoUnit unit) => unit switch
    {
        RespireGeoUnit.Meters => "m",
        RespireGeoUnit.Kilometers => "km",
        RespireGeoUnit.Miles => "mi",
        RespireGeoUnit.Feet => "ft",
        _ => throw new ArgumentOutOfRangeException(nameof(unit)),
    };
}

internal readonly struct GeoSearchCommand(
    Verb verb, RespireValue source, RespireGeoSearchOrigin origin, RespireGeoSearchShape shape,
    RespireGeoSearchOptions options, RespireValue? destination, bool storeDistance) : IRespCommand
{
    internal bool IsPolygon => shape.IsPolygon;

    public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => verb.CacheMetadata;
    public RespireCacheMutation GetCacheMutation(string operation) => verb.CacheMetadata.Policy;
    public RespireCommandRetryCategory RetryCategory => verb.RetryCategory;
    public ReadCommandKind ReadKind => verb.ReadKind;

    public bool TryGetPrimaryKey(out RespireValue primaryKey)
    {
        primaryKey = source;
        return true;
    }

    public bool TryGetClientCacheKey(string operation, out ClientCacheCommandKey cacheKey)
    {
        if (options.Any)
        {
            cacheKey = default;
            return false;
        }

        RespireValue[] arguments =
        [
            source,
            origin.Member ?? RespireValue.Null,
            origin.Longitude,
            origin.Latitude,
            shape.IsRadius,
            shape.Radius,
            shape.Width,
            shape.Height,
            (int)shape.Unit,
            (int)options.Sort,
            options.Count ?? -1,
            options.Any,
            options.IncludeDistance,
            options.IncludeHash,
            options.IncludeCoordinates,
            destination ?? RespireValue.Null,
            storeDistance,
        ];
        if (shape.IsPolygon)
        {
            var polygonArguments = new RespireValue[arguments.Length + 1 + shape.Vertices.Length * 2];
            arguments.CopyTo(polygonArguments, 0);
            var index = arguments.Length;
            polygonArguments[index++] = "BYPOLYGON";
            foreach (var vertex in shape.Vertices)
            {
                polygonArguments[index++] = vertex.Longitude;
                polygonArguments[index++] = vertex.Latitude;
            }

            arguments = polygonArguments;
        }

        cacheKey = new(operation, arguments);
        return true;
    }

    public bool TryGetClusterSlot(out int slot)
        => (destination ?? source).TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
    {
        var shapeArgumentCount = shape.IsRadius ? 3 : 4;
        if (shape.IsPolygon)
        {
            shapeArgumentCount = 2 + shape.Vertices.Length * 2;
        }

        var originArgumentCount = 0;
        if (!shape.IsPolygon)
        {
            originArgumentCount = origin.Member is null ? 3 : 2;
        }
        var argumentCount = (destination.HasValue ? 1 : 0)
            + 1
            + originArgumentCount
            + shapeArgumentCount
            + (options.Sort == RespireGeoSortOrder.Unsorted ? 0 : 1)
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
        if (!shape.IsPolygon && origin.Member is { } member)
        {
            writer.WriteRaw(CommandOptionFrames.FROMMEMBER);
            member.WriteTo(ref writer);
        }
        else if (!shape.IsPolygon)
        {
            writer.WriteRaw(CommandOptionFrames.FROMLONLAT);
            ((RespireValue)origin.Longitude).WriteTo(ref writer);
            ((RespireValue)origin.Latitude).WriteTo(ref writer);
        }

        if (shape.IsPolygon)
        {
            writer.WriteBulkString("BYPOLYGON"u8);
            writer.WriteBulkInteger(shape.Vertices.Length);
            foreach (var vertex in shape.Vertices)
            {
                ((RespireValue)vertex.Longitude).WriteTo(ref writer);
                ((RespireValue)vertex.Latitude).WriteTo(ref writer);
            }
        }
        else if (shape.IsRadius)
        {
            writer.WriteRaw(CommandOptionFrames.BYRADIUS);
            ((RespireValue)shape.Radius).WriteTo(ref writer);
        }
        else
        {
            writer.WriteRaw(CommandOptionFrames.BYBOX);
            ((RespireValue)shape.Width).WriteTo(ref writer);
            ((RespireValue)shape.Height).WriteTo(ref writer);
        }

        if (!shape.IsPolygon)
        {
            writer.WriteBulkString(GeoCommands.Unit(shape.Unit));
        }

        if (options.Sort != RespireGeoSortOrder.Unsorted)
        {
            writer.WriteRaw(options.Sort == RespireGeoSortOrder.Ascending ? CommandOptionFrames.ASC : CommandOptionFrames.DESC);
        }

        if (options.Count is { } count)
        {
            writer.WriteRaw(CommandOptionFrames.COUNT);
            writer.WriteBulkInteger(count);
            if (options.Any)
            {
                writer.WriteRaw(CommandOptionFrames.ANY);
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
    Verb verb, RespireValue key, RespireSetWhen when, bool changed, RespireGeoEntry[] entries) : IRespCommand
{
    public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => verb.CacheMetadata;
    public RespireCacheMutation GetCacheMutation(string operation) => verb.CacheMetadata.Policy;
    public RespireCommandRetryCategory RetryCategory => verb.RetryCategory;
    public ReadCommandKind ReadKind => ReadCommandKind.None;

    public bool TryGetClusterSlot(out int slot) => key.TryGetClusterSlot(out slot);

    public void Write(ref RespWriter writer)
    {
        var optionCount = (when == RespireSetWhen.Always ? 0 : 1) + (changed ? 1 : 0);
        writer.WriteArrayHeader(verb.Tokens + 1 + optionCount + entries.Length * 3);
        writer.WriteRaw(verb.Bulk);
        key.WriteTo(ref writer);
        if (when != RespireSetWhen.Always)
        {
            writer.WriteBulkString(when == RespireSetWhen.NotExists ? "NX" : "XX");
        }

        if (changed)
        {
            writer.WriteRaw(CommandOptionFrames.CH);
        }

        foreach (var entry in entries)
        {
            ((RespireValue)entry.Longitude).WriteTo(ref writer);
            ((RespireValue)entry.Latitude).WriteTo(ref writer);
            entry.Member.WriteTo(ref writer);
        }
    }
}
