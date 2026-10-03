using System.Text;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Respire.Testing.Containers;
using TUnit.Core;
using static Respire.IntegrationTests.ContainerStartupRetryTests;

namespace Respire.IntegrationTests;

public class ContainerStartupDiagnosticsTests
{
    [Test]
    public async Task StartupFailureCapturesDaemonLogsBeforeCleanup()
    {
        var original = new IOException("Controlled startup failure.");
        var container = ContainerProbe.Create(_ => Task.CompletedTask);
        var probe = (ContainerProbe)container;
        probe.HostnameError = original;
        var captured = false;
        probe.Execute = (_, token) =>
        {
            probe.DisposeCount.Should().Be(0);
            token.IsCancellationRequested.Should().BeFalse();
            captured = true;
            return Task.FromResult(new ExecResult("daemon launch failed", "missing second log", 1));
        };
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(
            new() { Topology = RespireContainerTopology.Sentinel }, default, (_, _) => Task.FromResult(container));
        var error = (await start.Should().ThrowAsync<IOException>()).Which;
        error.Should().BeSameAs(original);
        captured.Should().BeTrue();
        error.Data["RespireFixture.DaemonLogs"].Should().BeOfType<string>().Which
            .Should().Contain("daemon launch failed").And.Contain("missing second log");
        probe.DisposeCount.Should().Be(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExpiredStartupTokenDoesNotCancelDiagnostics(bool callerCancellation)
    {
        using var caller = new CancellationTokenSource();
        var original = new IOException("Failed after startup cancellation.");
        CancellationToken startupToken = default;
        var container = ContainerProbe.Create(async token =>
        {
            startupToken = token;
            if (callerCancellation) caller.Cancel();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        });
        var probe = (ContainerProbe)container;
        probe.HostnameError = original;
        probe.Execute = (_, token) =>
        {
            startupToken.IsCancellationRequested.Should().BeTrue();
            token.Should().NotBe(startupToken);
            token.IsCancellationRequested.Should().BeFalse();
            return Task.FromResult(new ExecResult("diagnostics after cancellation", "", 0));
        };
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(new()
        {
            StartupTimeout = callerCancellation ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(100),
        }, caller.Token, (_, _) => Task.FromResult(container));
        Exception error;
        if (callerCancellation)
        {
            var cancellation = (await start.Should().ThrowAsync<OperationCanceledException>()).Which;
            cancellation.CancellationToken.Should().Be(caller.Token);
            error = cancellation;
        }
        else error = (await start.Should().ThrowAsync<TimeoutException>()).Which;
        error.InnerException.Should().BeSameAs(original);
        original.Data["RespireFixture.DaemonLogs"].Should().BeOfType<string>().Which
            .Should().Contain("diagnostics after cancellation");
        probe.DisposeCount.Should().Be(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DiagnosticFailurePreservesStartupAndCleanupFailures(bool cleanupFails)
    {
        var original = new IOException("Startup failed.");
        var cleanup = new IOException("Cleanup failed.");
        var container = ContainerProbe.Create(_ => Task.CompletedTask);
        var probe = (ContainerProbe)container;
        probe.HostnameError = original;
        probe.Execute = (_, _) => throw new InvalidOperationException("Docker exec unavailable.");
        if (cleanupFails) probe.Cleanup = () => ValueTask.FromException(cleanup);
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(new(), default,
            (_, _) => Task.FromResult(container));
        if (cleanupFails)
            (await start.Should().ThrowAsync<AggregateException>()).Which.InnerExceptions.Should().Equal(original, cleanup);
        else (await start.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(original);
        original.Data["RespireFixture.DaemonLogs"].Should().BeOfType<string>().Which
            .Should().Contain("Docker exec unavailable");
        probe.DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task IgnoredDiagnosticCancellationCannotPreventCleanup()
    {
        var original = new IOException("Startup failed.");
        var pending = new TaskCompletionSource<ExecResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var container = ContainerProbe.Create(_ => Task.CompletedTask);
        var probe = (ContainerProbe)container;
        probe.HostnameError = original;
        CancellationToken diagnosticToken = default;
        probe.Execute = (_, token) => { diagnosticToken = token; return pending.Task; };
        try
        {
            Func<Task> start = async () => await RespireContainerFixture.StartAsync(new(), default,
                (_, _) => Task.FromResult(container)).WaitAsync(TimeSpan.FromSeconds(10));
            (await start.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(original);
            diagnosticToken.IsCancellationRequested.Should().BeTrue();
            pending.Task.IsCompleted.Should().BeFalse();
            probe.DisposeCount.Should().Be(1);
            original.Data["RespireFixture.DaemonLogs"].Should().BeOfType<string>().Which.Should().Contain("exceeded");
        }
        finally { pending.TrySetException(new IOException("Late diagnostic transport failure.")); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OversizedDiagnosticOutputOrErrorIsBounded(bool fails)
    {
        var text = new string('x', ContainerStartupDiagnostics.MaximumCharacters * 2) + "last diagnostic line";
        var container = ContainerProbe.Create(_ => Task.CompletedTask);
        var probe = (ContainerProbe)container;
        probe.Execute = (_, _) => fails ? Task.FromException<ExecResult>(new IOException(text))
            : Task.FromResult(new ExecResult(text, "stderr tail", 1));
        var diagnostics = await ContainerStartupDiagnostics.CaptureAsync(container, [6379]);
        diagnostics.Length.Should().BeLessThanOrEqualTo(ContainerStartupDiagnostics.MaximumCharacters);
        diagnostics.Should().StartWith("[truncated]").And.Contain("last diagnostic line");
        if (!fails) diagnostics.Should().EndWith("stderr tail");
    }

    [Test]
    [Arguments(RespireContainerServer.Redis)]
    [Arguments(RespireContainerServer.Valkey)]
    public async Task RealDaemonLogCollectionIncludesOnlyBoundedTails(RespireContainerServer server)
    {
        await using var container = RespireContainerFixture.BuildContainer(new() { Server = server }, [6379]);
        await container.StartAsync();
        await container.ExecAsync(["mkdir", "-p", "/tmp/respire-fixture"]);
        await container.CopyAsync(Encoding.UTF8.GetBytes("discarded beginning\n" + new string('x', 8192) + "\nlast daemon line"),
            "/tmp/respire-fixture/6379.log");
        var diagnostics = await ContainerStartupDiagnostics.CaptureAsync(container, [6379, 6380]);
        diagnostics.Should().Contain("6379.log").And.Contain("last daemon line")
            .And.NotContain("discarded beginning").And.Contain("6380.log");
        diagnostics.Length.Should().BeLessThan(5000);
    }
}
