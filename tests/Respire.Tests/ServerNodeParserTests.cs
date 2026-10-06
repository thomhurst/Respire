using Respire.Protocol;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ServerNodeParserTests
{
    [Test]
    public async Task KeyFlagsOwnBinaryKeysAndFutureFlags()
    {
        byte[] bytes = [255, 0, 128];
        var reply = RespValue.Array(RespValue.Array(RespValue.BulkString(bytes), RespValue.Array(Text("RO"), Text("future"))));
        var result = ServerNodeParser.KeysAndFlags(in reply);
        reply.Dispose(); bytes.AsSpan().Clear();
        await Assert.That(result.Single().Key).IsEquivalentTo(new byte[] { 255, 0, 128 });
        await Assert.That(result.Single().Flags).IsEquivalentTo(["RO", "future"]);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task BackupStatusParsesBothProtocolsAndOwnsUnknownFields(bool map, bool integerTimestamps)
    {
        var fields = new[] { Text("state"), Text("future-state"), Text("error"), Text(""), Text("start_time"), Text("1720000000"),
            Text("end_time"), Text("0"), Text("future"), Text("value") };
        if (integerTimestamps) { fields[5] = RespValue.Integer(1720000000); fields[7] = RespValue.Integer(0); }
        var reply = RespValue.Array(fields);
        if (map)
        {
            var wire = Encoding.UTF8.GetBytes("%5\r\n" + string.Concat(fields.Select(field =>
            {
                if (field.Type == RespDataType.Integer) return $":{field.AsInteger()}\r\n";
                var text = field.AsString();
                return $"${Encoding.UTF8.GetByteCount(text)}\r\n{text}\r\n";
            })));
            var position = 0;
            await Assert.That(RespParser.TryParseValue(wire, ref position, out reply)).IsEqualTo(RespParseStatus.Done);
        }
        var result = ServerNodeParser.BackupStatus(in reply); reply.Dispose();
        await Assert.That(result.State).IsEqualTo("future-state");
        await Assert.That(result.StartTime).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1720000000));
        await Assert.That(result.EndTime).IsNull();
        await Assert.That(result.AdditionalFields["future"].AsString()).IsEqualTo("value");
    }

    [Test]
    [Arguments("odd")]
    [Arguments("missing")]
    [Arguments("duplicate")]
    [Arguments("negative")]
    [Arguments("overflow")]
    [Arguments("wrong-type")]
    [Arguments("negative-integer")]
    [Arguments("timestamp-type")]
    public async Task MalformedBackupStatusIsRejected(string shape)
    {
        var reply = shape switch
        {
            "odd" => RespValue.Array(Text("state")),
            "missing" => RespValue.Array(),
            "duplicate" => RespValue.Array(Text("state"), Text("idle"), Text("state"), Text("idle")),
            "wrong-type" => RespValue.Integer(1),
            "negative-integer" => RespValue.Array(Text("state"), Text("idle"), Text("error"), Text(""),
                Text("start_time"), RespValue.Integer(-1), Text("end_time"), RespValue.Integer(0)),
            "timestamp-type" => RespValue.Array(Text("state"), Text("idle"), Text("error"), Text(""),
                Text("start_time"), RespValue.Array(), Text("end_time"), RespValue.Integer(0)),
            _ => RespValue.Array(Text("state"), Text("idle"), Text("error"), Text(""),
                Text("start_time"), Text(shape == "negative" ? "-1" : "9223372036854775807"), Text("end_time"), Text("0")),
        };
        await Assert.That(() => ServerNodeParser.BackupStatus(in reply)).ThrowsExactly<RespireProtocolException>();
    }

    [Test]
    [Arguments("integer")]
    [Arguments("missing-flags")]
    [Arguments("key-type")]
    [Arguments("flags-type")]
    public async Task MalformedKeyFlagsAreRejected(string shape)
    {
        var reply = shape switch
        {
            "integer" => RespValue.Integer(1),
            "missing-flags" => RespValue.Array(RespValue.Array(Text("key"))),
            "key-type" => RespValue.Array(RespValue.Array(RespValue.Integer(1), RespValue.Array())),
            _ => RespValue.Array(RespValue.Array(Text("key"), Text("RO"))),
        };
        await Assert.That(() => ServerNodeParser.KeysAndFlags(in reply)).ThrowsExactly<RespireProtocolException>();
    }

    [Test]
    [Arguments("OK", RespireMigrateResult.Migrated)]
    [Arguments("NOKEY", RespireMigrateResult.NoKey)]
    public async Task MigrationParsesOnlyAcknowledgedResults(string text, RespireMigrateResult expected)
    {
        var reply = RespValue.SimpleString(text);
        await Assert.That(ServerNodeParser.Migration(in reply)).IsEqualTo(expected);
        var wrong = Text(text);
        await Assert.That(() => ServerNodeParser.Migration(in wrong)).ThrowsExactly<RespireProtocolException>();
    }

    private static RespValue Text(string text) => RespValue.BulkString(text);
}
