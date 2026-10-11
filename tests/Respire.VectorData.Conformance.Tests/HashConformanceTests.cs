using Microsoft.Extensions.VectorData;
using VectorData.ConformanceTests;
using VectorData.ConformanceTests.ModelTests;
using Xunit;

namespace Respire.VectorData.Conformance.Tests;

// Overrides only tag capabilities explicitly rejected by the shipped connector.
// CI excludes this trait; all applicable inherited tests and their assertions run unchanged.
public abstract class SupportedBasicTests(BasicModelTests<string>.Fixture fixture) : BasicModelTests<string>(fixture)
{
    [Fact, Trait("Unsupported", "FilteredOrdering")]
    public override Task GetAsync_with_filter_and_OrderBy() => base.GetAsync_with_filter_and_OrderBy();
    [Fact, Trait("Unsupported", "FilteredOrdering")]
    public override Task GetAsync_with_filter_and_multiple_OrderBys() => base.GetAsync_with_filter_and_multiple_OrderBys();
    [Fact, Trait("Unsupported", "FilteredOrdering")]
    public override Task GetAsync_with_filter_and_OrderBy_and_Skip() => base.GetAsync_with_filter_and_OrderBy_and_Skip();
}

public abstract class SupportedFilterTests(FilterTests<string>.Fixture fixture) : FilterTests<string>(fixture)
{
    // This upstream fact contains only a dynamic branch; TestDynamic=false would make it a no-op.
    [Fact, Trait("Unsupported", "DynamicCollections")]
    public override Task Equal_with_string_sql_injection_in_name() => base.Equal_with_string_sql_injection_in_name();
    // Upstream captures fixture.SpecialCharactersText as a property getter, which production
    // deliberately never invokes. Literal/captured-field special characters remain covered locally.
    [Fact, Trait("Unsupported", "CapturedPropertyGetters")]
    public override Task Equal_with_string_containing_special_characters() => base.Equal_with_string_containing_special_characters();
}

public abstract class SupportedDistanceTests(DistanceFunctionTests<string>.Fixture fixture) : DistanceFunctionTests<string>(fixture)
{
    [Fact, Trait("Unsupported", "DistanceMetric")]
    public override Task CosineSimilarity() => base.CosineSimilarity();
    [Fact, Trait("Unsupported", "DistanceMetric")]
    public override Task DotProductSimilarity() => base.DotProductSimilarity();
    [Fact, Trait("Unsupported", "DistanceMetric")]
    public override Task EuclideanDistance() => base.EuclideanDistance();
    [Fact, Trait("Unsupported", "DistanceMetric")]
    public override Task HammingDistance() => base.HammingDistance();
    [Fact, Trait("Unsupported", "DistanceMetric")]
    public override Task ManhattanDistance() => base.ManhattanDistance();
}

public abstract class SupportedIndexTests(IndexKindTests<string>.Fixture fixture) : IndexKindTests<string>(fixture)
{
    [Fact]
    public Task Hnsw() => Test(IndexKind.Hnsw);
}

[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp2BasicTests(BasicResp2Fixture fixture) : SupportedBasicTests(fixture), IClassFixture<BasicResp2Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp3BasicTests(BasicResp3Fixture fixture) : SupportedBasicTests(fixture), IClassFixture<BasicResp3Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp2NoDataTests(NoDataResp2Fixture fixture) : NoDataModelTests<string>(fixture), IClassFixture<NoDataResp2Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp3NoDataTests(NoDataResp3Fixture fixture) : NoDataModelTests<string>(fixture), IClassFixture<NoDataResp3Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp2MultiVectorTests(MultiVectorResp2Fixture fixture) : MultiVectorModelTests<string>(fixture), IClassFixture<MultiVectorResp2Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp3MultiVectorTests(MultiVectorResp3Fixture fixture) : MultiVectorModelTests<string>(fixture), IClassFixture<MultiVectorResp3Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp2FilterTests(FilterResp2Fixture fixture) : SupportedFilterTests(fixture), IClassFixture<FilterResp2Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp3FilterTests(FilterResp3Fixture fixture) : SupportedFilterTests(fixture), IClassFixture<FilterResp3Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp2DistanceTests(DistanceResp2Fixture fixture) : SupportedDistanceTests(fixture), IClassFixture<DistanceResp2Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp3DistanceTests(DistanceResp3Fixture fixture) : SupportedDistanceTests(fixture), IClassFixture<DistanceResp3Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp2IndexTests(IndexResp2Fixture fixture) : SupportedIndexTests(fixture), IClassFixture<IndexResp2Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp3IndexTests(IndexResp3Fixture fixture) : SupportedIndexTests(fixture), IClassFixture<IndexResp3Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp2HybridTests(HybridResp2Fixture fixture, MultiTextResp2Fixture multiText)
    : HybridSearchTests<string>(fixture, multiText), IClassFixture<HybridResp2Fixture>, IClassFixture<MultiTextResp2Fixture>;
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp3HybridTests(HybridResp3Fixture fixture, MultiTextResp3Fixture multiText)
    : HybridSearchTests<string>(fixture, multiText), IClassFixture<HybridResp3Fixture>, IClassFixture<MultiTextResp3Fixture>;
