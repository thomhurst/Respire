using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class Valkey92AdministrationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConfigurationMetadataPreservesMapsAndExactBounds(bool resp3)
    {
        var fields = new object[] { "name", "maxmemory", "type", "numeric", "flags", new object[] { "modifiable" },
            "range", new object[] { "0", "18446744073709551615" }, "future", "retained" };
        await using var server = new FakeRespServer(Array(Map(resp3, fields)), FakeRespServer.PongReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var entries = await client.WithKeyPrefix("tenant:").Server.ConfigInfoAsync(["maxmemory"]);
        await client.PingAsync();
        await client.DisposeAsync();
        var entry = entries.Single();
        await Assert.That(entry.Name).IsEqualTo("maxmemory");
        await Assert.That(entry.Type).IsEqualTo("numeric");
        await Assert.That(entry.Flags).IsEquivalentTo(["modifiable"]);
        await Assert.That(entry.Range).IsEquivalentTo(["0", "18446744073709551615"]);
        await Assert.That(entry.Values).IsEmpty();
        await Assert.That(entry.AdditionalFields["future"].AsString()).IsEqualTo("retained");
        await Assert.That(server.ReceivedCommands.First()).IsEqualTo("CONFIG INFO maxmemory");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RoleMetadataOwnsBinaryUsersAndDatabaseRules(bool resp3)
    {
        var fields = new object[] { "commands", "-@all +get", "keys", "~*", "channels", "&*", "databases", "alldbs",
            "selectors", new object[] { }, "users", new object[] { new byte[] { 0, 255 } } };
        await using var server = new FakeRespServer(Map(resp3, fields), FakeRespServer.PongReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var role = (await client.WithKeyPrefix("tenant:").Server.AclGetRoleAsync("reader"))!;
        await client.PingAsync();
        await client.DisposeAsync();
        await Assert.That(role.Root.Commands).IsEqualTo("-@all +get");
        await Assert.That(role.Root.AdditionalFields["databases"].AsString()).IsEqualTo("alldbs");
        await Assert.That(role.Users.Single()).IsEquivalentTo(new byte[] { 0, 255 });
        await Assert.That(role.Selectors).IsEmpty();
        await Assert.That(server.ReceivedCommands.First()).IsEqualTo("ACL GETROLE reader");
    }

    [Test]
    public async Task MutationsRequireAdminBeforeSending()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.Server.AclSetRoleAsync("r", ["+get"]))
            .Throws<NotSupportedException>();
        await Assert.That(async () => await client.Server.AclDeleteRolesAsync(["r"]))
            .Throws<NotSupportedException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task RoleCommandsRetainTokenBoundariesAndServerErrors()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, ":1\r\n"u8.ToArray(),
            "-ERR unknown subcommand 'DIGEST'\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, AllowAdmin = true, Protocol = RespireProtocol.Resp2,
        });
        await client.WithKeyPrefix("tenant:").Server.AclSetRoleAsync("r", ["(+get ~cache:*)"]);
        await Assert.That(await client.Server.AclDeleteRolesAsync(["r"])).IsEqualTo(1);
        await Assert.That(async () => await client.Server.AclDigestAsync()).Throws<RespireServerException>();
        await Assert.That(server.ReceivedArguments[0].Select(value => Encoding.UTF8.GetString(value)))
            .IsEquivalentTo(["ACL", "SETROLE", "r", "(+get ~cache:*)"]);
    }

    private static byte[] Map(bool resp3, object[] fields)
        => Encoding.ASCII.GetBytes(resp3 ? $"%{fields.Length / 2}\r\n" : $"*{fields.Length}\r\n")
            .Concat(fields.SelectMany(Encode)).ToArray();

    private static byte[] Array(byte[] value) => "*1\r\n"u8.ToArray().Concat(value).ToArray();

    private static byte[] Encode(object value)
    {
        if (value is object[] items)
            return Encoding.ASCII.GetBytes($"*{items.Length}\r\n").Concat(items.SelectMany(Encode)).ToArray();
        var bytes = value is byte[] binary ? binary : Encoding.UTF8.GetBytes((string)value);
        return Encoding.ASCII.GetBytes($"${bytes.Length}\r\n").Concat(bytes).Concat("\r\n"u8.ToArray()).ToArray();
    }
}
