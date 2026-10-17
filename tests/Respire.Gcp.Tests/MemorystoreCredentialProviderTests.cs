using System.Net;
using System.Security.Cryptography;
using System.Text;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Http;
using Google.Apis.Util;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Clock = Respire.Testing.CredentialTestClock;

namespace Respire.Gcp.Tests;

public class MemorystoreCredentialProviderTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Test]
    public async Task ReturnsDefaultUserAccessTokenAndActualExpiry()
    {
        var clock = new Clock();
        var source = new TokenSource();
        var provider = CreateProvider(clock, source);
        var first = await provider.GetCredentialsAsync();
        clock.Advance(TimeSpan.FromMinutes(10));
        var cached = await provider.GetCredentialsAsync();

        await Assert.That(first.Username).IsNull();
        await Assert.That(first.Password).IsEqualTo("token-1");
        await Assert.That(first.ExpiresAt).IsEqualTo(new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero));
        await Assert.That(cached.ExpiresAt).IsEqualTo(first.ExpiresAt);
        await Assert.That(source.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task RefreshesCachedTokenBeforeExpiry()
    {
        var clock = new Clock();
        var source = new TokenSource();
        var provider = CreateProvider(clock, source);
        var first = await provider.GetCredentialsAsync();
        clock.Advance(TimeSpan.FromSeconds(3550));
        var second = await provider.GetCredentialsAsync();

        await Assert.That(source.Calls).IsEqualTo(2);
        await Assert.That(second.Password).IsEqualTo("token-2");
        await Assert.That(second.ExpiresAt).IsEqualTo(clock.GetUtcNow().AddHours(1));
        await Assert.That(clock.GetUtcNow() < first.ExpiresAt).IsTrue();
    }

    [Test]
    public async Task SupportsUserCredentials()
    {
        var clock = new Clock();
        var source = new TokenSource();
        using var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = "client", ClientSecret = "secret" },
            Clock = new GoogleClock(clock), HttpClientFactory = source,
        });
        var user = new UserCredential(flow, "user", new TokenResponse { RefreshToken = "refresh-token" });
        var provider = new RespireMemorystoreCredentialProvider(user.ToGoogleCredential());

        var credentials = await provider.GetCredentialsAsync();

        await Assert.That(credentials.Password).IsEqualTo("token-1");
        await Assert.That(credentials.ExpiresAt).IsEqualTo(clock.GetUtcNow().AddHours(1));
        await Assert.That(source.LastBody).Contains("grant_type=refresh_token");
    }

    [Test]
    public async Task ServiceAccountRequestsCloudPlatformOAuthInsteadOfSelfSignedJwt()
    {
        var clock = new Clock();
        var source = new TokenSource();
        using var key = RSA.Create(2048);
        var serviceAccount = new ServiceAccountCredential(new ServiceAccountCredential.Initializer("service@example.iam.gserviceaccount.com")
        {
            Key = key, Clock = new GoogleClock(clock), HttpClientFactory = source,
            UseJwtAccessWithScopes = true,
        });
        var provider = new RespireMemorystoreCredentialProvider(serviceAccount.ToGoogleCredential());

        var credentials = await provider.GetCredentialsAsync();

        await Assert.That(credentials.Password).IsEqualTo("token-1");
        var assertion = Uri.UnescapeDataString(source.LastBody!.Split("assertion=")[1].Split('&')[0]);
        var payload = assertion.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        await Assert.That(Encoding.UTF8.GetString(Convert.FromBase64String(payload)))
            .Contains(RespireMemorystoreCredentialProvider.Scope);
        await Assert.That(serviceAccount.UseJwtAccessWithScopes).IsTrue();
    }

    [Test]
    public async Task ConcurrentConnectionsShareGoogleTokenAcquisition()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TokenSource { BeforeReply = cancellationToken => release.Task.WaitAsync(cancellationToken) };
        var provider = CreateProvider(new Clock(), source);

        var pending = Enumerable.Range(0, 16).Select(_ => provider.GetCredentialsAsync().AsTask()).ToArray();
        await UntilAsync(() => source.Calls == 1);
        release.TrySetResult();
        var credentials = await Task.WhenAll(pending).WaitAsync(Limit);

        await Assert.That(source.Calls).IsEqualTo(1);
        await Assert.That(credentials.All(value => value.Password == "token-1")).IsTrue();
    }

    [Test]
    public async Task ForwardsCancellationDuringTokenAcquisition()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new TokenSource
        {
            BeforeReply = async cancellationToken =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
        };
        var provider = CreateProvider(new Clock(), source);
        using var cancellation = new CancellationTokenSource();
        var pending = provider.GetCredentialsAsync(cancellation.Token).AsTask();
        await entered.Task.WaitAsync(Limit);
        cancellation.Cancel();

        await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task PropagatesTokenAcquisitionFailures()
    {
        var source = new TokenSource { BeforeReply = _ => throw new InvalidOperationException("token source failed") };
        var provider = CreateProvider(new Clock(), source);

        await Assert.That(async () => await provider.GetCredentialsAsync()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RejectsNullAndNonRenewableCredentials()
    {
        await Assert.That(() => new RespireMemorystoreCredentialProvider(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => new RespireMemorystoreCredentialProvider(GoogleCredential.FromAccessToken("fixed")))
            .Throws<ArgumentException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new TokenSource();
        var provider = CreateProvider(new Clock(), source);
        await Assert.That(async () => await provider.GetCredentialsAsync(cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(source.Calls).IsEqualTo(0);
    }

    [Test]
    [Arguments("{\"access_token\":\"token\"}")]
    [Arguments("{\"access_token\":\"token\",\"expires_in\":0}")]
    [Arguments("{\"access_token\":\"\",\"expires_in\":3600}")]
    public async Task RejectsTokenWithoutUsableAccessTokenAndLifetime(string json)
    {
        var provider = CreateProvider(new Clock(), new TokenSource { Json = json });

        await Assert.That(async () => await provider.GetCredentialsAsync()).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    public async Task RefreshReauthenticatesExistingConnection(RespireProtocol protocol)
    {
        var clock = new Clock();
        var source = new TokenSource();
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("HELLO ", StringComparison.Ordinal)
                ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray()
                : command == "PING" ? FakeRespServer.PongReply : FakeRespServer.OkReply,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, Protocol = protocol,
            CredentialProvider = CreateProvider(clock, source), CredentialTimeProvider = clock,
            // Distinguish the retry timer from the five-second acquisition deadline.
            CredentialRefreshRetryDelay = TimeSpan.FromSeconds(7),
            ConnectTimeout = Limit,
        });
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromMinutes(55)));
        clock.Advance(TimeSpan.FromMinutes(55));
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromSeconds(7)));
        await Assert.That(source.Calls).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(1);
        clock.Advance(TimeSpan.FromSeconds(250));
        await UntilAsync(() => server.ReceivedCommands.Contains("AUTH token-2"));
        await UntilAsync(() => clock.HasDelay(TimeSpan.FromMinutes(55)));
        await client.PingAsync();

        await Assert.That(source.Calls).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            protocol == RespireProtocol.Resp2 ? "AUTH token-1" : "HELLO 3 AUTH default token-1",
            "AUTH token-2", "PING",
        });
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(1);
    }

    private static RespireMemorystoreCredentialProvider CreateProvider(Clock clock, TokenSource source)
        => new(new ComputeCredential(new ComputeCredential.Initializer
        {
            Clock = new GoogleClock(clock), HttpClientFactory = source,
        }).ToGoogleCredential());

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private sealed class GoogleClock(Clock clock) : IClock
    {
        public DateTime Now => UtcNow.ToLocalTime();
        public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;
    }

    private sealed class TokenSource : HttpClientFactory
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string? LastBody { get; private set; }
        public string? Json { get; init; }
        public Func<CancellationToken, Task>? BeforeReply { get; init; }
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => new Handler(this);

        private sealed class Handler(TokenSource source) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var call = Interlocked.Increment(ref source._calls);
                source.LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                if (source.BeforeReply is { } beforeReply) await beforeReply(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(source.Json ?? $$"""{"access_token":"token-{{call}}","expires_in":3600,"token_type":"Bearer"}""",
                        Encoding.UTF8, "application/json"),
                };
            }
        }
    }
}
