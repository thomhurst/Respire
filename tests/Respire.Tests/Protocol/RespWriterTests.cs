using System.Text;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Protocol;

public class RespWriterTests
{
    [Test]
    [Arguments("Hello, World!")]
    [Arguments("£ sterling")]
    [Arguments("𐍈")]
    [Arguments("\uD800")]
    public async Task StringBulkValue_MatchesUtf8Encoding(string value)
    {
        var buffer = new WriteBuffer(64);
        var writer = new RespWriter(buffer);

        writer.WriteBulkString(value);

        var payload = Encoding.UTF8.GetBytes(value);
        var expected = Encoding.UTF8.GetBytes($"${payload.Length}\r\n{Encoding.UTF8.GetString(payload)}\r\n");
        var actual = buffer.WrittenMemory.ToArray();
        buffer.Release();
        await Assert.That(actual.SequenceEqual(expected)).IsTrue();
    }
}
