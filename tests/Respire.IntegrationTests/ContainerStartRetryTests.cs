using System.Net;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using TUnit.Core;
using static Respire.IntegrationTests.ContainerStartupRetryTests;
using static Respire.IntegrationTests.StandaloneStartupRetryTests;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
public class ContainerStartRetryTests
{
    private const string ListenFailure = "failed to set up container networking: driver failed programming external connectivity on endpoint sharp_colden (2db25cff5020): failed to listen on TCP socket: address already in use";
    private const string ExpectedRunId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ProbePassword = "páss\r\n!";

    [Test]
    [Arguments(true, false)]
    [Arguments(false, false)]
    [Arguments(true, true)]
    [Arguments(false, true)]
    public async Task WrongPublishedListenerStartsAFreshContainer(bool http, bool authenticate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var wrong = new TcpListener(IPAddress.Loopback, 0);
        using var ready = new TcpListener(IPAddress.Loopback, 0);
        wrong.Start();
        ready.Start();
        string wrongReply = http
            ? "HTTP/1.1 400 Bad Request\r\n\r\n"
            : InfoReply("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        Task wrongReplySent = ReplyAsync(wrong, wrongReply, timeout.Token, authenticate);
        Task readyReplySent = ReplyAsync(ready, InfoReply(ExpectedRunId), timeout.Token, authenticate);
        var attempts = new List<ContainerProbe>();
        int bindFailures = http ? 1 : 0;

        try
        {
            await using var started = await ContainerStartRetry.StartRedisAsync(() =>
            {
                attempts.Should().OnlyContain(attempt => attempt.DisposeCount == 1);
                var container = attempts.Count < bindFailures
                    ? ContainerProbe.Create(_ => Task.FromException(ApiError(ListenFailure)))
                    : PublishedContainer(attempts.Count == bindFailures ? wrong : ready);
                attempts.Add((ContainerProbe)container);
                return container;
            }, 6379, timeout.Token, password: authenticate ? ProbePassword : null);
            attempts.Should().HaveCount(bindFailures + 2).And.OnlyContain(attempt => attempt.StartCount == 1);
            started.Should().BeSameAs(attempts[^1]);
            attempts[^1].DisposeCount.Should().Be(0);
            await Task.WhenAll(wrongReplySent, readyReplySent);
        }
        finally
        {
            await timeout.CancelAsync();
        }
    }

    [Test]
    public async Task RejectedAuthenticationReportsEndpointWithoutRetrying()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var container = PublishedContainer(listener);
        var probe = (ContainerProbe)container;
        Task reply = ReplyAsync(listener, "-WRONGPASS invalid credentials\r\n", timeout.Token, authenticate: true);
        Func<Task<IContainer>> start = () => ContainerStartRetry.StartRedisAsync(
            () => container, 6379, timeout.Token, password: ProbePassword);

        try
        {
            var error = (await start.Should().ThrowAsync<IOException>()).Which;
            error.Message.Should().Contain($"127.0.0.1:{probe.MappedPort}");
            error.InnerException!.Message.Should().Contain("AUTH");
            probe.StartCount.Should().Be(1);
            probe.DisposeCount.Should().Be(1);
            await reply;
        }
        finally
        {
            await timeout.CancelAsync();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SilentPublishedEndpointTimesOutOrHonorsCallerCancellation(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var container = PublishedContainer(listener);
        var probe = (ContainerProbe)container;
        Task<IContainer> startup = ContainerStartRetry.StartRedisAsync(() => container, 6379, caller.Token);
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        using var stream = client.GetStream();
        await ReadInfoRequestAsync(stream, timeout.Token);

        Func<Task> start = () => startup;

        if (cancelCaller)
        {
            await caller.CancelAsync();
            var error = (await start.Should().ThrowAsync<OperationCanceledException>()).Which;
            error.CancellationToken.Should().Be(caller.Token);
        }
        else
        {
            var error = (await start.Should().ThrowAsync<TimeoutException>()).Which;
            error.Message.Should().Contain($"127.0.0.1:{probe.MappedPort}");
            error.InnerException.Should().BeAssignableTo<OperationCanceledException>();
        }

        probe.StartCount.Should().Be(1);
        probe.DisposeCount.Should().Be(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SuccessfulStartChecksThePublishedEndpoint(bool disconnect)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Task reply = ReplyAsync(listener, disconnect ? null : InfoReply(ExpectedRunId), timeout.Token);
        var container = PublishedContainer(listener);
        var probe = (ContainerProbe)container;
        int attempts = 0;
        Func<Task<IContainer>> start = () => ContainerStartRetry.StartRedisAsync(() =>
        {
            attempts++;
            return container;
        }, 6379, timeout.Token);

        try
        {
            if (disconnect)
            {
                var error = (await start.Should().ThrowAsync<IOException>()).Which;
                error.Message.Should().Contain($"127.0.0.1:{probe.MappedPort}");
                error.InnerException.Should().BeOfType<EndOfStreamException>();
                probe.DisposeCount.Should().Be(1);
            }
            else
            {
                await using var started = await start();
                started.Should().BeSameAs(container);
                probe.DisposeCount.Should().Be(0);
            }

            attempts.Should().Be(1);
            probe.StartCount.Should().Be(1);
            await reply;
        }
        finally
        {
            await timeout.CancelAsync();
        }
    }

    private static IContainer PublishedContainer(TcpListener listener)
    {
        var container = ContainerProbe.Create(_ => Task.CompletedTask);
        var probe = (ContainerProbe)container;
        probe.Hostname = "127.0.0.1";
        probe.MappedPort = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        probe.Execute = (_, _) => Task.FromResult(new ExecResult($"run_id:{ExpectedRunId}\r\n", "", 0));
        return container;
    }

    private static string InfoReply(string runId)
    {
        string info = $"# Server\r\nrun_id:{runId}\r\n";
        return "$" + FormattableString.Invariant($"{info.Length}\r\n{info}\r\n");
    }

    private static async Task ReplyAsync(
        TcpListener listener, string? reply, CancellationToken cancellationToken, bool authenticate = false)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        using var stream = client.GetStream();

        if (authenticate)
        {
            byte[] expected = "*2\r\n$4\r\nAUTH\r\n$8\r\npáss\r\n!\r\n"u8.ToArray();
            var request = new byte[expected.Length];
            await stream.ReadExactlyAsync(request, cancellationToken);
            request.Should().Equal(expected);

            if (reply is not null && (reply.StartsWith("HTTP/", StringComparison.Ordinal) || reply.StartsWith('-')))
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), cancellationToken);
                return;
            }

            await stream.WriteAsync("+OK\r\n"u8.ToArray(), cancellationToken);
        }

        await ReadInfoRequestAsync(stream, cancellationToken);

        if (reply is not null)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), cancellationToken);
        }
    }

    private static async Task ReadInfoRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var command = "*2\r\n$4\r\nINFO\r\n$6\r\nserver\r\n"u8.ToArray();
        var request = new byte[command.Length];
        await stream.ReadExactlyAsync(request, cancellationToken);
        request.Should().Equal(command);
    }

    [Test]
    [Arguments(0, ListenFailure)]
    [Arguments(1, ListenFailure)]
    [Arguments(2, ListenFailure)]
    [Arguments(2, StandaloneStartupRetryTests.BindFailure)]
    public async Task CollisionsStartFreshContainersUntilOneStarts(int collisions, string message)
    {
        var probes = new List<ContainerProbe>();
        var started = await ContainerStartRetry.StartAsync(() =>
        {
            // A failed container is removed before its replacement is built.
            probes.Should().OnlyContain(probe => probe.DisposeCount == 1);
            var collide = probes.Count < collisions;
            var container = ContainerProbe.Create(_ => collide ? Task.FromException(ApiError(message)) : Task.CompletedTask);
            probes.Add((ContainerProbe)container);
            return container;
        }, [6379]);
        probes.Should().HaveCount(collisions + 1).And.OnlyContain(probe => probe.StartCount == 1);
        started.Should().BeSameAs(probes[^1]);
        probes[^1].DisposeCount.Should().Be(0);
        probes.Take(collisions).Should().OnlyContain(probe => probe.DisposeCount == 1);
    }

    [Test]
    public async Task ExhaustionRethrowsTheThirdCollisionWithThePreviousOnes()
    {
        var probes = new List<ContainerProbe>();
        var failures = new List<Exception>();
        Func<Task> start = () => ContainerStartRetry.StartAsync(() =>
        {
            var failure = ApiError(ListenFailure);
            failures.Add(failure);
            var container = ContainerProbe.Create(_ => Task.FromException(failure));
            probes.Add((ContainerProbe)container);
            return container;
        }, [6379]);
        var error = (await start.Should().ThrowAsync<Docker.DotNet.DockerApiException>()).Which;
        error.Should().BeSameAs(failures[2]);
        error.Data[ContainerStartRetry.PreviousPortCollisionsKey].Should().BeEquivalentTo(failures.Take(2).ToArray());
        probes.Should().HaveCount(3).And.OnlyContain(probe => probe.StartCount == 1 && probe.DisposeCount == 1);
    }

    [Test]
    [Arguments("failed to set up container networking: driver failed programming external connectivity on endpoint fixture (abc123): failed to listen on UDP socket: address already in use", 6379)]
    [Arguments("failed to set up container networking: driver failed programming external connectivity on endpoint fixture (abc123): failed to listen on TCP socket: permission denied", 6379)]
    [Arguments("image pull failed (abc123): failed to listen on TCP socket: address already in use", 6379)]
    [Arguments("failed to set up container networking: driver failed programming external connectivity on endpoint fixture (abc123): failed to bind host port for 127.0.0.1::172.17.0.8:6379/tcp: address already in use", 7000)]
    public async Task OtherFailuresAreRemovedAndNeverRetried(string message, int containerPort)
        => await AssertNoRetryAsync(ApiError(message), containerPort);

    [Test]
    public async Task NonDockerFailuresAreRemovedAndNeverRetried()
        => await AssertNoRetryAsync(new IOException(ListenFailure), 6379);

    [Test]
    public async Task CancellationDuringCleanupPreventsRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var probes = new List<ContainerProbe>();
        Func<Task> start = () => ContainerStartRetry.StartAsync(() =>
        {
            var container = ContainerProbe.Create(_ => Task.FromException(ApiError(ListenFailure)));
            var probe = (ContainerProbe)container;
            probe.Cleanup = () =>
            {
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            };
            probes.Add(probe);
            return container;
        }, [6379], cancellation.Token);
        (await start.Should().ThrowAsync<OperationCanceledException>()).Which.CancellationToken.Should().Be(cancellation.Token);
        probes.Should().ContainSingle().Which.DisposeCount.Should().Be(1);
    }

    [Test]
    public async Task CleanupFailureStopsRetriesAndRetainsCauses()
    {
        var failures = new List<Exception>();
        var cleanup = new IOException("controlled cleanup failure");
        Func<Task> start = () => ContainerStartRetry.StartAsync(() =>
        {
            var failure = ApiError(ListenFailure);
            failures.Add(failure);
            var container = ContainerProbe.Create(_ => Task.FromException(failure));

            if (failures.Count == 2)
            {
                ((ContainerProbe)container).Cleanup = () => ValueTask.FromException(cleanup);
            }

            return container;
        }, [6379]);
        var error = (await start.Should().ThrowAsync<AggregateException>()).Which;
        error.InnerExceptions.Should().Equal(failures.Append(cleanup));
    }

    private static async Task AssertNoRetryAsync(Exception expected, int containerPort)
    {
        var probes = new List<ContainerProbe>();
        Func<Task> start = () => ContainerStartRetry.StartAsync(() =>
        {
            var container = ContainerProbe.Create(_ => Task.FromException(expected));
            probes.Add((ContainerProbe)container);
            return container;
        }, [containerPort]);
        Exception? actual = null;

        try
        {
            await start();
        }
        catch (Exception error) { actual = error; }

        actual.Should().BeSameAs(expected);
        probes.Should().ContainSingle().Which.DisposeCount.Should().Be(1);
    }
}
