using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ExceptionEndpointTests
{
    private static readonly RespireEndpoint Endpoint = new("redis.example", 6380);

    [Test]
    public async Task PublicExceptions_CanCarryEndpointContext()
    {
        RespireException[] errors =
        [
            new RespireException("failure") { Endpoint = Endpoint },
            new RespireConnectionException("failure") { Endpoint = Endpoint },
            new RespireAuthenticationException("failure") { Endpoint = Endpoint },
            new RespireReconnectLimitException("failure") { Endpoint = Endpoint },
            new RespireProtocolException("failure") { Endpoint = Endpoint },
            new RespireConfigurationException("failure") { Endpoint = Endpoint },
            new RespireServerException("ERR failure", "GET") { Endpoint = Endpoint },
            new RespireTimeoutException("GET", TimeSpan.FromSeconds(1)) { Endpoint = Endpoint },
            new RespireLockNotAcquiredException { Endpoint = Endpoint },
            new RespireBatchDiscardedException { Endpoint = Endpoint },
            new RespireTransactionDiscardedException { Endpoint = Endpoint },
            new RespirePendingNotReadyException { Endpoint = Endpoint },
            new RespireTransactionAbortedException { Endpoint = Endpoint },
            new RespireCircuitOpenException(Endpoint, null),
            new RespireTransactionConflictException(3) { Endpoint = Endpoint },
            new RespireTransactionRetryException(new RespireServerException("READONLY replica")) { Endpoint = Endpoint },
            new RespireScriptingEngineUnavailableException("lua", Endpoint, new RespireServerException("ERR missing engine")),
        ];

        foreach (var error in errors)
            await Assert.That(error.Endpoint).IsEqualTo((RespireEndpoint?)Endpoint);
    }

    [Test]
    public async Task UnknownEndpoint_RemainsNull()
    {
        RespireException[] errors =
        [
            new RespireException("failure"),
            new RespireConnectionException("failure"),
            new RespireAuthenticationException("failure"),
            new RespireReconnectLimitException("failure"),
            new RespireProtocolException("failure"),
            new RespireConfigurationException("failure"),
            new RespireServerException("ERR failure"),
            new RespireTimeoutException("GET", TimeSpan.FromSeconds(1)),
            new RespireLockNotAcquiredException(),
            new RespireBatchDiscardedException(),
            new RespireTransactionDiscardedException(),
            new RespirePendingNotReadyException(),
            new RespireTransactionAbortedException(),
            new RespireTransactionConflictException(3),
            new RespireTransactionRetryException(new RespireServerException("READONLY replica")),
        ];

        foreach (var error in errors)
            await Assert.That(error.Endpoint).IsNull();
    }

    [Test]
    [Arguments("redis.example", 6380)]
    [Arguments("::1", 6381)]
    [Arguments("/tmp/redis.sock", 0)]
    public async Task ExistingEndpointProperties_AgreeWithBaseProperty(string host, int port)
    {
        var endpoint = new RespireEndpoint(host, port);
        var circuit = new RespireCircuitOpenException(endpoint, TimeSpan.FromSeconds(1));
        var server = new RespireServerException("ERR engine unavailable", "EVAL");
        var scripting = new RespireScriptingEngineUnavailableException("lua", endpoint, server);

        // Keep the existing non-nullable property signatures usable by source callers.
        RespireEndpoint circuitEndpoint = circuit.Endpoint;
        RespireEndpoint scriptingEndpoint = scripting.Endpoint;
        await Assert.That(circuitEndpoint).IsEqualTo(endpoint);
        await Assert.That(scriptingEndpoint).IsEqualTo(endpoint);
        await Assert.That(((RespireException)circuit).Endpoint).IsEqualTo((RespireEndpoint?)endpoint);
        await Assert.That(((RespireException)scripting).Endpoint).IsEqualTo((RespireEndpoint?)endpoint);
        await Assert.That(scripting.InnerException).IsSameReferenceAs(server);
        await Assert.That(scripting.ServerError).IsSameReferenceAs(server);
    }

    [Test]
    public async Task RetiredConnection_ExposesItsEndpointThroughBase()
    {
        var error = new Respire.Networking.RespireConnectionRetiredException(Endpoint.Host, Endpoint.Port);

        await Assert.That(error.Endpoint).IsEqualTo(Endpoint);
        await Assert.That(((RespireException)error).Endpoint).IsEqualTo((RespireEndpoint?)Endpoint);
    }

    [Test]
    public async Task GeneralWrappers_DoNotAttributeMultipleCandidatesToLastCause()
    {
        var last = new RespireConnectionException("last candidate failed") { Endpoint = Endpoint };
        var aggregate = new AggregateException(new RespireConnectionException("first failed"), last);
        RespireException[] wrappers =
        [
            new RespireException("all failed", last),
            new RespireConnectionException("all failed", last),
            new RespireConnectionException("all failed", aggregate),
        ];

        foreach (var wrapper in wrappers)
            await Assert.That(wrapper.Endpoint).IsNull();
        await Assert.That(wrappers[1].InnerException).IsSameReferenceAs(last);
        await Assert.That(last.Endpoint).IsEqualTo((RespireEndpoint?)Endpoint);
    }

    [Test]
    public async Task TransactionRetry_PreservesRejectingServerAndCause()
    {
        var rejection = new RespireServerException("MOVED 1 destination:6379", "EXEC") { Endpoint = Endpoint };
        var error = new RespireTransactionRetryException(rejection);

        await Assert.That(error.Endpoint).IsEqualTo((RespireEndpoint?)Endpoint);
        await Assert.That(error.InnerException).IsSameReferenceAs(rejection);
        await Assert.That(error.ServerError).IsSameReferenceAs(rejection);
    }

    [Test]
    public async Task Timeout_PrefersItsSnapshotAndPreservesItThroughWrapping()
    {
        var cause = new RespireConnectionException("previous attempt") { Endpoint = new("previous", 6379) };
        var snapshot = RespireTimeoutDiagnostics.Capture(RespireCommandStage.AwaitingReply, Endpoint);
        var timeout = new RespireTimeoutException("GET", TimeSpan.FromSeconds(1), cause, snapshot);
        var wrapper = new RespireTimeoutException("MGET", TimeSpan.FromSeconds(2), timeout);

        await Assert.That(timeout.Endpoint).IsEqualTo((RespireEndpoint?)Endpoint);
        await Assert.That(wrapper.Endpoint).IsEqualTo((RespireEndpoint?)Endpoint);
        await Assert.That(wrapper.Diagnostics).IsSameReferenceAs(snapshot);
        await Assert.That(wrapper.InnerException).IsSameReferenceAs(timeout);
        await Assert.That(timeout.InnerException).IsSameReferenceAs(cause);
    }

    [Test]
    public async Task TimeoutWithoutSnapshot_PreservesKnownCauseEndpoint()
    {
        var cause = new RespireServerException("ERR missing function", "FCALL_RO") { Endpoint = Endpoint };
        var timeout = new RespireTimeoutException("FCALL_RO", TimeSpan.FromSeconds(1), cause);
        var propagation = RespireTimeoutException.FunctionPropagation("FCALL_RO", TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5), cause);

        await Assert.That(timeout.Endpoint).IsEqualTo((RespireEndpoint?)Endpoint);
        await Assert.That(propagation.Endpoint).IsEqualTo((RespireEndpoint?)Endpoint);
        await Assert.That(propagation.InnerException).IsSameReferenceAs(cause);
        await Assert.That(propagation.Diagnostics.Endpoint).IsNull();
    }

    [Test]
    public async Task TimeoutWithoutSingleCauseEndpoint_RemainsUnknown()
    {
        var candidate = new RespireConnectionException("candidate failed") { Endpoint = Endpoint };
        var aggregate = new AggregateException(candidate);
        var timeout = new RespireTimeoutException("CONNECT", TimeSpan.FromSeconds(1), aggregate);
        var propagation = RespireTimeoutException.FunctionPropagation("FCALL_RO", TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5), new IOException("no endpoint"));

        await Assert.That(timeout.Endpoint).IsNull();
        await Assert.That(timeout.InnerException).IsSameReferenceAs(aggregate);
        await Assert.That(propagation.Endpoint).IsNull();
    }
}
