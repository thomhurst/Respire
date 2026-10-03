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
    public async Task DiagnosticCollectionWithNoPortsDoesNotRunCommand()
    {
        var container = ContainerProbe.Create(_ => Task.CompletedTask);
        var called = false;
        ((ContainerProbe)container).Execute = (_, _) =>
        {
            called = true;
            return Task.FromResult(new ExecResult("", "", 0));
        };
        var diagnostics = await ContainerStartupDiagnostics.CaptureAsync(container, []);
        called.Should().BeFalse();
        diagnostics.Should().Contain("no daemon ports");
    }

    [Test]
    public async Task DiagnosticCollectionSupportsTailWithoutVerboseOption()
    {
        var container = ContainerProbe.Create(_ => Task.CompletedTask);
        ((ContainerProbe)container).Execute = (command, _) => Task.FromResult(command.Contains("-v")
            ? new ExecResult("", "tail: unsupported option -v", 1)
            : new ExecResult($"portable daemon log from {command[^1]}", "", 0));
        var diagnostics = await ContainerStartupDiagnostics.CaptureAsync(container, [6381, 6382]);
        diagnostics.Should().Contain("portable daemon log").And.Contain("6381.log").And.Contain("6382.log");
    }

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

    // This serialized regression deliberately replaces stderr and restores it in finally.
#pragma warning disable TUnit0055
    [Test]
    [NotInParallel]
    public async Task StartupFailureDoesNotDependOnStandardError()
    {
        var original = new IOException("Controlled startup failure.");
        var container = ContainerProbe.Create(_ => Task.CompletedTask);
        var probe = (ContainerProbe)container;
        probe.HostnameError = original;
        probe.Execute = (_, _) => Task.FromResult(new ExecResult("preserved daemon tail", "", 0));
        var previous = Console.Error;
        using var writer = new RejectingErrorWriter();
        Console.SetError(writer);
        try
        {
            Func<Task> start = async () => await RespireContainerFixture.StartAsync(new(), default,
                (_, _) => Task.FromResult(container));
            (await start.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(original);
            original.Data["RespireFixture.DaemonLogs"].Should().BeOfType<string>().Which
                .Should().Contain("preserved daemon tail");
            probe.DisposeCount.Should().Be(1);
            writer.Writes.Should().BeGreaterThan(0);
        }
        finally { Console.SetError(previous); }
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StartupFailureReportsAfterCleanupWithBoundedWait(bool blocked)
    {
        var original = new IOException("Controlled startup failure.");
        var container = ContainerProbe.Create(_ => Task.CompletedTask);
        var probe = (ContainerProbe)container;
        probe.HostnameError = original;
        probe.Execute = (_, _) => Task.FromResult(new ExecResult("automatic daemon tail", "", 0));
        var previous = Console.Error;
        using var writer = new ControlledErrorWriter(() => probe.DisposeCount, blocked);
        Console.SetError(writer);
        try
        {
            Func<Task> start = async () => await RespireContainerFixture.StartAsync(new(), default,
                (_, _) => Task.FromResult(container)).WaitAsync(TimeSpan.FromSeconds(10));
            (await start.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(original);
            writer.CleanupCount.Should().Be(1);
            writer.Text.Should().Contain("automatic daemon tail");
            writer.Completed.Task.IsCompleted.Should().Be(!blocked);
            // Another failure must not queue a second worker behind a stalled sink.
            if (blocked)
            {
                await ContainerStartupDiagnostics.ReportAsync("second report").WaitAsync(TimeSpan.FromSeconds(10));
                writer.Writes.Should().Be(1);
            }
            else
            {
                await Task.WhenAll(Enumerable.Range(0, 8)
                    .Select(index => ContainerStartupDiagnostics.ReportAsync($"report {index}")));
                writer.Writes.Should().Be(9);
            }
        }
        finally
        {
            writer.Release.Set();
            await writer.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Console.SetError(previous);
        }
    }
#pragma warning restore TUnit0055

    private sealed class ControlledErrorWriter(Func<int> cleanupCount, bool blocked) : TextWriter
    {
        internal readonly ManualResetEventSlim Release = new(false);
        internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CleanupCount;
        internal int Writes;
        internal string? Text;
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value)
        {
            CleanupCount = cleanupCount();
            Text = value;
            Interlocked.Increment(ref Writes);
            if (blocked) Release.Wait();
            Completed.TrySetResult();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Release.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class RejectingErrorWriter : TextWriter
    {
        internal int Writes;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value)
        {
            Writes++;
            throw new IOException("Standard error is unavailable.");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExpiredStartupTokenDoesNotCancelDiagnostics(bool callerCancellation)
    {
        using var caller = new CancellationTokenSource();
        var clock = new CredentialTestClock();
        var original = new IOException("Failed after startup cancellation.");
        CancellationToken startupToken = default;
        var container = ContainerProbe.Create(async token =>
        {
            startupToken = token;
            if (callerCancellation) caller.Cancel();
            else clock.Advance(TimeSpan.FromMilliseconds(100));
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
        }, caller.Token, (_, _) => Task.FromResult(container), clock);
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
        var clock = new CredentialTestClock();
        var original = new IOException("Startup failed.");
        var pending = new TaskCompletionSource<ExecResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var container = ContainerProbe.Create(_ => Task.CompletedTask);
        var probe = (ContainerProbe)container;
        probe.HostnameError = original;
        CancellationToken diagnosticToken = default;
        probe.Execute = (_, token) =>
        {
            diagnosticToken = token;
            clock.Advance(TimeSpan.FromSeconds(2));
            return pending.Task;
        };
        try
        {
            Func<Task> start = async () => await RespireContainerFixture.StartAsync(new(), default,
                (_, _) => Task.FromResult(container), clock).WaitAsync(TimeSpan.FromSeconds(10));
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
    [Arguments(false)]
    [Arguments(true)]
    public async Task OversizedPortsEachRetainTheirLabelAndTail(bool fails)
    {
        var container = ContainerProbe.Create(_ => Task.CompletedTask);
        ((ContainerProbe)container).Execute = (command, _) =>
        {
            var tail = $"last line from {command[^1]}";
            var text = new string('x', ContainerStartupDiagnostics.MaximumCharacters * 2) + tail;
            return fails ? Task.FromException<ExecResult>(new IOException(text))
                : Task.FromResult(new ExecResult(text, "", 0));
        };
        var diagnostics = await ContainerStartupDiagnostics.CaptureAsync(container, [6381, 6382, 6383]);
        diagnostics.Length.Should().BeLessThanOrEqualTo(ContainerStartupDiagnostics.MaximumCharacters);
        foreach (var port in new[] { 6381, 6382, 6383 })
        {
            diagnostics.Should().Contain($"[truncated]\n/tmp/respire-fixture/{port}.log");
            diagnostics.Should().Contain($"last line from /tmp/respire-fixture/{port}.log");
        }
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
