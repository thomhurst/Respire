using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;

namespace Respire.StackExchangeCompat;

internal static class UpstreamCache
{
    internal static RedisCache Create(RedisTestContainer fixture)
    {
        var connection = RespireConnectionMultiplexer.Create(RespireOptions.Parse(fixture.ConnectionString));
        return new RedisCache(Options.Create(new RedisCacheOptions
        {
            ConnectionMultiplexerFactory = () => Task.FromResult<StackExchange.Redis.IConnectionMultiplexer>(connection),
            InstanceName = "upstream:",
        }));
    }

    internal static void ThrowsArgumentOutOfRange(Action action, string paramName, string message, object actualValue)
    {
        var error = Xunit.Assert.Throws<ArgumentOutOfRangeException>(action);
        Xunit.Assert.Equal(paramName, error.ParamName);
        Xunit.Assert.StartsWith(message, error.Message);
        Xunit.Assert.Equal(actualValue, error.ActualValue);
    }
}
