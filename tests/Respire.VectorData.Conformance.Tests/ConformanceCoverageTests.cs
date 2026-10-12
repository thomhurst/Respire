using System.Reflection;
using VectorData.ConformanceTests;
using VectorData.ConformanceTests.ModelTests;
using VectorData.ConformanceTests.TypeTests;
using Xunit;

namespace Respire.VectorData.Conformance.Tests;

// Run the official completeness assertion, not a local count-only substitute.
public sealed class ConformanceCoverageTests : TestSuiteImplementationTests
{
    // Reviewed against package 10.10.0, source 02107c65bab30aad9e35b5133ed643eaa77bccd8.
    // See the backend/capability matrix in website/docs/guides/vector-data.md.
    private static readonly Dictionary<Type, string> UnsupportedBases = new()
    {
        [typeof(DynamicModelTests<>)] = "GetDynamicCollection rejects dynamic dictionaries.",
        [typeof(NoVectorModelTests<>)] = "Collection schemas require at least one vector.",
        [typeof(DependencyInjectionTests<>)] = "No VectorData registration delegates are shipped.",
        [typeof(DependencyInjectionTests<,,,>)] = "No VectorData registration delegates are shipped.",
        [typeof(EmbeddingGenerationTests<>)] = "No embedding generators; the no-generator fact runs separately on both backends.",
        [typeof(KeyTypeTests)] = "Only explicit string keys; the pinned typed string helper runs on hashes.",
        [typeof(EmbeddingTypeTests<>)] = "Mandatory dynamic/generator steps; pinned typed FLOAT32 contracts run on hashes.",
        [typeof(DataTypeTests<>)] = "Mandatory dynamic steps; pinned typed data CRUD/null/filter contracts run on hashes.",
        [typeof(DataTypeTests<,>)] = "Mandatory dynamic steps; pinned typed data CRUD/null/filter contracts run on hashes.",
    };

    protected override ICollection<Type> IgnoredTestBases => UnsupportedBases.Keys.ToArray();

    private static readonly (Type Suite, Type Resp2, Type Resp3, string[] Excluded)[] HashSuites =
    [
        (typeof(CollectionManagementTests<string>), typeof(HashResp2CollectionManagementTests), typeof(HashResp3CollectionManagementTests), []),
        (typeof(BasicModelTests<string>), typeof(HashResp2BasicTests), typeof(HashResp3BasicTests),
            ["GetAsync_with_filter_and_OrderBy", "GetAsync_with_filter_and_multiple_OrderBys", "GetAsync_with_filter_and_OrderBy_and_Skip"]),
        (typeof(NoDataModelTests<string>), typeof(HashResp2NoDataTests), typeof(HashResp3NoDataTests), []),
        (typeof(MultiVectorModelTests<string>), typeof(HashResp2MultiVectorTests), typeof(HashResp3MultiVectorTests), []),
        (typeof(FilterTests<string>), typeof(HashResp2FilterTests), typeof(HashResp3FilterTests),
            ["Equal_with_string_sql_injection_in_name", "Equal_with_string_containing_special_characters"]),
        (typeof(DistanceFunctionTests<string>), typeof(HashResp2DistanceTests), typeof(HashResp3DistanceTests),
            ["CosineSimilarity", "DotProductSimilarity", "EuclideanDistance", "HammingDistance", "ManhattanDistance"]),
        (typeof(IndexKindTests<string>), typeof(HashResp2IndexTests), typeof(HashResp3IndexTests), []),
        (typeof(HybridSearchTests<string>), typeof(HashResp2HybridTests), typeof(HashResp3HybridTests), []),
    ];

    [Fact]
    public void Every_upstream_base_has_a_reviewed_disposition()
    {
        var reviewed = HashSuites.Select(s => s.Suite.GetGenericTypeDefinition())
            .Concat(UnsupportedBases.Keys).Append(typeof(TestSuiteImplementationTests)).ToHashSet();
        AssertReviewed(GetBaseTestClasses(), reviewed);
        Assert.All(UnsupportedBases.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Theory]
    [InlineData(RespProtocol.Resp2)]
    [InlineData(RespProtocol.Resp3)]
    public void Every_applicable_hash_fact_is_discoverable(RespProtocol protocol)
    {
        foreach (var (suite, resp2, resp3, excluded) in HashSuites)
        {
            var adapter = protocol == RespProtocol.Resp2 ? resp2 : resp3;
            AssertDiscoverable(adapter);
            Assert.True(suite.IsAssignableFrom(adapter));
            var upstream = Facts(suite).Select(m => m.Name).ToHashSet();
            Assert.All(excluded, name => Assert.Contains(name, upstream));
            var applicable = Facts(adapter).Where(m => !IsExcluded(m)).Select(m => m.Name).ToHashSet();
            AssertCovered(upstream.Except(excluded), applicable);
            Assert.Equal(excluded.Order(), Facts(adapter).Where(IsExcluded).Select(m => m.Name).Order());
            Assert.All(Facts(adapter).Where(m => !IsExcluded(m)), m => Assert.Null(m.GetCustomAttribute<FactAttribute>()!.Skip));
            if (suite == typeof(IndexKindTests<string>)) AssertRunnable(adapter.GetMethod("Hnsw")!);
        }
        foreach (var (upstream, resp2, resp3) in new (Type?, Type, Type)[]
        {
            (typeof(DataTypeTests<string, DataTypeTests<string>.DefaultRecord>), typeof(HashResp2DataTypeTests), typeof(HashResp3DataTypeTests)),
            (typeof(EmbeddingTypeTests<string>), typeof(HashResp2EmbeddingTypeTests), typeof(HashResp3EmbeddingTypeTests)),
            (null, typeof(HashResp2KeyTypeTests), typeof(HashResp3KeyTypeTests)),
        })
        {
            var type = protocol == RespProtocol.Resp2 ? resp2 : resp3;
            AssertDiscoverable(type);
            var expected = upstream is null ? ["String"] : Facts(upstream).Select(m => m.Name).Except(["Embedding_of_float"]);
            Assert.Equal(expected.Order(), Facts(type).Where(m => !IsExcluded(m)).Select(m => m.Name).Order());
            Assert.All(Facts(type).Where(m => !IsExcluded(m)), AssertRunnable);
        }
    }

    [Theory]
    [InlineData(RespProtocol.Resp2)]
    [InlineData(RespProtocol.Resp3)]
    public void Every_applicable_json_fact_has_all_theory_rows(RespProtocol protocol)
    {
        // Independent of Cases() exclusions: changing the runner cannot silently reduce coverage.
        AssertDiscoverable(typeof(JsonBackendTests));
        AssertRunnable(typeof(JsonBackendTests).GetMethod(nameof(JsonBackendTests.Official_json_contract))!);
        var cases = JsonBackendTests.Cases().Where(row => (RespProtocol)row[0] == protocol).ToArray();
        foreach (var (name, suite, excluded) in new (string, Type, string[])[]
        {
            ("Basic", typeof(BasicModelTests<string>), ["GetAsync_with_filter", "GetAsync_with_filter_by_true",
                "GetAsync_with_filter_and_OrderBy", "GetAsync_with_filter_and_multiple_OrderBys", "GetAsync_with_filter_and_OrderBy_and_Skip",
                "UpsertAsync_does_nothing_for_empty_batch", "DeleteAsync_does_nothing_for_non_existing_key", "DeleteAsync_does_nothing_for_empty_batch", "SearchAsync_with_Filter"]),
            ("NoData", typeof(NoDataModelTests<string>), []),
            ("MultiVector", typeof(MultiVectorModelTests<string>), []),
            ("Distance", typeof(DistanceFunctionTests<string>), ["CosineSimilarity", "DotProductSimilarity", "EuclideanDistance", "HammingDistance", "ManhattanDistance"]),
            ("Index", typeof(IndexKindTests<string>), []),
            ("Hybrid", typeof(HybridSearchTests<string>), ["HybridSearchAsync_with_filter"]),
        })
        {
            var facts = Facts(suite);
            Assert.All(excluded, method => Assert.Contains(facts, f => f.Name == method));
            var rows = cases.Where(row => (string)row[1] == name).ToArray();
            AssertCovered(facts.Select(m => m.Name).Except(excluded), rows.Select(row => (string)row[2]));
            foreach (var fact in facts.Where(m => !excluded.Contains(m.Name)))
            {
                var values = rows.Where(row => (string)row[2] == fact.Name).Select(row => row[3]).ToArray();
                Assert.Equal(fact.GetParameters().Length == 0 ? [null] : new object?[] { false, true }, values);
            }
        }
        Assert.Single(cases, row => (string)row[1] == "Index" && (string)row[2] == "Hnsw");
        var lifecycle = protocol == RespProtocol.Resp2 ? typeof(JsonResp2CollectionManagementTests) : typeof(JsonResp3CollectionManagementTests);
        AssertDiscoverable(lifecycle);
        AssertCovered(Facts(typeof(CollectionManagementTests<string>)).Select(m => m.Name), Facts(lifecycle).Select(m => m.Name));
        Assert.All(Facts(lifecycle), AssertRunnable);
    }

    [Fact]
    public void Coverage_gate_rejects_a_missing_supported_fact()
        => Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertCovered(["GetAsync", "SearchAsync"], ["GetAsync"]));

    [Fact]
    public void Coverage_gate_rejects_an_unreviewed_base()
        => Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertReviewed([typeof(FilterTests<>)], []));

    [Fact]
    public void No_generator_error_contract_runs_on_both_backends_and_protocols()
    {
        var method = typeof(EmbeddingErrorTests).GetMethod(nameof(EmbeddingErrorTests.SearchAsync_string_without_generator_throws))!;
        AssertDiscoverable(typeof(EmbeddingErrorTests));
        AssertRunnable(method);
        var rows = method.GetCustomAttributes<InlineDataAttribute>().Select(row => row.Data).ToArray();
        Assert.Equal(4, rows.Length);
        foreach (var protocol in new[] { RespProtocol.Resp2, RespProtocol.Resp3 })
        foreach (var json in new[] { false, true })
            Assert.Single(rows, row => (RespProtocol)row[0]! == protocol && (bool)row[1]! == json);
    }

    private static void AssertReviewed(IEnumerable<Type> upstream, IEnumerable<Type> reviewed)
        => Assert.Equal(upstream.OrderBy(t => t.FullName), reviewed.OrderBy(t => t.FullName));

    private static void AssertDiscoverable(Type type)
    {
        Assert.True(type.IsPublic && !type.IsAbstract);
        Assert.DoesNotContain(type.GetCustomAttributes<TraitAttribute>(), t => t.Name == "Unsupported");
    }

    private static void AssertRunnable(MethodInfo method)
    {
        var fact = method.GetCustomAttribute<FactAttribute>();
        Assert.NotNull(fact);
        Assert.Null(fact.Skip);
        Assert.False(IsExcluded(method));
    }

    private static MethodInfo[] Facts(Type type)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<FactAttribute>() is not null).ToArray();

    private static bool IsExcluded(MethodInfo method)
        => method.GetCustomAttributes<TraitAttribute>().Any(t => t.Name == "Unsupported");

    private static void AssertCovered(IEnumerable<string> expected, IEnumerable<string> actual)
        => Assert.Empty(expected.Except(actual));
}
