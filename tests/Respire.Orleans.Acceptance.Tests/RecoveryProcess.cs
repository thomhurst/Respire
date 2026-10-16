using System.Diagnostics;
using Respire.Testing.Containers;
using Assert = Xunit.Assert;

namespace Respire.Orleans.Acceptance.Tests;

public partial class SiloScenarios
{
    private const string RecoveryCommand = "RESPIRE-RECOVERY:";
    internal const int RecoveryDeadlineExitCode = 2;
    // Child-process state survives host disposal exceptions during stack unwinding.
    internal static bool RecoveryDeadlineExpired { get; private set; }

    private static async Task<int> RunRecoveryProcess(RespireContainerTopology topology, bool failedElection)
    {
        // The parent owns Redis: killing an uncooperative host must not skip fixture disposal.
        var redis = await StartRedis(topology);
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            };
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(SiloScenarios).Assembly.Location);
            foreach (var argument in new[] { "--child-recovery", topology.ToString(),
                string.Join(',', redis.DataEndpoints), string.Join(',', redis.SentinelEndpoints), failedElection.ToString() })
                start.ArgumentList.Add(argument);
            using var child = Process.Start(start)!;
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            // Keep reads alive through termination and the 10s output drain, even when
            // the four-minute lifetime expires. Total cleanup budget is documented as 310s.
            using var outputLifetime = new CancellationTokenSource();
            var output = new List<string>();
            var stdout = PumpOutput();
            var stderr = PumpError();
            try
            {
                var exited = child.WaitForExitAsync(lifetime.Token);
                await Task.WhenAny(exited, stdout).WaitAsync(lifetime.Token);
                if (stdout.IsCompleted) await stdout;
                await exited;
                await stdout.WaitAsync(lifetime.Token);
                await stderr.WaitAsync(lifetime.Token);
                if (child.ExitCode != 0) await Diagnostics();
                if (failedElection)
                {
                    Assert.Equal(RecoveryDeadlineExitCode, child.ExitCode);
                    Assert.Contains(output, line => line == "Recovery deadline expired; collecting server diagnostics.");
                }
                return child.ExitCode;
            }
            catch (OperationCanceledException error) when (lifetime.IsCancellationRequested)
            {
                await Diagnostics();
                throw new TimeoutException($"Recovery child {child.Id} exceeded its four-minute lifetime.", error);
            }
            catch
            {
                await Diagnostics();
                throw;
            }
            finally
            {
                // Only this child hosts the recovery silo/client and owns their connections.
                try
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (Exception error) { Console.WriteLine($"Recovery child termination: {error.Message}"); }
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception error) { Console.WriteLine($"Recovery output drain: {error.Message}"); }
                finally { outputLifetime.Cancel(); }
                if (child.HasExited) Console.WriteLine($"Recovery child {child.Id} exited with code {child.ExitCode}.");
                else Console.WriteLine($"Recovery child {child.Id} did not exit after termination.");
            }

            async Task PumpError()
            {
                while (await child.StandardError.ReadLineAsync(outputLifetime.Token) is { } line)
                    Console.WriteLine(line);
            }

            async Task PumpOutput()
            {
                while (await child.StandardOutput.ReadLineAsync(outputLifetime.Token) is { } line)
                {
                    output.Add(line);
                    Console.WriteLine(line);
                    if (!line.StartsWith(RecoveryCommand, StringComparison.Ordinal)) continue;
                    using var command = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    var operation = line[RecoveryCommand.Length..];
                    command.CancelAfter(TimeSpan.FromSeconds(operation == "replica:0" ? 90 : 15));
                    try
                    {
                        var task = operation switch
                        {
                            "stop:0" => redis.StopDataNodeAsync(0, command.Token),
                            "stop:1" => redis.StopDataNodeAsync(1, command.Token),
                            "start:0" => redis.StartDataNodeAsync(0, command.Token),
                            "replica:0" => redis.WaitForDataNodeReplicaAsync(0, command.Token),
                            "logs" => Diagnostics(),
                            _ => throw new InvalidOperationException($"Unknown recovery command: {operation}")
                        };
                        await task.WaitAsync(command.Token);
                        await child.StandardInput.WriteLineAsync("ok".AsMemory(), command.Token);
                        await child.StandardInput.FlushAsync(command.Token);
                    }
                    catch (Exception error)
                    {
                        Console.WriteLine($"Recovery command {operation} failed: {error.Message}");
                        // The command token may already be cancelled. Bound the failure
                        // reply separately, then keep draining the child's diagnostics.
                        using var reply = CancellationTokenSource.CreateLinkedTokenSource(outputLifetime.Token);
                        reply.CancelAfter(TimeSpan.FromSeconds(1));
                        try
                        {
                            await child.StandardInput.WriteLineAsync("fail".AsMemory(), reply.Token);
                            await child.StandardInput.FlushAsync(reply.Token);
                        }
                        catch (Exception replyError) { Console.WriteLine($"Recovery failure reply: {replyError.Message}"); }
                    }
                }
            }

            async Task Diagnostics()
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { Console.WriteLine(await redis.ReadServerLogsAsync(deadline.Token).WaitAsync(deadline.Token)); }
                catch (Exception error) { Console.WriteLine($"Server diagnostics unavailable: {error.Message}"); }
            }
        }
        finally
        {
            var containerId = redis.ContainerId;
            Console.WriteLine($"Removing recovery container {containerId}.");
            await redis.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            Console.WriteLine($"Removed recovery container {containerId}.");
        }
    }

    // Only the parent controls and disposes the fixture. The child sends serialized
    // requests on stdin/stdout so its termination cannot bypass container cleanup.
    internal sealed class RecoveryDeployment(RespireContainerTopology topology, string data, string sentinels)
    {
        public RespireContainerTopology Topology => topology;
        public RespireEndpoint[] DataEndpoints { get; } = data.Split(',').Select(RespireEndpoint.Parse).ToArray();
        public RespireOptions Options => RecoveryOptions(new RespireOptions
        {
            Endpoints = topology == RespireContainerTopology.Sentinel
                ? sentinels.Split(',').Select(RespireEndpoint.Parse).ToList() : DataEndpoints.ToList(),
            SentinelPrimaryName = topology == RespireContainerTopology.Sentinel ? RespireContainerFixture.SentinelServiceName : null
        });

        public Task StopDataNodeAsync(int index, CancellationToken token) => Request($"stop:{index}", token);
        public Task StartDataNodeAsync(int index, CancellationToken token) => Request($"start:{index}", token);
        public Task WaitForDataNodeReplicaAsync(int index, CancellationToken token) => Request($"replica:{index}", token);
        public Task ReadServerLogsAsync(CancellationToken token) => Request("logs", token);

        private static async Task Request(string operation, CancellationToken token)
        {
            Console.WriteLine(RecoveryCommand + operation);
            if (await Console.In.ReadLineAsync(token) != "ok")
                throw new IOException($"Parent did not acknowledge recovery command {operation}.");
        }
    }
}
