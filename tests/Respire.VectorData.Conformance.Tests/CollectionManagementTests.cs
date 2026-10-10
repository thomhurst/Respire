using VectorData.ConformanceTests;
using VectorData.ConformanceTests.Support;
using Xunit;

namespace Respire.VectorData.Conformance.Tests;

// All six facts and assertions are inherited unchanged from the official 10.10.0 package.
[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp2CollectionManagementTests(HashResp2Fixture fixture)
    : CollectionManagementTests<string>(fixture), IClassFixture<HashResp2Fixture>;

[Collection(RedisConformanceCollection.Name)]
public sealed class HashResp3CollectionManagementTests(HashResp3Fixture fixture)
    : CollectionManagementTests<string>(fixture), IClassFixture<HashResp3Fixture>;

[Collection(RedisConformanceCollection.Name)]
public sealed class JsonResp2CollectionManagementTests(JsonResp2Fixture fixture)
    : CollectionManagementTests<string>(fixture), IClassFixture<JsonResp2Fixture>;

[Collection(RedisConformanceCollection.Name)]
public sealed class JsonResp3CollectionManagementTests(JsonResp3Fixture fixture)
    : CollectionManagementTests<string>(fixture), IClassFixture<JsonResp3Fixture>;

public sealed class HashResp2Fixture(RedisServerFixture server) : LifecycleFixture(server, RespProtocol.Resp2, json: false);
public sealed class HashResp3Fixture(RedisServerFixture server) : LifecycleFixture(server, RespProtocol.Resp3, json: false);
public sealed class JsonResp2Fixture(RedisServerFixture server) : LifecycleFixture(server, RespProtocol.Resp2, json: true);
public sealed class JsonResp3Fixture(RedisServerFixture server) : LifecycleFixture(server, RespProtocol.Resp3, json: true);

public abstract class LifecycleFixture(RedisServerFixture server, RespProtocol protocol, bool json) : VectorStoreFixture
{
    public override TestStore TestStore { get; } = new LifecycleTestStore(server, protocol, json);
}
