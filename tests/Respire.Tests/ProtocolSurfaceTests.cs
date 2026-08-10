using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ProtocolSurfaceTests
{
    [Test]
    public async Task ProtocolImplementationTypes_AreNotExported()
    {
        var protocolTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Respire.Commands.AuthCommand",
            "Respire.Commands.ClientSetNameCommand",
            "Respire.Commands.HelloCommand",
            "Respire.Commands.RawCommand",
            "Respire.Commands.SelectCommand",
            "Respire.Infrastructure.RespireConnectionMultiplexer",
            "Respire.Networking.RespireConnection",
            "Respire.Networking.RespireConnectionOptions",
            "Respire.Networking.RespirePushHandler",
            "Respire.Protocol.IRespCommand",
            "Respire.Protocol.RespCommands",
            "Respire.Protocol.RespParser",
            "Respire.Protocol.RespParseStatus",
            "Respire.Protocol.RespValue",
            "Respire.Protocol.RespWriter",
        };
        var exportedProtocolTypes = typeof(RespireClient).Assembly
            .GetExportedTypes()
            .Select(static type => type.FullName!)
            .Where(protocolTypes.Contains)
            .ToArray();

        await Assert.That(exportedProtocolTypes).IsEmpty();
    }

    [Test]
    public async Task RespDataType_RemainsExported()
    {
        var exported = typeof(RespireClient).Assembly
            .GetExportedTypes()
            .Any(static type => type.FullName == "Respire.Protocol.RespDataType");

        await Assert.That(exported).IsTrue();
    }
}
