using VectorData.ConformanceTests;
using VectorData.ConformanceTests.ModelTests;
using VectorData.ConformanceTests.Support;

namespace Respire.VectorData.Conformance.Tests;

public abstract class BasicFixture(RedisServerFixture server, RespProtocol protocol) : BasicModelTests<string>.Fixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class BasicResp2Fixture(RedisServerFixture server) : BasicFixture(server, RespProtocol.Resp2);
public sealed class BasicResp3Fixture(RedisServerFixture server) : BasicFixture(server, RespProtocol.Resp3);

public abstract class NoDataFixture(RedisServerFixture server, RespProtocol protocol) : NoDataModelTests<string>.Fixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class NoDataResp2Fixture(RedisServerFixture server) : NoDataFixture(server, RespProtocol.Resp2);
public sealed class NoDataResp3Fixture(RedisServerFixture server) : NoDataFixture(server, RespProtocol.Resp3);

public abstract class MultiVectorFixture(RedisServerFixture server, RespProtocol protocol) : MultiVectorModelTests<string>.Fixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class MultiVectorResp2Fixture(RedisServerFixture server) : MultiVectorFixture(server, RespProtocol.Resp2);
public sealed class MultiVectorResp3Fixture(RedisServerFixture server) : MultiVectorFixture(server, RespProtocol.Resp3);

public abstract class FilterFixture(RedisServerFixture server, RespProtocol protocol) : FilterTests<string>.Fixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
    // Supported upstream switch: preserve every typed assertion, omit unsupported dynamic mapping.
    public override bool TestDynamic => false;
}
public sealed class FilterResp2Fixture(RedisServerFixture server) : FilterFixture(server, RespProtocol.Resp2);
public sealed class FilterResp3Fixture(RedisServerFixture server) : FilterFixture(server, RespProtocol.Resp3);

public abstract class DistanceFixture(RedisServerFixture server, RespProtocol protocol) : DistanceFunctionTests<string>.Fixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class DistanceResp2Fixture(RedisServerFixture server) : DistanceFixture(server, RespProtocol.Resp2);
public sealed class DistanceResp3Fixture(RedisServerFixture server) : DistanceFixture(server, RespProtocol.Resp3);

public abstract class IndexFixture(RedisServerFixture server, RespProtocol protocol) : IndexKindTests<string>.Fixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class IndexResp2Fixture(RedisServerFixture server) : IndexFixture(server, RespProtocol.Resp2);
public sealed class IndexResp3Fixture(RedisServerFixture server) : IndexFixture(server, RespProtocol.Resp3);

public abstract class HybridFixture(RedisServerFixture server, RespProtocol protocol) : HybridSearchTests<string>.VectorAndStringFixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class HybridResp2Fixture(RedisServerFixture server) : HybridFixture(server, RespProtocol.Resp2);
public sealed class HybridResp3Fixture(RedisServerFixture server) : HybridFixture(server, RespProtocol.Resp3);

public abstract class MultiTextFixture(RedisServerFixture server, RespProtocol protocol) : HybridSearchTests<string>.MultiTextFixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class MultiTextResp2Fixture(RedisServerFixture server) : MultiTextFixture(server, RespProtocol.Resp2);
public sealed class MultiTextResp3Fixture(RedisServerFixture server) : MultiTextFixture(server, RespProtocol.Resp3);
