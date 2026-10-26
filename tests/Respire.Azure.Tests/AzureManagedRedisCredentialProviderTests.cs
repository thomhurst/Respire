using System.Text;
using Azure.Core;
using Respire.Azure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Azure.Tests;

public class AzureManagedRedisCredentialProviderTests
{
    [Test]
    public async Task DerivesUsernameFromTokenAndPreservesTokenAndExpiry()
    {
        var expiry = DateTimeOffset.Parse("2030-01-02T03:04:05Z");
        // Exercise both base64url substitutions and omitted padding.
        var token = CreateToken("{\"oid\":\"redis-user-object-id\",\"extra\":\"\u083e\u083f\"}");
        var credential = new RecordingCredential(new AccessToken(token, expiry));
        var provider = new RespireAzureManagedRedisCredentialProvider(credential);

        var credentials = await provider.GetCredentialsAsync();

        await Assert.That(credential.RequestContext!.Value.Scopes[0])
            .IsEqualTo(RespireAzureManagedRedisCredentialProvider.Scope);
        await Assert.That(credentials.Username).IsEqualTo("redis-user-object-id");
        await Assert.That(credentials.Password).IsEqualTo(token);
        await Assert.That(credentials.ExpiresAt).IsEqualTo(expiry);
    }

    [Test]
    public async Task CachesUsernameAcrossTokenRefreshesWithoutParsingLaterTokens()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var credential = new RecordingCredential(
            new AccessToken(CreateToken("{\"oid\":\"first-user\"}"), expiry),
            new AccessToken(CreateToken("{\"oid\":\"different-user\"}"), expiry),
            new AccessToken("opaque-refreshed-token", expiry));
        var provider = new RespireAzureManagedRedisCredentialProvider(credential);

        var first = await provider.GetCredentialsAsync();
        var second = await provider.GetCredentialsAsync();
        var third = await provider.GetCredentialsAsync();

        await Assert.That(first.Username).IsEqualTo("first-user");
        await Assert.That(second.Username).IsEqualTo("first-user");
        await Assert.That(third.Username).IsEqualTo("first-user");
        await Assert.That(third.Password).IsEqualTo("opaque-refreshed-token");
        await Assert.That(credential.Calls).IsEqualTo(3);
    }

    [Test]
    public async Task ConcurrentFirstRequestsUseTheSameUsername()
    {
        var credential = new PendingCredential();
        var provider = new RespireAzureManagedRedisCredentialProvider(credential);
        var firstRequest = provider.GetCredentialsAsync();
        var secondRequest = provider.GetCredentialsAsync();
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var firstToken = CreateToken("{\"oid\":\"first-user\"}");
        var secondToken = CreateToken("{\"oid\":\"second-user\"}");

        credential.Second.SetResult(new AccessToken(secondToken, expiry));
        var second = await secondRequest;
        credential.First.SetResult(new AccessToken(firstToken, expiry));
        var first = await firstRequest;

        await Assert.That(first.Username).IsEqualTo("second-user");
        await Assert.That(second.Username).IsEqualTo("second-user");
        await Assert.That(first.Password).IsEqualTo(firstToken);
        await Assert.That(second.Password).IsEqualTo(secondToken);
    }

    [Test]
    [Arguments("{}")]
    [Arguments("{\"oid\":null}")]
    [Arguments("{\"oid\":123}")]
    [Arguments("{\"oid\":\"\"}")]
    [Arguments("{\"oid\":\" \"}")]
    [Arguments("[]")]
    [Arguments("invalid-json")]
    public async Task RejectsPayloadWithoutUsableObjectId(string payload)
    {
        await AssertInvalidToken(CreateToken(payload));
    }

    [Test]
    [Arguments("")]
    [Arguments("opaque-token")]
    [Arguments("header.payload")]
    [Arguments("header.%.signature")]
    [Arguments("header.a.signature")]
    [Arguments("header..signature")]
    [Arguments("header.e30.signature.extra")]
    public async Task RejectsMalformedToken(string token)
    {
        await AssertInvalidToken(token);
    }

    [Test]
    public async Task FailedUsernameResolutionCanRetryWithValidToken()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        var credential = new RecordingCredential(
            new AccessToken(CreateToken("{}"), expiry),
            new AccessToken(CreateToken("{\"oid\":\"resolved-user\"}"), expiry));
        var provider = new RespireAzureManagedRedisCredentialProvider(credential);

        await Assert.That(async () => await provider.GetCredentialsAsync()).Throws<InvalidOperationException>();
        var credentials = await provider.GetCredentialsAsync();

        await Assert.That(credentials.Username).IsEqualTo("resolved-user");
    }

    private static async Task AssertInvalidToken(string token)
    {
        var credential = new RecordingCredential(new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)));
        var provider = new RespireAzureManagedRedisCredentialProvider(credential);

        var exception = await Assert.That(async () => await provider.GetCredentialsAsync())
            .Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("oid");
        await Assert.That(exception.Message).Contains("explicit username");
        await Assert.That(exception.InnerException).IsNull();
    }

    private static string CreateToken(string payload)
        => "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

    [Test]
    public async Task RequestsManagedRedisScopeAndReturnsUsernameTokenAndExpiry()
    {
        var expiry = DateTimeOffset.Parse("2030-01-02T03:04:05Z");
        var credential = new RecordingCredential(new AccessToken("access-token", expiry));
        var provider = new RespireAzureManagedRedisCredentialProvider(credential, "redis-user-object-id");

        var credentials = await provider.GetCredentialsAsync();

        await Assert.That(credential.RequestContext!.Value.Scopes[0])
            .IsEqualTo(RespireAzureManagedRedisCredentialProvider.Scope);
        await Assert.That(credentials.Username).IsEqualTo("redis-user-object-id");
        await Assert.That(credentials.Password).IsEqualTo("access-token");
        await Assert.That(credentials.ExpiresAt).IsEqualTo(expiry);
    }

    [Test]
    public async Task RequestsANewTokenForEveryCredentialRefresh()
    {
        var firstExpiry = DateTimeOffset.Parse("2030-01-02T03:04:05Z");
        var secondExpiry = DateTimeOffset.Parse("2030-01-02T04:04:05Z");
        var credential = new RecordingCredential(
            new AccessToken("first-token", firstExpiry),
            new AccessToken("second-token", secondExpiry));
        var provider = new RespireAzureManagedRedisCredentialProvider(credential, "redis-user-object-id");

        var first = await provider.GetCredentialsAsync();
        var second = await provider.GetCredentialsAsync();

        await Assert.That(credential.Calls).IsEqualTo(2);
        await Assert.That(first.Password).IsEqualTo("first-token");
        await Assert.That(first.ExpiresAt).IsEqualTo(firstExpiry);
        await Assert.That(second.Password).IsEqualTo("second-token");
        await Assert.That(second.ExpiresAt).IsEqualTo(secondExpiry);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ForwardsCancellationToTokenCredential(bool deriveUsername)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var credential = new RecordingCredential(new AccessToken("unused", DateTimeOffset.UtcNow.AddMinutes(1)))
        {
            CancelOnRequest = true,
        };
        var provider = deriveUsername
            ? new RespireAzureManagedRedisCredentialProvider(credential)
            : new RespireAzureManagedRedisCredentialProvider(credential, "redis-user-object-id");

        var exception = await Assert.That(async () => await provider.GetCredentialsAsync(cancellation.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(credential.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(exception!.CancellationToken).IsEqualTo(cancellation.Token);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PropagatesTokenAcquisitionFailures(bool deriveUsername)
    {
        var failure = new InvalidOperationException("credential unavailable");
        var credential = new RecordingCredential(failure);
        var provider = deriveUsername
            ? new RespireAzureManagedRedisCredentialProvider(credential)
            : new RespireAzureManagedRedisCredentialProvider(credential, "redis-user-object-id");

        var thrown = await Assert.That(async () => await provider.GetCredentialsAsync())
            .Throws<InvalidOperationException>();

        await Assert.That(thrown).IsSameReferenceAs(failure);
    }

    [Test]
    public async Task RequiresCredentialAndRedisUsername()
    {
        await Assert.That(() => new RespireAzureManagedRedisCredentialProvider(null!))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new RespireAzureManagedRedisCredentialProvider(null!, "redis-user-object-id"))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new RespireAzureManagedRedisCredentialProvider(
                new RecordingCredential(new AccessToken("token", DateTimeOffset.UtcNow.AddMinutes(1))), " "))
            .Throws<ArgumentException>();
    }

    private sealed class PendingCredential : TokenCredential
    {
        private int _calls;
        public TaskCompletionSource<AccessToken> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AccessToken> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(Interlocked.Increment(ref _calls) == 1 ? First.Task : Second.Task);
    }

    private sealed class RecordingCredential : TokenCredential
    {
        private readonly Queue<AccessToken> _tokens = new();
        private readonly Exception? _failure;

        public RecordingCredential(params AccessToken[] tokens)
        {
            foreach (var token in tokens)
            {
                _tokens.Enqueue(token);
            }
        }

        public RecordingCredential(Exception failure) => _failure = failure;

        public int Calls { get; private set; }
        public TokenRequestContext? RequestContext { get; private set; }
        public CancellationToken CancellationToken { get; private set; }
        public bool CancelOnRequest { get; init; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new NotSupportedException("The adapter must use asynchronous token acquisition.");

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            RequestContext = requestContext;
            CancellationToken = cancellationToken;

            if (CancelOnRequest)
            {
                return ValueTask.FromCanceled<AccessToken>(cancellationToken);
            }

            if (_failure is not null)
            {
                return ValueTask.FromException<AccessToken>(_failure);
            }

            return ValueTask.FromResult(_tokens.Dequeue());
        }
    }
}
