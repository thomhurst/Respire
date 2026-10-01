using Azure.Core;
using Respire.Extensions.Azure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Extensions.Azure.Tests;

public class AzureManagedRedisCredentialProviderTests
{
    [Test]
    public async Task RequestsManagedRedisScopeAndReturnsUsernameTokenAndExpiry()
    {
        var expiry = DateTimeOffset.Parse("2030-01-02T03:04:05Z");
        var credential = new RecordingCredential(new AccessToken("access-token", expiry));
        var provider = new AzureManagedRedisCredentialProvider(credential, "redis-user-object-id");

        var credentials = await provider.GetCredentialsAsync();

        await Assert.That(credential.RequestContext!.Value.Scopes[0])
            .IsEqualTo(AzureManagedRedisCredentialProvider.Scope);
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
        var provider = new AzureManagedRedisCredentialProvider(credential, "redis-user-object-id");

        var first = await provider.GetCredentialsAsync();
        var second = await provider.GetCredentialsAsync();

        await Assert.That(credential.Calls).IsEqualTo(2);
        await Assert.That(first.Password).IsEqualTo("first-token");
        await Assert.That(first.ExpiresAt).IsEqualTo(firstExpiry);
        await Assert.That(second.Password).IsEqualTo("second-token");
        await Assert.That(second.ExpiresAt).IsEqualTo(secondExpiry);
    }

    [Test]
    public async Task ForwardsCancellationToTokenCredential()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var credential = new RecordingCredential(new AccessToken("unused", DateTimeOffset.UtcNow.AddMinutes(1)))
        {
            CancelOnRequest = true,
        };
        var provider = new AzureManagedRedisCredentialProvider(credential, "redis-user-object-id");

        var exception = await Assert.That(async () => await provider.GetCredentialsAsync(cancellation.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(credential.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(exception!.CancellationToken).IsEqualTo(cancellation.Token);
    }

    [Test]
    public async Task PropagatesTokenAcquisitionFailures()
    {
        var failure = new InvalidOperationException("credential unavailable");
        var credential = new RecordingCredential(failure);
        var provider = new AzureManagedRedisCredentialProvider(credential, "redis-user-object-id");

        var thrown = await Assert.That(async () => await provider.GetCredentialsAsync())
            .Throws<InvalidOperationException>();

        await Assert.That(thrown).IsSameReferenceAs(failure);
    }

    [Test]
    public async Task RequiresCredentialAndRedisUsername()
    {
        await Assert.That(() => new AzureManagedRedisCredentialProvider(null!, "redis-user-object-id"))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new AzureManagedRedisCredentialProvider(
                new RecordingCredential(new AccessToken("token", DateTimeOffset.UtcNow.AddMinutes(1))), " "))
            .Throws<ArgumentException>();
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
