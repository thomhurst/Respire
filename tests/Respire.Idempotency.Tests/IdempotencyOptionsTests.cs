using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Idempotency.Tests;

public class IdempotencyOptionsTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    public async Task MalformedKeyPrefixFailsHostStartup(int malformedCase)
    {
        var suffix = malformedCase switch
        {
            0 => "\uD800",
            1 => "\uD801",
            2 => "\uDC00",
            3 => "\uD800x",
            _ => "\uDC00\uD800"
        };
        using var host = CreateHost("application:" + suffix);

        await Assert.That(() => host.StartAsync()).Throws<OptionsValidationException>();
    }

    [Test]
    [Arguments("application:")]
    [Arguments("application:\uFFFD")]
    [Arguments("application:\U0001F600")]
    public async Task WellFormedKeyPrefixAllowsHostStartup(string prefix)
    {
        using var host = CreateHost(prefix);

        await host.StartAsync();
        await Assert.That(host.Services.GetRequiredService<IOptions<RespireIdempotencyOptions>>().Value.KeyPrefix)
            .IsEqualTo(prefix);
        await host.StopAsync();
    }

    private static IHost CreateHost(string prefix) => new HostBuilder()
        .ConfigureServices(services => services.AddRespireIdempotency(options =>
        {
            options.CallerScope = _ => "caller";
            options.KeyPrefix = prefix;
        })).Build();
}
