using System.Reflection;
using System.Runtime.ExceptionServices;
using VectorData.ConformanceTests;
using VectorData.ConformanceTests.ModelTests;
using VectorData.ConformanceTests.Support;
using Xunit;

namespace Respire.VectorData.Conformance.Tests;

/// <summary>Executes applicable official methods without changing their assertions.</summary>
[Collection(RedisConformanceCollection.Name)]
public sealed class JsonBackendTests(RedisServerFixture server)
{
    // Explicit capability boundaries. Never catch an applicable failure or mark it skipped.
    // Filtered ordering remains unsupported for both backends. Evidence is documented in the feature guide.
    private static readonly HashSet<string> BasicExclusions =
    [
        "GetAsync_with_filter_and_OrderBy",
        "GetAsync_with_filter_and_multiple_OrderBys", "GetAsync_with_filter_and_OrderBy_and_Skip",
    ];

    public static IEnumerable<object[]> Cases()
    {
        (string Name, Type Type, HashSet<string> Exclusions)[] suites =
        [
            ("Basic", typeof(BasicModelTests<string>), BasicExclusions),
            ("NoData", typeof(NoDataModelTests<string>), []),
            ("MultiVector", typeof(MultiVectorModelTests<string>), []),
            ("Filter", typeof(FilterTests<string>),
                ["Equal_with_string_sql_injection_in_name", "Equal_with_string_containing_special_characters"]),
            ("Distance", typeof(DistanceFunctionTests<string>),
                ["CosineSimilarity", "DotProductSimilarity", "EuclideanDistance", "HammingDistance", "ManhattanDistance"]),
            ("Index", typeof(IndexKindTests<string>), []),
            ("Hybrid", typeof(HybridSearchTests<string>), []),
        ];
        foreach (var (name, type, exclusions) in suites)
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttribute<FactAttribute>() is null || exclusions.Contains(method.Name)) continue;
            foreach (var protocol in new[] { RespireProtocol.Resp2, RespireProtocol.Resp3 })
            {
                if (method.GetParameters().Length == 0) yield return [protocol, name, method.Name, null!];
                else
                {
                    // The only applicable upstream theories take IncludeVectors, with false and true rows.
                    if (method.GetParameters() is not [{ ParameterType: var parameterType }] || parameterType != typeof(bool))
                        throw new InvalidOperationException("New upstream theory requires an explicit adapter.");
                    yield return [protocol, name, method.Name, false];
                    yield return [protocol, name, method.Name, true];
                }
            }
        }
        foreach (var protocol in new[] { RespireProtocol.Resp2, RespireProtocol.Resp3 })
            yield return [protocol, "Index", "Hnsw", null!];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Official_json_contract(RespireProtocol protocol, string suite, string methodName, bool? includeVectors)
    {
        var store = new JsonTestStore(server, protocol);
        VectorStoreFixture[] fixtures;
        object tests;
        switch (suite)
        {
            case "Basic":
                var basic = new BasicFixture(store);
                fixtures = [basic]; tests = new BasicTests(basic); break;
            case "NoData":
                var noData = new NoDataFixture(store);
                fixtures = [noData]; tests = new NoDataTests(noData); break;
            case "MultiVector":
                var multi = new MultiVectorFixture(store);
                fixtures = [multi]; tests = new MultiVectorTests(multi); break;
            case "Filter":
                var filter = new FilterFixture(store);
                fixtures = [filter]; tests = new FilterTestsAdapter(filter); break;
            case "Distance":
                var distance = new DistanceFixture(store);
                fixtures = [distance]; tests = new DistanceTests(distance); break;
            case "Index":
                var index = new IndexFixture(store);
                fixtures = [index]; tests = new IndexTests(index); break;
            case "Hybrid":
                var text = new TextFixture(store);
                var multiText = new MultiTextFixture(store);
                fixtures = [text, multiText]; tests = new HybridTests(text, multiText); break;
            default: throw new InvalidOperationException("Unknown official suite.");
        }

        var initialized = new List<VectorStoreFixture>();
        try
        {
            // A fresh fixture per row isolates writes and deletion contracts from other upstream methods.
            // Track before initialization so a seed/index failure still closes the owned client.
            foreach (var fixture in fixtures)
            {
                initialized.Add(fixture);
                await fixture.InitializeAsync();
            }
            var method = tests.GetType().GetMethod(methodName)!;
            try
            {
                await (Task)method.Invoke(tests, includeVectors.HasValue ? [includeVectors.Value] : null)!;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
        finally
        {
            foreach (var fixture in initialized.AsEnumerable().Reverse()) await fixture.DisposeAsync();
        }
    }

    private sealed class BasicFixture(TestStore store) : BasicModelTests<string>.Fixture { public override TestStore TestStore => store; }
    private sealed class NoDataFixture(TestStore store) : NoDataModelTests<string>.Fixture { public override TestStore TestStore => store; }
    private sealed class MultiVectorFixture(TestStore store) : MultiVectorModelTests<string>.Fixture { public override TestStore TestStore => store; }
    private sealed class FilterFixture(TestStore store) : FilterTests<string>.Fixture
    {
        public override TestStore TestStore => store;
        public override bool TestDynamic => false;
    }
    private sealed class DistanceFixture(TestStore store) : DistanceFunctionTests<string>.Fixture { public override TestStore TestStore => store; }
    private sealed class IndexFixture(TestStore store) : IndexKindTests<string>.Fixture { public override TestStore TestStore => store; }
    private sealed class TextFixture(TestStore store) : HybridSearchTests<string>.VectorAndStringFixture { public override TestStore TestStore => store; }
    private sealed class MultiTextFixture(TestStore store) : HybridSearchTests<string>.MultiTextFixture { public override TestStore TestStore => store; }

    // Private adapters keep inherited, unsupported facts out of discovery. Public wrapper rows name every method executed.
    private sealed class BasicTests(BasicModelTests<string>.Fixture fixture) : BasicModelTests<string>(fixture);
    private sealed class NoDataTests(NoDataModelTests<string>.Fixture fixture) : NoDataModelTests<string>(fixture);
    private sealed class MultiVectorTests(MultiVectorModelTests<string>.Fixture fixture) : MultiVectorModelTests<string>(fixture);
    private sealed class FilterTestsAdapter(FilterTests<string>.Fixture fixture) : FilterTests<string>(fixture);
    private sealed class DistanceTests(DistanceFunctionTests<string>.Fixture fixture) : DistanceFunctionTests<string>(fixture);
    private sealed class IndexTests(IndexKindTests<string>.Fixture fixture) : IndexKindTests<string>(fixture)
    {
        // Upstream exposes only a Flat fact. Reuse its complete protected contract for Redis HNSW.
        public Task Hnsw() => Test(Microsoft.Extensions.VectorData.IndexKind.Hnsw);
    }
    private sealed class HybridTests(HybridSearchTests<string>.VectorAndStringFixture text, HybridSearchTests<string>.MultiTextFixture multi)
        : HybridSearchTests<string>(text, multi);
}
