using System.Globalization;
using Microsoft.Extensions.Configuration;
using ModularPipelines.Modules;
using Respire.Pipeline.Modules;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Pipeline.Tests;

public class RunUnitTestsModuleTests
{
    [Test]
    public async Task HangDumpInactivityThresholdLeavesTimeBeforeModuleDeadline()
    {
        IModule module = new RunUnitTestsModule(new ConfigurationBuilder().Build());
        var arguments = RunUnitTestsModule.CreateDiagnosticsArguments();
        var timeoutIndex = Array.IndexOf(arguments, "--hangdump-timeout");
        await Assert.That(timeoutIndex).IsGreaterThanOrEqualTo(0);
        var timeout = arguments[timeoutIndex + 1];
        await Assert.That(timeout.EndsWith('m')).IsTrue();
        var inactivity = TimeSpan.FromMinutes(double.Parse(timeout[..^1], CultureInfo.InvariantCulture));
        await Assert.That(inactivity).IsGreaterThan(TimeSpan.Zero);
        await Assert.That(module.Configuration.Timeout).IsNotNull();
        await Assert.That(module.Configuration.Timeout!.Value).IsGreaterThan(inactivity + TimeSpan.FromMinutes(1));
    }
}