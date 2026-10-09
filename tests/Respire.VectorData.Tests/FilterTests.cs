// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses the original contracts under the MIT license.
// Named cases/data adapted from FilterTests.cs at the pinned 10.10.0 package source:
// https://github.com/dotnet/extensions/blob/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests/FilterTests.cs
// This supported typed subset does not establish complete upstream conformance (#1262).
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.VectorData;
using Respire.IntegrationTests;
using Respire.Search;
using TUnit.Core;

namespace Respire.VectorData.Tests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class FilterTests(ModernRedisTestContainer fixture)
{
    private const string Special = """>with $om[ specia]"chara<ters'and\stuff""";
    private static readonly int StaticNumber = 8;
    private readonly int _number = 8;
    private readonly Wrapper _wrapper = new();
    private sealed class Wrapper { public int Number = 8; }

    [Test, Arguments(2), Arguments(3)]
    public async Task PinnedUpstreamFilterContracts(int protocol)
    {
        await WithCollection(protocol, async collection =>
        {
            var data = Data();
            await collection.UpsertAsync(data);
            foreach (var (name, filter) in Cases())
            {
                // Compilation is only the test oracle; production translation never compiles expressions.
                var expected = data.Where(filter.Compile()).Select(record => record.Id).Order().ToArray();
                var search = await Collect(collection.SearchAsync(new float[] { 1, 0 }, data.Length,
                    new() { Filter = filter, VectorProperty = record => record.Vector }));
                search.Select(hit => hit.Record.Id).Order().Should().Equal(expected, name + " KNN");
                var retrieved = await Collect(collection.GetAsync(filter, data.Length));
                retrieved.Select(record => record.Id).Order().Should().Equal(expected, name + " filtered retrieval");
            }
        });
    }

    private (string Name, Expression<Func<FilterRecord, bool>> Filter)[] Cases()
    {
        var captured = 8;
        int? nullable = 8;
        int? missing = null;
        string? nullString = null;
        string[] strings = ["foo", "baz", "unknown"];
        List<string> tags = ["x", "z", "nonexistent"];
        return
        [
            ("Equal_with_int", r => r.Int == 8),
            ("Equal_with_string", r => r.String == "foo"),
            ("Equal_with_string_sql_injection_in_value", r => r.String == "foo; DROP TABLE FilterTests;"),
            ("Equal_with_string_containing_special_characters", r => r.String == Special),
            ("Equal_with_string_is_not_Contains", r => r.String == "some"),
            ("Equal_reversed", r => 8 == r.Int),
            ("Equal_with_null_reference_type", r => r.String == null),
            ("Equal_with_null_captured", r => r.String == nullString),
            ("Equal_int_property_with_nonnull_nullable_int", r => r.Int == nullable),
            ("Equal_int_property_with_null_nullable_int", r => r.Int == missing),
            ("Equal_int_property_with_nonnull_nullable_int_Value", r => r.Int == nullable.Value),
            ("NotEqual_with_int", r => r.Int != 8),
            ("NotEqual_with_string", r => r.String != "foo"),
            ("NotEqual_with_null_reference_type", r => r.String != null),
            ("NotEqual_with_null_captured", r => r.String != nullString),
            ("Bool", r => r.Bool),
            ("Bool_And_Bool", r => r.Bool && r.Bool),
            ("Bool_Or_Not_Bool", r => r.Bool || !r.Bool),
            ("GreaterThan_with_int", r => r.Int > 9),
            ("GreaterThanOrEqual_with_int", r => r.Int >= 9),
            ("LessThan_with_int", r => r.Int < 10),
            ("LessThanOrEqual_with_int", r => r.Int <= 10),
            ("Comparison_reversed", r => 9 < r.Int),
            ("And", r => r.Int == 8 && r.String == "foo"),
            ("Or", r => r.Int == 8 || r.String == "foo"),
            ("And_within_And", r => (r.Int == 8 && r.String == "foo") && r.Bool),
            ("Or_within_Or", r => (r.Int == 8 || r.String == "foo") || r.Int > 10),
            ("Or_within_And", r => (r.Int == 8 || r.String == "foo") && r.Bool),
            ("And_within_Or", r => (r.Int == 8 && r.String == "foo") || r.Int > 10),
            ("Not_over_And", r => !(r.Int == 8 && r.String == "foo")),
            ("Not_over_Or", r => !(r.Int == 8 || r.String == "foo")),
            ("Not_over_bool", r => !r.Bool),
            ("Not_over_bool_And_Comparison", r => !r.Bool && r.Int != int.MaxValue),
            ("Contains_over_field_string_array", r => r.StringArray.Contains("x")),
            ("Contains_over_field_string_List", r => r.StringList.Contains("x")),
            ("Contains_over_inline_int_array", r => new[] { 8, 10 }.Contains(r.Int)),
            ("Contains_over_inline_string_array", r => new[] { "foo", "baz", "unknown" }.Contains(r.String)),
            ("Contains_over_inline_string_array_with_weird_chars", r => new[] { "foo", "baz", "un  , ' \"" }.Contains(r.String)),
            ("Contains_over_captured_string_array", r => strings.Contains(r.String)),
            ("Contains_with_Enumerable_Contains", r => Enumerable.Contains(r.StringArray, "x")),
            ("Contains_with_MemoryExtensions_Contains", r => MemoryExtensions.Contains(r.StringArray, "x")),
#if NET10_0_OR_GREATER
            ("Contains_with_MemoryExtensions_Contains_with_null_comparer", r => MemoryExtensions.Contains(r.StringArray, "x", comparer: null)),
#endif
            ("Any_with_Contains_over_inline_string_array", r => r.StringArray.Any(s => new[] { "x", "z", "nonexistent" }.Contains(s))),
            ("Any_with_Contains_over_captured_string_list", r => r.StringArray.Any(s => tags.Contains(s))),
            ("Any_over_List", r => r.StringList.Any(s => tags.Contains(s))),
            ("Captured_local_variable", r => r.Int == captured),
            ("Member_field", r => r.Int == _number),
            ("Member_static_readonly_field", r => r.Int == StaticNumber),
            ("Member_nested_access", r => r.Int == _wrapper.Number),
            ("True", r => true),
            ("False", r => false),
            ("Nullable_null", r => r.Optional == null),
            ("Nullable_not_null", r => r.Optional != null),
            ("Nullable_boundary", r => r.Optional >= 9),
            ("Nullable_inequality_includes_missing", r => r.Optional != 9),
            ("Empty_membership", r => Array.Empty<string>().Contains(r.String)),
            ("Double_exact_boundary", r => r.Double == 0.1),
            ("Double_exclusive_boundary", r => r.Double > 0.1),
            ("Float_exact_boundary", r => r.Float == 0.1f),
            ("Float_exclusive_boundary", r => r.Float > 0.1f),
            ("Lossless_promotion", r => r.Int > 9.5),
            ("True_within_And", r => true && r.Int == 8),
            ("False_within_Or", r => false || r.Int == 8),
            ("True_within_Or", r => r.Int == 8 || true),
            ("Not_false", r => !false),
        ];
    }

    [Test, Arguments(2), Arguments(3)]
    public Task OrdinalStringsAndInjectionResistance(int protocol) => WithCollection(protocol, async collection =>
    {
        string[] values = ["", "foo", "FOO", " foo ", "a,b|c", "映画😀", "@number:[-inf +inf] | *", "} | * =>[KNN 99 @v $vector]", "a\r\nb\t\\'\"", "s"];
        await collection.UpsertAsync(values.Select((value, index) => new FilterRecord(index.ToString(), index, value, true, [value], [value], index)));
        foreach (var value in values)
        {
            (await Collect(collection.GetAsync(r => r.String == value, 100))).Should().ContainSingle().Which.String.Should().Be(value);
            (await Collect(collection.GetAsync(r => r.StringArray.Contains(value), 100))).Should().ContainSingle().Which.String.Should().Be(value);
        }
        (await Collect(collection.GetAsync(r => r.String == "missing } | *", 100))).Should().BeEmpty();
        var empty = (await collection.GetAsync("0", new() { IncludeVectors = true }))!;
        await collection.UpsertAsync(empty with { String = null });
        (await Collect(collection.GetAsync(r => r.String == "", 100))).Should().BeEmpty();
        (await Collect(collection.GetAsync(r => r.String == null, 100))).Should().ContainSingle().Which.Id.Should().Be("0");
    });

    [Test, Arguments(2), Arguments(3)]
    public Task FilteredPagingAndSelectedVector(int protocol) => WithCollection(protocol, async collection =>
    {
        await collection.UpsertAsync(Data());
        var hits = await Collect(collection.SearchAsync(new float[] { 1, 0 }, 1,
            new() { Filter = r => r.Int > 8, Skip = 1, VectorProperty = r => r.OtherVector, IncludeVectors = true }));
        hits.Should().ContainSingle().Which.Record.Id.Should().Be("2");
        hits[0].Record.OtherVector.ToArray().Should().Equal(3, 0);
        var all = await Collect(collection.GetAsync(r => r.Int > 8, 100));
        var page = await Collect(collection.GetAsync(r => r.Int > 8, 2, new() { Skip = 1, IncludeVectors = true }));
        page.Select(r => r.Id).Should().Equal(all.Skip(1).Take(2).Select(r => r.Id));
        page.Should().OnlyContain(r => !r.Vector.IsEmpty && !r.OtherVector.IsEmpty);
    });

    private async Task WithCollection(int protocol, Func<RespireVectorStoreCollection<FilterRecord>, Task> test)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        using var store = new RespireVectorStore(client, "filters:" + Guid.NewGuid().ToString("N"));
        store.RegisterMapper(new FilterMapper());
        using var collection = store.GetHashCollection<FilterRecord>("records");
        try { await collection.EnsureCollectionExistsAsync(); await test(collection); }
        finally { await collection.EnsureCollectionDeletedAsync(); }
    }

    private static FilterRecord[] Data() =>
    [
        new("0", 8, "foo", true, ["x", "y"], ["x", "y"], null) { Double = 0.1, Float = 0.1f },
        new("1", 9, "bar", false, ["a", "b"], ["a", "b"], 9) { Double = 0.2, Float = 0.2f },
        new("2", 9, "foo", true, ["x"], ["x"], 8) { Double = 0.3, Float = 0.3f },
        new("3", 10, null, false, ["x", "y", "z"], ["x", "y", "z"], 10) { Double = 0.4, Float = 0.4f },
        new("4", 11, Special, true, ["y", "z"], ["y", "z"], null) { Double = 0.5, Float = 0.5f },
    ];

    internal sealed record FilterRecord(string Id, int Int, string? String, bool Bool, string[] StringArray, List<string> StringList, int? Optional)
    {
        public double Double { get; init; }
        public float Float { get; init; }
        public ReadOnlyMemory<float> Vector { get; init; } = new float[] { 1, 0 };
        public ReadOnlyMemory<float> OtherVector { get; init; } = new float[] { int.Parse(Id, CultureInfo.InvariantCulture) + 1, 0 };
    }

    internal sealed class FilterMapper : RespireVectorDataHashMapper<FilterRecord>
    {
        public override IReadOnlyList<RespireVectorDataVectorField> VectorFields =>
        [new(nameof(FilterRecord.Vector), "v", 2) { Algorithm = RespireSearchVectorAlgorithm.Flat, DistanceMetric = RespireSearchDistanceMetric.L2 },
         new(nameof(FilterRecord.OtherVector), "other", 2) { Algorithm = RespireSearchVectorAlgorithm.Flat, DistanceMetric = RespireSearchDistanceMetric.L2 }];
        public override IReadOnlyList<RespireVectorDataFilterField> FilterFields =>
        [new(nameof(FilterRecord.Int), "number", RespireVectorDataFilterKind.Numeric),
         new(nameof(FilterRecord.String), "text", RespireVectorDataFilterKind.String),
         new(nameof(FilterRecord.Bool), "boolean", RespireVectorDataFilterKind.Boolean),
         new(nameof(FilterRecord.StringArray), "tags", RespireVectorDataFilterKind.StringCollection),
         new(nameof(FilterRecord.StringList), "list", RespireVectorDataFilterKind.StringCollection),
         new(nameof(FilterRecord.Optional), "optional", RespireVectorDataFilterKind.Numeric),
         new(nameof(FilterRecord.Double), "double", RespireVectorDataFilterKind.Numeric),
         new(nameof(FilterRecord.Float), "float", RespireVectorDataFilterKind.Numeric)];
        public override string GetKey(FilterRecord record) => record.Id;
        public override IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Write(FilterRecord record)
        {
            var fields = new Dictionary<string, ReadOnlyMemory<byte>>
            {
                ["v"] = RespireVectorDataFloat32.Encode(record.Vector.Span),
                ["other"] = RespireVectorDataFloat32.Encode(record.OtherVector.Span),
                ["number"] = Bytes(record.Int.ToString(CultureInfo.InvariantCulture)),
                ["double"] = Bytes(RespireVectorDataFilterEncoding.EncodeNumber(record.Double)),
                ["float"] = Bytes(RespireVectorDataFilterEncoding.EncodeNumber(record.Float)),
                ["boolean"] = Bytes(record.Bool ? "1" : "0"),
                ["tags"] = Bytes(RespireVectorDataFilterEncoding.EncodeTags(record.StringArray)),
                ["list"] = Bytes(RespireVectorDataFilterEncoding.EncodeTags(record.StringList)),
            };
            if (record.String is { } text) fields.Add("text", Bytes(RespireVectorDataFilterEncoding.EncodeTag(text)));
            if (record.Optional is { } number) fields.Add("optional", Bytes(number.ToString(CultureInfo.InvariantCulture)));
            return fields;
        }
        public override FilterRecord Read(string key, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> fields)
            => new(key, int.Parse(Text(fields["number"]), CultureInfo.InvariantCulture),
                fields.TryGetValue("text", out var text) ? RespireVectorDataFilterEncoding.DecodeTag(Text(text)) : null,
                Text(fields["boolean"]) == "1", Tags(fields["tags"]), Tags(fields["list"]).ToList(),
                fields.TryGetValue("optional", out var optional) ? int.Parse(Text(optional), CultureInfo.InvariantCulture) : null)
            {
                Double = double.Parse(Text(fields["double"]), CultureInfo.InvariantCulture),
                Float = (float)double.Parse(Text(fields["float"]), CultureInfo.InvariantCulture),
                Vector = fields.TryGetValue("v", out var vector) ? RespireVectorDataFloat32.Decode(vector.Span) : default,
                OtherVector = fields.TryGetValue("other", out var other) ? RespireVectorDataFloat32.Decode(other.Span) : default,
            };
        private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
        private static string Text(ReadOnlyMemory<byte> bytes) => Encoding.UTF8.GetString(bytes.Span);
        private static string[] Tags(ReadOnlyMemory<byte> bytes) => Text(bytes).Length == 0 ? [] : Text(bytes).Split(',').Select(RespireVectorDataFilterEncoding.DecodeTag).ToArray();
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> values)
    {
        var items = new List<T>();
        await foreach (var value in values) items.Add(value);
        return items;
    }
}
