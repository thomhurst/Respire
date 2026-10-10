using System.Linq.Expressions;
using FluentAssertions;
using TUnit.Core;
using static Respire.VectorData.Tests.FilterTests;

namespace Respire.VectorData.Tests;

public class FilterValidationTests
{
    private static string Getter => throw new InvalidOperationException("Getter must never execute");

    [Test]
    public void NullableNumericPromotionsPreserveFieldAndCapturedValues()
    {
        short? captured = 1;
        short? missing = null;
        Expression<Func<NullableNumericRecord, bool>>[] equalities =
        [
            r => r.Byte == 1,
            r => r.SByte == 1,
            r => r.Short == 1,
            r => r.UShort == 1,
            r => r.Int == 1.0,
            r => r.UInt == 1.0,
            r => r.Float == 1.0,
            r => r.Short == 1.0,
            r => 1 == r.Short,
            r => r.Int == captured,
        ];
        var fields = new[] { "Byte", "SByte", "Short", "UShort", "Int", "UInt", "Float" }
            .Select(name => new RespireVectorDataFilterField(name, "number", RespireVectorDataFilterKind.Numeric)).ToArray();
        foreach (var filter in equalities)
            new RespireVectorDataFilter<NullableNumericRecord>(fields).Translate(filter).Value.Should().Be("@number:[1 1]", filter.ToString());

        new RespireVectorDataFilter<NullableNumericRecord>(fields).Translate(r => r.Int == missing).Value.Should().Be("ismissing(@number)");
    }

    [Test]
    public void NullableNumericNarrowingAndUnwrappingRemainUnsupported()
    {
        Expression<Func<FilterRecord, bool>>[] filters =
        [
            r => (short?)r.Optional == 9,
            r => (int)r.Optional! == 9,
            r => checked((short?)r.Optional) == 9,
        ];
        foreach (var filter in filters)
        {
            var translate = () => new RespireVectorDataFilter<FilterRecord>(new FilterMapper().FilterFields).Translate(filter);
            translate.Should().Throw<NotSupportedException>();
        }
    }

    private sealed record NullableNumericRecord(byte? Byte, sbyte? SByte, short? Short, ushort? UShort, int? Int, uint? UInt, float? Float);

    [Test]
    public void UnsupportedExpressionsNeverExecuteOrDisappear()
    {
        Expression<Func<FilterRecord, bool>>[] filters =
        [
            r => r.String!.Contains("foo"),
            r => r.String == Getter,
            r => r.Int + 1 == 9,
            r => r.Int == r.Optional,
            r => r.Id == "id",
            r => (short)r.Int == 8,
            r => (object)r.String! == (object)"foo",
            r => r.StringArray.Contains("x", StringComparer.OrdinalIgnoreCase),
            r => r.StringList.Any(s => s.StartsWith("x")),
            r => r.Optional!.Value == 9,
            r => r.Double == double.NaN,
        ];
        foreach (var filter in filters)
        {
            var translate = () => new RespireVectorDataFilter<FilterRecord>(new FilterMapper().FilterFields).Translate(filter);
            translate.Should().Throw<NotSupportedException>();
        }
    }

    [Test]
    public void NullableValueWithNoValueFailsExplicitly()
    {
        int? missing = null;
        Expression<Func<FilterRecord, bool>> filter = r => r.Int == missing!.Value;
        var translate = () => new RespireVectorDataFilter<FilterRecord>(new FilterMapper().FilterFields).Translate(filter);
        translate.Should().Throw<InvalidOperationException>().WithMessage("Nullable object must have a value.");
    }

    [Test]
    public async Task InvalidFilterAndRetrievalOptionsFailBeforeIo()
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new FilterMapper());
        using var collection = store.GetHashCollection<FilterRecord>("records");
        Func<Task> invalid = () => Collect(collection.SearchAsync(new float[] { 1, 0 }, 1,
            new() { Filter = r => r.Id == "id", VectorProperty = r => r.Vector }));
        await invalid.Should().ThrowAsync<NotSupportedException>().WithMessage("*Id*explicit filter mapping*");
        Func<Task> nullFilter = () => Collect(collection.GetAsync((Expression<Func<FilterRecord, bool>>)null!, 1));
        await nullFilter.Should().ThrowAsync<ArgumentNullException>();
        Func<Task> top = () => Collect(collection.GetAsync(r => true, 0));
        await top.Should().ThrowAsync<ArgumentOutOfRangeException>();
        Func<Task> ordering = () => Collect(collection.GetAsync(r => true, 1, new() { OrderBy = order => order.Ascending(r => r.Int) }));
        await ordering.Should().ThrowAsync<NotSupportedException>().WithMessage("*ordering*");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Func<Task> cancel = () => Collect(collection.GetAsync(r => true, 1, cancellationToken: cancelled.Token));
        await cancel.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public void EncodingRoundTripsAndRejectsMalformedUtf16()
    {
        string[] values = ["", "foo", "FOO", "a,b", " 映画😀\r\n ", "} | * @field:{$vector}"];
        foreach (var value in values)
            RespireVectorDataFilterEncoding.DecodeTag(RespireVectorDataFilterEncoding.EncodeTag(value)).Should().Be(value);
        var invalid = () => RespireVectorDataFilterEncoding.EncodeTag("\uD800");
        invalid.Should().Throw<System.Text.EncoderFallbackException>();
    }

    [Test, Arguments("number", "NaN"), Arguments("boolean", "2"), Arguments("text", "s6f")]
    public async Task InvalidFilterStorageFailsBeforeIo(string field, string text)
    {
        await using var client = RespireClient.Create("redis://127.0.0.1:1");
        using var store = new RespireVectorStore(client);
        store.RegisterMapper(new InvalidMapper(field, text));
        using var collection = store.GetHashCollection<FilterRecord>("records");
        Func<Task> upsert = () => collection.UpsertAsync(new FilterRecord("0", 1, "ok", true, [], [], null));
        await upsert.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class InvalidMapper(string field, string text) : RespireVectorDataHashMapper<FilterRecord>
    {
        private readonly FilterMapper _mapper = new();
        public override IReadOnlyList<RespireVectorDataVectorField> VectorFields => _mapper.VectorFields;
        public override IReadOnlyList<RespireVectorDataFilterField> FilterFields => _mapper.FilterFields;
        public override string GetKey(FilterRecord record) => _mapper.GetKey(record);
        public override IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Write(FilterRecord record)
        {
            var fields = new Dictionary<string, ReadOnlyMemory<byte>>(_mapper.Write(record));
            fields[field] = System.Text.Encoding.UTF8.GetBytes(text);
            return fields;
        }
        public override FilterRecord Read(string key, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> fields) => _mapper.Read(key, fields);
    }

    private static async Task Collect<T>(IAsyncEnumerable<T> values)
    {
        await foreach (var value in values) { }
    }
}
