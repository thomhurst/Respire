using System.Text;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Respire.Testing.Containers;
using Respire.Tests.Networking;
using TUnit.Core;
using static Respire.IntegrationTests.ContainerStartupRetryTests;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
public class StandalonePublishedPortTests
{
    private const string ExpectedRunId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherRunId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Test]
    [Arguments(RespireContainerServer.Redis, false)]
    [Arguments(RespireContainerServer.Valkey, false)]
    [Arguments(RespireContainerServer.Redis, true)]
    public async Task WrongListenerIsDisposedBeforeRetryingWithTheCorrectListener(RespireContainerServer server, bool http)
    {
        byte[] wrongReply = http ? "HTTP/1.1 400 Bad Request\r\n\r\n"u8.ToArray() : InfoReply(OtherRunId);
        await using var wrong = new FakeRespServer(wrongReply);
        await using var correct = new FakeRespServer(InfoReply(ExpectedRunId));
        var rejected = CreateContainer(wrong.Port, server);
        var accepted = CreateContainer(correct.Port, server);
        int attempts = 0;

        await using var fixture = await RespireContainerFixture.StartAsync(new() { Server = server }, default, (_, _) =>
        {
            if (++attempts == 1)
            {
                return Task.FromResult((IContainer)rejected);
            }

            rejected.DisposeCount.Should().Be(1);
            return Task.FromResult((IContainer)accepted);
        });

        attempts.Should().Be(2);
        fixture.DataEndpoints.Should().Equal(new RespireEndpoint("127.0.0.1", correct.Port));
        accepted.DisposeCount.Should().Be(0);
        wrong.ReceivedCommands.Should().Equal("INFO server");
        correct.ReceivedCommands.Should().Equal("INFO server");
    }

    [Test]
    public async Task WrongListenersUseTheSameThreeAttemptsAsBindCollisions()
    {
        await using var firstWrong = new FakeRespServer(InfoReply(OtherRunId));
        await using var secondWrong = new FakeRespServer(InfoReply(OtherRunId));
        var bindError = StandaloneStartupRetryTests.ApiError(StandaloneStartupRetryTests.BindFailure);
        var probes = new[]
        {
            (ContainerProbe)ContainerProbe.Create(_ => Task.FromException(bindError)),
            CreateContainer(firstWrong.Port),
            CreateContainer(secondWrong.Port),
        };
        int attempts = 0;
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(new(), default, (_, _) =>
        {
            probes.Take(attempts).Should().OnlyContain(probe => probe.DisposeCount == 1);
            return Task.FromResult((IContainer)probes[attempts++]);
        });

        var error = (await start.Should().ThrowAsync<IOException>()).Which;
        error.Message.Should().Contain($"127.0.0.1:{secondWrong.Port}").And.Contain(ExpectedRunId).And.Contain(OtherRunId);
        error.Data["RespireFixture.StartupAttempt"].Should().Be(3);
        var previous = (Exception[])error.Data["RespireFixture.PreviousPortCollisions"]!;
        previous.Should().HaveCount(2);
        previous[0].Should().BeSameAs(bindError);
        previous[1].Message.Should().Contain($"127.0.0.1:{firstWrong.Port}");
        probes.Should().OnlyContain(probe => probe.StartCount == 1 && probe.DisposeCount == 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnconfirmedMismatchDoesNotRetry(bool daemonRestarted)
    {
        byte[] reply = daemonRestarted ? InfoReply(OtherRunId) : "-ERR INFO unavailable\r\n"u8.ToArray();
        await using var listener = new FakeRespServer(reply);
        var probe = CreateContainer(listener.Port, currentRunId: daemonRestarted ? OtherRunId : ExpectedRunId);
        int attempts = 0;
        Func<Task> start = async () => await RespireContainerFixture.StartAsync(new(), default, (_, _) =>
        {
            attempts++;
            return Task.FromResult((IContainer)probe);
        });

        var error = (await start.Should().ThrowAsync<IOException>()).Which;
        error.Message.Should().Contain($"127.0.0.1:{listener.Port}");
        error.InnerException!.Message.Should().Contain(daemonRestarted ? "changed run_id" : "Unexpected INFO server reply");
        attempts.Should().Be(1);
        probe.DisposeCount.Should().Be(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SilentListenerHonorsStartupDeadlineAndCallerCancellation(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        var clock = new CredentialTestClock();
        var timeout = TimeSpan.FromSeconds(10);
        var infoReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new FakeRespServer(FakeRespServer.PongReply)
        {
            SuppressReply = _ =>
            {
                infoReceived.TrySetResult();
                return true;
            },
        };
        var probe = CreateContainer(listener.Port);
        Task<RespireContainerFixture> startup = RespireContainerFixture.StartAsync(new() { StartupTimeout = timeout },
            caller.Token, (_, _) => Task.FromResult((IContainer)probe), clock);
        Func<Task> start = () => startup;

        try
        {
            await Task.WhenAny(infoReceived.Task, startup).WaitAsync(TimeSpan.FromSeconds(10));
            infoReceived.Task.IsCompletedSuccessfully.Should().BeTrue();

            if (cancelCaller)
            {
                await caller.CancelAsync();
                var error = (await start.Should().ThrowAsync<OperationCanceledException>()).Which;
                error.CancellationToken.Should().Be(caller.Token);
            }
            else
            {
                clock.Advance(timeout);
                var error = (await start.Should().ThrowAsync<TimeoutException>()).Which;
                error.Message.Should().Contain($"127.0.0.1:{listener.Port}");
            }

            probe.StartCount.Should().Be(1);
            probe.DisposeCount.Should().Be(1);
        }
        finally
        {
            await caller.CancelAsync();

            if (startup.IsCompletedSuccessfully)
            {
                var fixture = await startup;
                await fixture.DisposeAsync();
            }
        }
    }

    private static ContainerProbe CreateContainer(int port, RespireContainerServer server = RespireContainerServer.Redis,
        string currentRunId = ExpectedRunId)
    {
        var probe = (ContainerProbe)ContainerProbe.Create(_ => Task.CompletedTask);
        string cli = server == RespireContainerServer.Redis ? "redis-cli" : "valkey-cli";
        bool passedReadiness = false;
        int runIdReads = 0;
        probe.Hostname = "127.0.0.1";
        probe.MappedPort = (ushort)port;
        probe.Execute = (command, _) =>
        {
            string output = "";

            if (command[^1] == "PING")
            {
                command.Should().Equal(cli, "--raw", "-p", "6379", "PING");
                passedReadiness = true;
                output = "PONG\n";
            }
            else if (command.TakeLast(2).SequenceEqual(["INFO", "server"]))
            {
                command[0].Should().Be(cli);
                passedReadiness.Should().BeTrue();
                string runId = runIdReads++ == 0 ? ExpectedRunId : currentRunId;
                output = $"# Server\r\nrun_id:{runId}\r\n";
            }

            return Task.FromResult(new ExecResult(output, "", 0));
        };
        return probe;
    }

    private static byte[] InfoReply(string runId)
    {
        string info = $"# Server\r\nrun_id:{runId}\r\n";
        return Encoding.ASCII.GetBytes(FormattableString.Invariant($"${info.Length}\r\n{info}\r\n"));
    }
}
