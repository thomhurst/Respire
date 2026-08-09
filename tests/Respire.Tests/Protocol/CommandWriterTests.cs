using System.Text;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Protocol;

public class CommandWriterTests
{
    [Test]
    [Arguments("key", "value")]
    [Arguments("£", "sterling")]
    [Arguments("𐍈", "\uD800")]
    public async Task SetCommand_MatchesUtf8Encoding(string key, string value)
    {
        var buffer = new WriteBuffer(128);
        var command = new Cmd2(Verbs.Set, key, value);
        var writer = new RespWriter(buffer);

        command.Write(ref writer);

        var expected = Encoding.UTF8.GetBytes(
            $"*3\r\n$3\r\nSET\r\n${Encoding.UTF8.GetByteCount(key)}\r\n{key}\r\n" +
            $"${Encoding.UTF8.GetByteCount(value)}\r\n{value}\r\n");
        var actual = buffer.WrittenMemory.ToArray();
        buffer.Release();
        await Assert.That(actual.SequenceEqual(expected)).IsTrue();
    }
}
