using Respire.Json;
using Respire.Search;
using System.Text.Json.Serialization;

namespace Respire.Tests.Models;

[RespireHash("generated-hash:{Id}")]
[RespireSearch("generated-hash-v1", Prefixes = ["generated-hash:"])]
internal partial record SearchHashModel(string Id,
    [property: RespireSearchField(RespireSearchFieldType.Text, Alias = "title", NoStem = true)] string Title,
    [property: RespireSearchField(RespireSearchFieldType.Tag, Alias = "category", Separator = '|')] string Category,
    [property: RespireSearchField(RespireSearchFieldType.Numeric, Alias = "rating", Sortable = true)] int Rating,
    [property: RespireSearchField(RespireSearchFieldType.Vector, Alias = "embedding", Dimensions = 2)] byte[] Embedding);

[RespireJson("generated-json:{Id}")]
[RespireSearch("generated-json-v1", Prefixes = ["generated-json:"])]
internal partial record SearchJsonModel(string Id,
    [property: JsonPropertyName("document.title")]
    [property: RespireSearchField(RespireSearchFieldType.Text, Alias = "title", NoStem = true)] string Title,
    [property: RespireSearchField(RespireSearchFieldType.Tag, Alias = "category", CaseSensitive = true)] string Category,
    [property: RespireSearchField(RespireSearchFieldType.Numeric, Alias = "rating", Sortable = true)] int Rating,
    [property: JsonPropertyName("vector")]
    [property: RespireSearchField(RespireSearchFieldType.Vector, Alias = "embedding", Dimensions = 2)] float[]? Embedding);

[RespireJson("generated-double:{Id}")]
[RespireSearch("generated-double-v1", Prefixes = ["generated-double:"])]
internal partial record SearchDoubleModel(string Id,
    [property: RespireSearchField(RespireSearchFieldType.Vector, Dimensions = 2, VectorType = RespireSearchVectorType.Float64)] double[] Embedding);

[RespireHash("generated-expiring:{Id}")]
[RespireSearch("generated-expiring-v1", Prefixes = ["generated-expiring:"])]
internal partial record SearchExpiringHashModel(string Id,
    [property: RespireFieldTtl(60000)]
    [property: RespireSearchField(RespireSearchFieldType.Vector, Dimensions = 2)] byte[] Embedding);
