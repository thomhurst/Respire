using Microsoft.Extensions.DependencyInjection;
using Respire.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Caching.Tests;

public class ThreadPoolRegistrationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BuilderDefaults_MatchClientOptions(bool keyed)
    {
        await using var provider = Register(keyed, _ => { });
        var options = Resolve(provider, keyed).Core.Options;

        await Assert.That(options.ThreadPoolMonitoring).IsTrue();
        await Assert.That(options.ThreadPoolWarningThreshold).IsEqualTo(TimeSpan.FromMilliseconds(500));
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task BuilderSettings_ReachResolvedClient(bool keyed, bool enabled)
    {
        var threshold = TimeSpan.FromMilliseconds(1250);
        await using var provider = Register(keyed, options =>
        {
            options.ThreadPoolMonitoring = enabled;
            options.ThreadPoolWarningThreshold = threshold;
        });
        var options = Resolve(provider, keyed).Core.Options;

        await Assert.That(options.ThreadPoolMonitoring).IsEqualTo(enabled);
        await Assert.That(options.ThreadPoolWarningThreshold).IsEqualTo(threshold);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InvalidBuilderThreshold_IsRejectedOnResolution(bool keyed)
    {
        await using var provider = Register(keyed, options =>
            options.ThreadPoolWarningThreshold = TimeSpan.Zero);

        await Assert.That(() => Resolve(provider, keyed)).Throws<RespireConfigurationException>();
    }

    private static ServiceProvider Register(bool keyed, Action<RespireOptionsBuilder> configure)
    {
        var services = new ServiceCollection();
        void Configure(RespireOptionsBuilder options)
        {
            // Client construction is lazy; these tests never connect to Redis.
            options.Endpoints.Add(new RespireEndpoint("127.0.0.1", 1));
            configure(options);
        }
        if (keyed) services.AddKeyedRespire("probe", Configure);
        else services.AddRespire(Configure);
        return services.BuildServiceProvider();
    }

    private static RespireClient Resolve(ServiceProvider provider, bool keyed)
        => keyed ? provider.GetRequiredKeyedService<RespireClient>("probe")
            : provider.GetRequiredService<RespireClient>();
}
