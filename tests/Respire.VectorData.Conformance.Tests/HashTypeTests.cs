using System.Linq.Expressions;
using Microsoft.Extensions.VectorData;
using VectorData.ConformanceTests.Support;
using Xunit;
using DataRecord = Respire.VectorData.Conformance.Tests.PinnedTyped.DataTypeTests<string>.DefaultRecord;

namespace Respire.VectorData.Conformance.Tests;

public abstract class SupportedDataTypeTests(PinnedTyped.DataTypeTests<string, DataRecord>.Fixture fixture)
    : PinnedTyped.DataTypeTests<string, DataRecord>(fixture)
{
    // Upstream exposes isFilterable specifically for providers with narrower filter types.
    // Keep CRUD/null assertions for every data type; disable only unsupported equality filters.
    protected override Task Test<TTestType>(string propertyName, TTestType mainValue, TTestType otherValue,
        bool isFilterable = true, Action<TTestType, TTestType>? comparisonAction = null,
        Expression<Func<TTestType>>? instantiationExpression = null)
        => base.Test(propertyName, mainValue, otherValue, isFilterable && DataTypeFixture.SupportsFiltering(typeof(TTestType)), comparisonAction, instantiationExpression);
}

public abstract class SupportedEmbeddingTypeTests(PinnedTyped.EmbeddingTypeTests<string>.Fixture fixture)
    : PinnedTyped.EmbeddingTypeTests<string>(fixture)
{
    [Fact, Trait("Unsupported", "EmbeddingInput")]
    public override Task Embedding_of_float() => base.Embedding_of_float();
}

public abstract class SupportedKeyTypeTests(PinnedTyped.KeyTypeTests.Fixture fixture) : PinnedTyped.KeyTypeTests(fixture)
{
    // Upstream exposes this exact typed contract as a protected method for connector key types.
    [Fact]
    public Task String() => Test("first", "second");
}

[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp2DataTypeTests(DataTypeResp2Fixture fixture) : SupportedDataTypeTests(fixture), IClassFixture<DataTypeResp2Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp3DataTypeTests(DataTypeResp3Fixture fixture) : SupportedDataTypeTests(fixture), IClassFixture<DataTypeResp3Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp2EmbeddingTypeTests(EmbeddingTypeResp2Fixture fixture) : SupportedEmbeddingTypeTests(fixture), IClassFixture<EmbeddingTypeResp2Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp3EmbeddingTypeTests(EmbeddingTypeResp3Fixture fixture) : SupportedEmbeddingTypeTests(fixture), IClassFixture<EmbeddingTypeResp3Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp2KeyTypeTests(KeyTypeResp2Fixture fixture) : SupportedKeyTypeTests(fixture), IClassFixture<KeyTypeResp2Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp3KeyTypeTests(KeyTypeResp3Fixture fixture) : SupportedKeyTypeTests(fixture), IClassFixture<KeyTypeResp3Fixture>;

public abstract class DataTypeFixture(RedisServerFixture server, RespProtocol protocol) : PinnedTyped.DataTypeTests<string, DataRecord>.Fixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
    public override IList<VectorStoreDataProperty> GetDataProperties()
    {
        var properties = base.GetDataProperties();
        foreach (var property in properties) property.IsIndexed = SupportsFiltering(property.Type);
        return properties;
    }

    internal static bool SupportsFiltering(Type? type)
    {
        if (type is null) return false;
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(float)
            || type == typeof(double) || type == typeof(string) || type == typeof(bool);
    }
}
public sealed class DataTypeResp2Fixture(RedisServerFixture server) : DataTypeFixture(server, RespProtocol.Resp2);
public sealed class DataTypeResp3Fixture(RedisServerFixture server) : DataTypeFixture(server, RespProtocol.Resp3);

public abstract class EmbeddingTypeFixture(RedisServerFixture server, RespProtocol protocol) : PinnedTyped.EmbeddingTypeTests<string>.Fixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class EmbeddingTypeResp2Fixture(RedisServerFixture server) : EmbeddingTypeFixture(server, RespProtocol.Resp2);
public sealed class EmbeddingTypeResp3Fixture(RedisServerFixture server) : EmbeddingTypeFixture(server, RespProtocol.Resp3);

public abstract class KeyTypeFixture(RedisServerFixture server, RespProtocol protocol) : PinnedTyped.KeyTypeTests.Fixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class KeyTypeResp2Fixture(RedisServerFixture server) : KeyTypeFixture(server, RespProtocol.Resp2);
public sealed class KeyTypeResp3Fixture(RedisServerFixture server) : KeyTypeFixture(server, RespProtocol.Resp3);
