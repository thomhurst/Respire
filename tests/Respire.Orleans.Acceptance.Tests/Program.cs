using System.Diagnostics.CodeAnalysis;

namespace Respire.Orleans.Acceptance.Tests;

internal static class Program
{
    [SuppressMessage("Usage", "TUnit0034", Justification = "A child silo must start without entering the test runner.")]
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--child-recovery", var topology, var data, var sentinels, var failedElection])
        {
            try
            {
                await SiloScenarios.RecoverRedisConnection(new(
                    Enum.Parse<Respire.Testing.Containers.RespireContainerTopology>(topology), data, sentinels),
                    bool.Parse(failedElection));
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return SiloScenarios.RecoveryDeadlineExpired ? SiloScenarios.RecoveryDeadlineExitCode : 1;
            }
        }
        if (args is ["--child-silo", var endpoint, var serviceId, var clusterId])
        {
            await SiloScenarios.RunChildSilo(endpoint, serviceId, clusterId);
            return 0;
        }
        return await MicrosoftTestingPlatformApplication.RunAsync(args);
    }
}
