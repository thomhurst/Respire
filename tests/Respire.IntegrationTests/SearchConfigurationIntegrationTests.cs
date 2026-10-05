using System.Globalization;
using FluentAssertions;
using Respire.Search;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
public class SearchConfigurationIntegrationTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LegacyAndModernConfigurationShareValuesAndRestoreChanges(int protocol)
    {
        // Configuration is node-global. Each test owns its container rather than a shared Search fixture.
        await using var fixture = new SearchConfigurationTestContainer();
        await fixture.InitializeAsync();
        await using var client = await ConnectAsync(fixture, protocol);
        var search = client.Search;
        var owned = await search.GetConfigurationAsync();
        var original = owned["TIMEOUT"]!;
        owned.Should().ContainKey("EXTLOAD");
        owned["EXTLOAD"].Should().BeNull();
        try
        {
            var updated = (long.Parse(original, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
            await search.SetConfigurationAsync("TIMEOUT", updated);
            (await search.GetConfigurationAsync("TIMEOUT"))["TIMEOUT"].Should().Be(updated);
            (await client.Server.ConfigAsync("search-timeout"))["search-timeout"].Should().Be(updated);
            await client.Server.SetConfigAsync("search-timeout", original);
            (await search.GetConfigurationAsync("TIMEOUT"))["TIMEOUT"].Should().Be(original);
        }
        finally
        {
            await search.SetConfigurationAsync("TIMEOUT", original);
        }

        (await search.GetConfigurationAsync("TIMEOUT"))["TIMEOUT"].Should().Be(original);
        owned["TIMEOUT"].Should().Be(original);
        (await search.GetConfigurationAsync("DOES_NOT_EXIST")).Should().BeEmpty();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ServerConfigurationErrorsKeepTheirOriginalErrorCodes(int protocol)
    {
        await using var fixture = new SearchConfigurationTestContainer();
        await fixture.InitializeAsync();
        await using var client = await ConnectAsync(fixture, protocol);
        Func<Task> invalid = async () => await client.Search.SetConfigurationAsync("TIMEOUT", "not-a-number");
        (await invalid.Should().ThrowAsync<RespireServerException>()).Which.Message.Should().StartWith("SEARCH_PARSE_ARGS ");
        Func<Task> immutable = async () => await client.Search.SetConfigurationAsync("EXTLOAD", "unused");
        (await immutable.Should().ThrowAsync<RespireServerException>()).Which.Message.Should().StartWith("SEARCH_OPTION_BAD ");
        Func<Task> unknown = async () => await client.Search.SetConfigurationAsync("DOES_NOT_EXIST", "value");
        await unknown.Should().ThrowAsync<RespireServerException>();
    }

    private static ValueTask<RespireClient> ConnectAsync(SearchConfigurationTestContainer fixture, int protocol)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(fixture.Host, fixture.Port)],
            Protocol = (RespProtocol)protocol,
            Connections = 1,
            AllowAdmin = true,
            ThreadPoolMonitoring = false,
        });
}

public sealed class SearchConfigurationTestContainer() : StandaloneRedisTestContainer("redis:8.10-alpine");
