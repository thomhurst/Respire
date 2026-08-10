using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ExceptionErgonomicsTests
{
    [Test]
    [Arguments(RespireErrorCodes.Loading)]
    [Arguments(RespireErrorCodes.Busy)]
    [Arguments(RespireErrorCodes.ClusterDown)]
    [Arguments(RespireErrorCodes.TryAgain)]
    [Arguments(RespireErrorCodes.MasterDown)]
    public async Task TransientServerErrors_AreClassified(string code)
    {
        var error = new RespireServerException($"{code} retry later");

        await Assert.That(error.Code).IsEqualTo(code);
        await Assert.That(error.IsTransient).IsTrue();
    }

    [Test]
    public async Task PermanentServerErrors_AreNotClassifiedAsTransient()
    {
        var error = new RespireServerException($"{RespireErrorCodes.WrongType} bad value");

        await Assert.That(error.IsTransient).IsFalse();
    }

    [Test]
    public async Task ServerException_PreservesCommandName()
    {
        var error = new RespireServerException("ERR failed", "GET");

        await Assert.That(error.CommandName).IsEqualTo("GET");
    }

    [Test]
    public async Task ProtocolException_PreservesInnerException()
    {
        var inner = new IOException("transport failed");
        var error = new RespireProtocolException("bad response", inner);

        await Assert.That(error.InnerException).IsSameReferenceAs(inner);
    }

    [Test]
    public async Task TimeoutException_PreservesInnerExceptionAndContext()
    {
        var inner = new OperationCanceledException("timer elapsed");
        var timeout = TimeSpan.FromSeconds(2);
        var error = new RespireTimeoutException("SET", timeout, inner);

        await Assert.That(error.InnerException).IsSameReferenceAs(inner);
        await Assert.That(error.CommandName).IsEqualTo("SET");
        await Assert.That(error.Timeout).IsEqualTo(timeout);
    }

    [Test]
    public async Task Create_WithSentinelOptions_ThrowsConfigurationException()
    {
        var options = new RespireOptions { ServiceName = "primary" };

        await Assert.That(() => RespireClient.Create(options))
            .ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    public async Task Create_WithClusterDatabase_ThrowsConfigurationException()
    {
        var options = new RespireOptions { Cluster = true, Database = 1 };

        await Assert.That(() => RespireClient.Create(options))
            .ThrowsExactly<RespireConfigurationException>();
    }
}
