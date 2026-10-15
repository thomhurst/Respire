using VectorData.ConformanceTests;
using VectorData.ConformanceTests.ModelTests;
using VectorData.ConformanceTests.Support;

namespace Respire.VectorData.Conformance.Tests;

public abstract class BasicFixture(RedisServerFixture server, RespireProtocol protocol) : BasicModelTests<string>.Fixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class BasicResp2Fixture(RedisServerFixture server) : BasicFixture(server, RespireProtocol.Resp2);
public sealed class BasicResp3Fixture(RedisServerFixture server) : BasicFixture(server, RespireProtocol.Resp3);

public abstract class NoDataFixture(RedisServerFixture server, RespireProtocol protocol) : NoDataModelTests<string>.Fixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class NoDataResp2Fixture(RedisServerFixture server) : NoDataFixture(server, RespireProtocol.Resp2);
public sealed class NoDataResp3Fixture(RedisServerFixture server) : NoDataFixture(server, RespireProtocol.Resp3);

public abstract class MultiVectorFixture(RedisServerFixture server, RespireProtocol protocol) : MultiVectorModelTests<string>.Fixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class MultiVectorResp2Fixture(RedisServerFixture server) : MultiVectorFixture(server, RespireProtocol.Resp2);
public sealed class MultiVectorResp3Fixture(RedisServerFixture server) : MultiVectorFixture(server, RespireProtocol.Resp3);

public abstract class FilterFixture(RedisServerFixture server, RespireProtocol protocol) : FilterTests<string>.Fixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
    // Supported upstream switch: preserve every typed assertion, omit unsupported dynamic mapping.
    public override bool TestDynamic => false;
}
public sealed class FilterResp2Fixture(RedisServerFixture server) : FilterFixture(server, RespireProtocol.Resp2);
public sealed class FilterResp3Fixture(RedisServerFixture server) : FilterFixture(server, RespireProtocol.Resp3);

public abstract class DistanceFixture(RedisServerFixture server, RespireProtocol protocol) : DistanceFunctionTests<string>.Fixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class DistanceResp2Fixture(RedisServerFixture server) : DistanceFixture(server, RespireProtocol.Resp2);
public sealed class DistanceResp3Fixture(RedisServerFixture server) : DistanceFixture(server, RespireProtocol.Resp3);

public abstract class IndexFixture(RedisServerFixture server, RespireProtocol protocol) : IndexKindTests<string>.Fixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class IndexResp2Fixture(RedisServerFixture server) : IndexFixture(server, RespireProtocol.Resp2);
public sealed class IndexResp3Fixture(RedisServerFixture server) : IndexFixture(server, RespireProtocol.Resp3);

public abstract class HybridFixture(RedisServerFixture server, RespireProtocol protocol) : HybridSearchTests<string>.VectorAndStringFixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class HybridResp2Fixture(RedisServerFixture server) : HybridFixture(server, RespireProtocol.Resp2);
public sealed class HybridResp3Fixture(RedisServerFixture server) : HybridFixture(server, RespireProtocol.Resp3);

public abstract class MultiTextFixture(RedisServerFixture server, RespireProtocol protocol) : HybridSearchTests<string>.MultiTextFixture
{
    public override TestStore TestStore { get; } = new HashTestStore(server, protocol);
}
public sealed class MultiTextResp2Fixture(RedisServerFixture server) : MultiTextFixture(server, RespireProtocol.Resp2);
public sealed class MultiTextResp3Fixture(RedisServerFixture server) : MultiTextFixture(server, RespireProtocol.Resp3);
