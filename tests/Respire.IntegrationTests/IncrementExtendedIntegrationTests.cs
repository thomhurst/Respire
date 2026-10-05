using DotNet.Testcontainers.Builders;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class IncrementExtendedIntegrationTests
{
    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task FloatingZeroIsCanonicalAcrossExecutionModes(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var container = useFake ? null : new ContainerBuilder("redis:8.10-alpine").WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        if (container is not null) await container.StartAsync();
        var options = fake?.CreateOptions() ?? RespireOptions.Parse($"redis://{container!.Hostname}:{container.GetMappedPublicPort(6379)}");
        await using var client = await RespireClient.ConnectAsync(options with { Protocol = (RespProtocol)protocol });
        foreach (var mode in new[] { "immediate", "batch", "transaction" })
        {
            await client.SetAsync("zero", "-1");
            var saturated = await Floating(client, mode, "zero", 0, new() { LowerBound = -0.0, Saturate = true });
            BitConverter.DoubleToInt64Bits(saturated.Value).Should().Be(0);
            (await client.GetStringAsync("zero")).Should().Be("0");
            await client.SetAsync("zero", "-0");
            var zero = await Floating(client, mode, "zero", -0.0);
            BitConverter.DoubleToInt64Bits(zero.Value).Should().Be(0);
            BitConverter.DoubleToInt64Bits(zero.AppliedIncrement).Should().Be(0);
            (await client.GetStringAsync("zero")).Should().Be("0");
            await client.SetAsync("zero", "-0");
            var rejected = await Floating(client, mode, "zero", 1, new() { UpperBound = -1 });
            BitConverter.DoubleToInt64Bits(rejected.Value).Should().Be(0);
            BitConverter.DoubleToInt64Bits(rejected.AppliedIncrement).Should().Be(0);
            (await client.GetStringAsync("zero")).Should().Be("-0", "a rejected increment must not rewrite the stored value");
        }
    }

    [Test]
    [Arguments("redis:8.10-alpine", 2)]
    [Arguments("redis:8.10-alpine", 3)]
    [Arguments("redis:7.4-alpine", 2)]
    [Arguments("redis:7.4-alpine", 3)]
    public async Task CoordinationUsesAtomicNativeOrLegacyPathWithoutPartialPermits(string image, int protocol)
    {
        await using var container = new ContainerBuilder(image).WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await container.StartAsync();
        await using var client = await RespireClient.ConnectAsync(
            $"redis://{container.Hostname}:{container.GetMappedPublicPort(6379)}?protocol={protocol}");
        var coordination = new Respire.Coordination.RespireCoordination(client.WithKeyPrefix("tenant:"));
        RespireKey key = new byte[] { 0xff, 0, 0x42 };
        await using var limiter = coordination.RateLimiters.FixedWindow(key, 3, TimeSpan.FromMinutes(1));
        using var initial = await limiter.AcquireAsync(1);
        initial.IsAcquired.Should().BeTrue();
        using var denied = await limiter.AcquireAsync(3);
        denied.IsAcquired.Should().BeFalse();
        using var remainder = await limiter.AcquireAsync(2);
        remainder.IsAcquired.Should().BeTrue();
        await using var concurrent = coordination.RateLimiters.FixedWindow("concurrent", 7, TimeSpan.FromMinutes(1));
        var accepted = await Task.WhenAll(Enumerable.Range(0, 30).Select(async _ =>
        {
            using var lease = await concurrent.AcquireAsync(1);
            return lease.IsAcquired;
        }));
        accepted.Count(value => value).Should().Be(7);
        if (image.Contains(":7.4", StringComparison.Ordinal))
        {
            Func<Task> typed = async () => { await client.Strings.IncrementExtendedAsync("unsupported"); };
            await typed.Should().ThrowAsync<RespireServerException>().WithMessage("*unknown command*");
            (await client.ExistsAsync("unsupported")).Should().BeFalse();
        }
    }

    [Test]
    [Arguments(false, 2)]
    [Arguments(false, 3)]
    [Arguments(true, 2)]
    [Arguments(true, 3)]
    public async Task IntegerAndFloatingResultsBoundsAndExpiryMatchAcrossExecutionModes(bool useFake, int protocol)
    {
        await using var fake = useFake ? new RespireFakeServer() : null;
        await using var container = useFake ? null : new ContainerBuilder("redis:8.10-alpine").WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        if (container is not null) await container.StartAsync();
        var options = fake?.CreateOptions() ?? RespireOptions.Parse($"redis://{container!.Hostname}:{container.GetMappedPublicPort(6379)}");
        await using var client = await RespireClient.ConnectAsync(options with { Protocol = (RespProtocol)protocol });
        foreach (var mode in new[] { "immediate", "batch", "transaction" })
        {
            var view = client.WithKeyPrefix(mode + ":");
            RespireKey key = new byte[] { 0xff, 0, 0x42 };
            (await Integer(view, mode, key, 5, new() { UpperBound = 4 })).Should().Be(new RespireIncrementResult<long>(0, 0));
            (await view.ExistsAsync(key)).Should().BeFalse();
            (await Integer(view, mode, key, 5, new() { Expiry = TimeSpan.FromMinutes(5) })).Should().Be(new RespireIncrementResult<long>(5, 5));
            (await Integer(view, mode, key, 5, new() { UpperBound = 6, Expiry = RespireExpiry.Persist })).Should().Be(new RespireIncrementResult<long>(5, 0));
            (await view.Keys.ExpiryAsync(key)).TimeToLive.Should().BeGreaterThan(TimeSpan.FromMinutes(4));
            (await Integer(view, mode, key, 5, new() { UpperBound = 6, Saturate = true, Expiry = RespireExpiry.Persist })).Should().Be(new RespireIncrementResult<long>(6, 1));
            (await view.Keys.ExpiryAsync(key)).HasExpiry.Should().BeFalse();
            (await Integer(view, mode, key, -10, new() { LowerBound = 1, Saturate = true, Expiry = TimeSpan.FromMinutes(5), ExpireOnlyWhenPersistent = true }))
                .Should().Be(new RespireIncrementResult<long>(1, -5));
            await Integer(view, mode, key, 0, new() { Expiry = TimeSpan.FromMilliseconds(1), ExpireOnlyWhenPersistent = true });
            (await view.Keys.ExpiryAsync(key)).TimeToLive.Should().BeGreaterThan(TimeSpan.FromMinutes(4));
            await Integer(view, mode, key, 1, new() { Expiry = RespireExpiry.Keep });
            (await view.Keys.ExpiryAsync(key)).HasExpiry.Should().BeTrue();
            await Integer(view, mode, key, 1, new() { Expiry = RespireExpiry.At(DateTimeOffset.UtcNow.AddMinutes(10)) });
            (await view.Keys.ExpiryAsync(key)).TimeToLive.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
            (await Integer(view, mode, key, 0, new() { Expiry = RespireExpiry.At(DateTimeOffset.FromUnixTimeSeconds(1)) }))
                .Should().Be(new RespireIncrementResult<long>(3, 0));
            (await view.ExistsAsync(key)).Should().BeFalse();
            await view.SetAsync(key, long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            (await Integer(view, mode, key, 1)).Should().Be(new RespireIncrementResult<long>(long.MaxValue, 0));
            (await Integer(view, mode, key, -1)).Should().Be(new RespireIncrementResult<long>(long.MaxValue - 1, -1));
            (await Integer(view, mode, key, 2, new() { Saturate = true })).Should().Be(new RespireIncrementResult<long>(long.MaxValue, 1));
            await view.SetAsync(key, long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Func<Task> overflow = async () => { await Integer(view, mode, key, 0, new() { LowerBound = long.MaxValue, Saturate = true }); };
            await overflow.Should().ThrowAsync<RespireServerException>();
            (await view.GetStringAsync(key)).Should().Be(long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await view.Keys.DeleteAsync(key);
            (await Floating(view, mode, key, 2.5, new() { LowerBound = double.NegativeInfinity, UpperBound = double.PositiveInfinity }))
                .Should().Be(new RespireIncrementResult<double>(2.5, 2.5));
            (await Floating(view, mode, key, 5.0, new() { UpperBound = 3.5 })).Should().Be(new RespireIncrementResult<double>(2.5, 0));
            (await Floating(view, mode, key, 5.0, new() { UpperBound = 3.5, Saturate = true })).Should().Be(new RespireIncrementResult<double>(3.5, 1));
            (await Floating(view, mode, key, -10, new() { LowerBound = -0.5, Saturate = true })).Should().Be(new RespireIncrementResult<double>(-0.5, -4));
            Func<Task> wrongInteger = async () => { await Integer(view, mode, key, 1); };
            await wrongInteger.Should().ThrowAsync<RespireServerException>();
            (await view.GetStringAsync(key)).Should().Be("-0.5");
            await view.Keys.DeleteAsync(key);
            await view.Lists.RightPushAsync(key, "item");
            Func<Task> wrongType = async () => { await Floating(view, mode, key, 1); };
            await wrongType.Should().ThrowAsync<RespireServerException>().WithMessage("*WRONGTYPE*");
        }

        // The fake and server accept all raw expiry units, including second-precision forms.
        foreach (var (token, value) in new[] { ("EX", 600L), ("PX", 600_000L),
            ("EXAT", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()),
            ("PXAT", DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds()) })
        {
            using var result = await client.ExecuteAsync("INCREX", ["raw-expiry", token, value]);
            result.Count.Should().Be(2);
            (await client.Keys.ExpiryAsync("raw-expiry")).TimeToLive.Should().BeGreaterThan(TimeSpan.FromMinutes(9));
        }
        RespireValue[][] invalid = [["BYINT", 1, "BYFLOAT", 1], ["BYINT", 1, "BYINT", 1],
            ["PERSIST", "ENX"], ["ENX"], ["PX", 0], ["PX", 1, "EX", 1], ["LBOUND", 2, "UBOUND", 1],
            ["BYFLOAT", "nan"], ["BYFLOAT", "inf"], ["BYFLOAT", 1, "LBOUND", "nan"]];
        foreach (var arguments in invalid)
        {
            Func<Task> rejected = async () => { using var result = await client.ExecuteAsync("INCREX", ["raw-expiry", .. arguments]); };
            await rejected.Should().ThrowAsync<RespireServerException>();
        }
    }

    private static async Task<RespireIncrementResult<long>> Integer(IRespireClient client, string mode, RespireKey key, long by,
        IntegerIncrementOptions options = default)
    {
        if (mode == "immediate") return await client.Strings.IncrementExtendedAsync(key, by, options);
        using var batch = mode == "batch" ? client.CreateBatch() : null;
        await using var transaction = mode == "transaction" ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var pending = queue.Strings.IncrementExtended(key, by, options);
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        return pending.Result;
    }

    private static async Task<RespireIncrementResult<double>> Floating(IRespireClient client, string mode, RespireKey key, double by,
        FloatIncrementOptions options = default)
    {
        if (mode == "immediate") return await client.Strings.IncrementExtendedAsync(key, by, options);
        using var batch = mode == "batch" ? client.CreateBatch() : null;
        await using var transaction = mode == "transaction" ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var pending = queue.Strings.IncrementExtended(key, by, options);
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        return pending.Result;
    }
}
