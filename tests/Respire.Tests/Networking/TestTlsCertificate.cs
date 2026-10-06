using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Respire.Tests.Networking;

internal static class TestTlsCertificate
{
    internal static X509Certificate2 Create(string hostname = "localhost")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={hostname}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName(hostname);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
#if NET10_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pfx, "test-password"),
            "test-password",
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
#else
        return new X509Certificate2(
            ephemeral.Export(X509ContentType.Pfx, "test-password"),
            "test-password",
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
#endif
    }
}
