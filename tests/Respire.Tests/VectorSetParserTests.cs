using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class VectorSetParserTests
{
    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, true)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    public async Task SearchReplyShapesHaveOwnedMembersAndOptionalDetails(bool resp3, bool scores, bool attributes)
    {
        var map = resp3 && (scores || attributes);
        var stride = map ? 2 : 1 + (scores ? 1 : 0) + (attributes ? 1 : 0);
        var frame = map ? "%1\r\n" : $"*{stride}\r\n";
        frame += "$1\r\nm\r\n";
        if (map && scores && attributes) frame += "*2\r\n";
        if (scores) frame += resp3 ? ",0.75\r\n" : "$4\r\n0.75\r\n";
        if (attributes) frame += "$2\r\n{}\r\n";
        var reply = Parse(frame);
        var results = VectorSetParser.Matches(in reply, new() { IncludeScores = scores, IncludeAttributes = attributes });
        reply.Dispose();
        await Assert.That(results.Length).IsEqualTo(1);
        await Assert.That(results[0].Member).IsEquivalentTo("m"u8.ToArray());
        await Assert.That(results[0].Score).IsEqualTo(scores ? (double?)0.75 : null);
        if (attributes) await Assert.That(results[0].AttributesJson!).IsEquivalentTo("{}"u8.ToArray());
        else await Assert.That(results[0].AttributesJson).IsNull();
    }

    [Test]
    public async Task MissingValuesAndFutureInfoFieldsRemainDistinct()
    {
        var missing = RespValue.Null;
        await Assert.That(VectorSetParser.Embedding(in missing)).IsNull();
        await Assert.That(VectorSetParser.Info(in missing)).IsNull();
        await Assert.That(VectorSetParser.Links(in missing, true)).IsNull();
        await Assert.That(VectorSetParser.NullableBytes(in missing)).IsNull();
        var info = Parse("%8\r\n+quant-type\r\n+future-quant\r\n+vector-dim\r\n:3\r\n+size\r\n:0\r\n+hnsw-m\r\n:16\r\n+max-level\r\n:0\r\n+attributes-count\r\n:0\r\n+projection-input-dim\r\n:9\r\n+future\r\n*1\r\n$2\r\nhi\r\n");
        var result = VectorSetParser.Info(in info)!;
        info.Dispose();
        await Assert.That(result.Quantization).IsEqualTo("future-quant");
        await Assert.That(result.ProjectionInputDimensions).IsEqualTo((long?)9);
        await Assert.That(result.AdditionalFields["future"][0].AsString()).IsEqualTo("hi");
        var nullAttribute = Parse("%1\r\n+m\r\n*2\r\n,1\r\n_\r\n");
        var match = VectorSetParser.Matches(in nullAttribute, new() { IncludeScores = true, IncludeAttributes = true })[0];
        await Assert.That(match.AttributesJson).IsNull();
    }

    [Test]
    public async Task MalformedShapesAndNonfiniteMeasurementsAreRejected()
    {
        await Assert.That(() => VectorSetParser.Flag(RespValue.Integer(2))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => VectorSetParser.Number(RespValue.Integer(-1))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => VectorSetParser.Members(RespValue.Array(RespValue.Integer(1)))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => VectorSetParser.Embedding(RespValue.Array(RespValue.Double(double.NaN)))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => VectorSetParser.Embedding(RespValue.Array(RespValue.Double(double.MaxValue)))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => VectorSetParser.Matches(Parse("*1\r\n+m\r\n"), new() { IncludeScores = true })).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => VectorSetParser.Matches(Parse("%1\r\n+m\r\n*1\r\n,1\r\n"), new() { IncludeScores = true, IncludeAttributes = true })).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => VectorSetParser.Info(Parse("*2\r\n+size\r\n:1\r\n"))).ThrowsExactly<RespireProtocolException>();
        await Assert.That(() => VectorSetParser.Info(Parse("*4\r\n+size\r\n:1\r\n+size\r\n:2\r\n"))).ThrowsExactly<RespireProtocolException>();
    }

    private static RespValue Parse(string wire)
    {
        var position = 0;
        if (RespParser.TryParseValue(Encoding.UTF8.GetBytes(wire), ref position, out var value) != RespParseStatus.Done)
            throw new InvalidOperationException("Invalid test frame.");
        return value;
    }
}
