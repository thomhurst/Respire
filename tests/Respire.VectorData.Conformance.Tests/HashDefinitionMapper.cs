using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.VectorData;
using Respire.Search;

namespace Respire.VectorData.Conformance.Tests;

// Reflection belongs only to this non-AOT upstream fixture adapter. The shipped connector
// still receives an explicit mapper and exercises its real encoding, schema and query paths.
internal sealed class HashDefinitionMapper<TRecord> : RespireVectorDataHashMapper<TRecord> where TRecord : class
{
    private readonly PropertyInfo _key;
    private readonly (VectorStoreProperty Definition, PropertyInfo Property)[] _properties;

    public HashDefinitionMapper(VectorStoreCollectionDefinition definition)
    {
        _key = typeof(TRecord).GetProperty(definition.Properties.OfType<VectorStoreKeyProperty>().Single().Name)!;
        _properties = definition.Properties.Where(p => p is not VectorStoreKeyProperty)
            .Select(p => (p, typeof(TRecord).GetProperty(p.Name)!)).ToArray();
        VectorFields = definition.Properties.OfType<VectorStoreVectorProperty>().Select(p =>
            new RespireVectorDataVectorField(p.Name, p.Name, p.Dimensions)
            {
                Algorithm = p.IndexKind switch
                {
                    null or IndexKind.Flat => RespireSearchVectorAlgorithm.Flat,
                    IndexKind.Hnsw => RespireSearchVectorAlgorithm.Hnsw,
                    _ => throw new NotSupportedException(p.IndexKind),
                },
                DistanceMetric = p.DistanceFunction switch
                {
                    null or DistanceFunction.CosineDistance => RespireSearchDistanceMetric.Cosine,
                    DistanceFunction.EuclideanSquaredDistance => RespireSearchDistanceMetric.L2,
                    DistanceFunction.NegativeDotProductSimilarity => RespireSearchDistanceMetric.InnerProduct,
                    _ => throw new NotSupportedException(p.DistanceFunction),
                },
            }).ToArray();
        var data = definition.Properties.OfType<VectorStoreDataProperty>().ToArray();
        DataFields = data.Where(p => p.IsFullTextIndexed).Select(p => new RespireSearchField(p.Name, RespireSearchFieldType.Text)).ToArray();
        TextFields = data.Where(p => p.IsFullTextIndexed).Select(p => new RespireVectorDataTextField(p.Name, p.Name)).ToArray();
        FilterFields = data.Where(p => p.IsIndexed).Select(p => new RespireVectorDataFilterField(p.Name, p.Name, p.Type switch
        {
            var type when type == typeof(string) => RespireVectorDataFilterKind.String,
            var type when type == typeof(string[]) || type == typeof(List<string>) => RespireVectorDataFilterKind.StringCollection,
            var type when type == typeof(bool) => RespireVectorDataFilterKind.Boolean,
            _ => RespireVectorDataFilterKind.Numeric,
        })).ToArray();
    }

    public override IReadOnlyList<RespireVectorDataVectorField> VectorFields { get; }
    public override IReadOnlyList<RespireSearchField> DataFields { get; }
    public override IReadOnlyList<RespireVectorDataTextField> TextFields { get; }
    public override IReadOnlyList<RespireVectorDataFilterField> FilterFields { get; }
    public override string GetKey(TRecord record) => (string)_key.GetValue(record)!;

    public override IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Write(TRecord record)
    {
        var fields = new Dictionary<string, ReadOnlyMemory<byte>>();
        foreach (var (definition, property) in _properties)
        {
            var value = property.GetValue(record);
            if (value is null) continue;
            if (definition is VectorStoreVectorProperty)
            {
                ReadOnlyMemory<float> vector = value switch
                {
                    ReadOnlyMemory<float> memory => memory,
                    float[] array => array,
                    _ => throw new NotSupportedException("The fixture maps raw FLOAT32 vectors only."),
                };
                fields[definition.Name] = RespireVectorDataFloat32.Encode(vector.Span);
                continue;
            }
            var indexed = ((VectorStoreDataProperty)definition).IsIndexed;
            var text = value switch
            {
                string s => indexed ? RespireVectorDataFilterEncoding.EncodeTag(s) : s,
                IEnumerable<string> tags => RespireVectorDataFilterEncoding.EncodeTags(tags),
                bool boolean => boolean ? "1" : "0",
                DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
                DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
                TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            };
            fields[definition.Name] = Encoding.UTF8.GetBytes(text);
        }
        return fields;
    }

    public override TRecord Read(string key, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> fields)
    {
        var record = Activator.CreateInstance<TRecord>();
        _key.SetValue(record, key);
        foreach (var (definition, property) in _properties)
        {
            if (!fields.TryGetValue(definition.Name, out var bytes)) continue;
            if (definition is VectorStoreVectorProperty)
            {
                var vector = RespireVectorDataFloat32.Decode(bytes.Span);
                property.SetValue(record, property.PropertyType == typeof(float[]) ? (object)vector.ToArray() : vector);
                continue;
            }
            var text = Encoding.UTF8.GetString(bytes.Span);
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object value;
            if (type == typeof(string)) value = ((VectorStoreDataProperty)definition).IsIndexed ? RespireVectorDataFilterEncoding.DecodeTag(text) : text;
            else if (type == typeof(bool)) value = text == "1";
            else if (type == typeof(string[]) || type == typeof(List<string>))
            {
                var tags = text.Length == 0 ? [] : text.Split(',').Select(RespireVectorDataFilterEncoding.DecodeTag).ToArray();
                value = type == typeof(string[]) ? tags : tags.ToList();
            }
            else if (type == typeof(Guid)) value = Guid.Parse(text);
            else if (type == typeof(DateTime)) value = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            else if (type == typeof(DateTimeOffset)) value = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
            else if (type == typeof(DateOnly)) value = DateOnly.Parse(text, CultureInfo.InvariantCulture);
            else if (type == typeof(TimeOnly)) value = TimeOnly.Parse(text, CultureInfo.InvariantCulture);
            else value = Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
            property.SetValue(record, value);
        }
        return record;
    }
}
