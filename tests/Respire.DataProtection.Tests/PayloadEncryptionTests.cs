using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Respire.Compression;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.DataProtection.Tests;

public class PayloadEncryptionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RoundTripsOwnedBytesAndAppendsToDestinations(bool compress)
    {
        var codec = CreateCodec(compress);
        byte[][] values = [[], [0, 255, 128, 1], Encoding.UTF8.GetBytes(new string('x', 4096))];
        foreach (var value in values)
        {
            var encrypted = codec.Encode(value);
            await Assert.That(encrypted.AsSpan().SequenceEqual(value)).IsFalse();
            await Assert.That(codec.Decode(encrypted)).IsEquivalentTo(value);
            var destination = new ArrayBufferWriter<byte>();
            destination.Write(new byte[] { 42 });
            codec.Encode(value, destination);
            await Assert.That(codec.Decode(destination.WrittenSpan[1..])).IsEquivalentTo(value);
            destination.Clear();
            destination.Write(new byte[] { 42 });
            codec.Decode(encrypted, destination);
            await Assert.That(destination.WrittenSpan.SequenceEqual(new byte[] { 42 }.Concat(value).ToArray())).IsTrue();
        }
        byte[] original = [1, 2, 3];
        var owned = codec.Encode(original);
        original[0] = 99;
        await Assert.That(codec.Decode(owned)).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    [Arguments("tampered")]
    [Arguments("truncated")]
    [Arguments("plaintext")]
    [Arguments("empty")]
    public async Task RejectsInvalidPayloadWithoutReturningData(string corruption)
    {
        var compression = new ObservingCodec();
        var codec = new RespireDataProtectionValueCodec(new EphemeralDataProtectionProvider().CreateProtector("tests"), compression);
        var encrypted = codec.Encode("secret"u8);
        switch (corruption)
        {
            case "tampered": encrypted[^1] ^= 1; break;
            case "truncated": encrypted = encrypted[..^1]; break;
            case "plaintext": encrypted = "secret"u8.ToArray(); break;
            case "empty": encrypted = []; break;
        }
        var destination = new ArrayBufferWriter<byte>();
        destination.Write(new byte[] { 42 });
        await Assert.That(() => codec.Decode(encrypted, destination)).Throws<CryptographicException>();
        await Assert.That(destination.WrittenSpan.SequenceEqual(new byte[] { 42 })).IsTrue();
        await Assert.That(compression.DecodeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task CompressesBeforeEncryptionAndAuthenticatesModeAndPurpose()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = provider.CreateProtector("cache");
        var compression = new RespireBrotliValueCodec(new() { MinimumLength = 1 });
        var codec = new RespireDataProtectionValueCodec(protector, compression);
        var payload = Encoding.UTF8.GetBytes(new string('x', 4096));
        var encrypted = codec.Encode(payload);
        await Assert.That(encrypted.Length).IsLessThan(payload.Length);
        await Assert.That(codec.Decode(encrypted)).IsEquivalentTo(payload);
        await Assert.That(() => new RespireDataProtectionValueCodec(protector).Decode(encrypted)).Throws<CryptographicException>();
        await Assert.That(() => new RespireDataProtectionValueCodec(provider.CreateProtector("jobs"), compression)
            .Decode(encrypted)).Throws<CryptographicException>();
        await Assert.That(() => new RespireDataProtectionValueCodec(new EphemeralDataProtectionProvider().CreateProtector("cache"), compression)
            .Decode(encrypted)).Throws<CryptographicException>();
    }

    [Test]
    public async Task RetainedKeyReadsOldPayloadAfterRotationAndProviderRestart()
    {
        var repository = new MemoryKeyRepository();
        byte[] oldPayload;
        Guid oldKeyId;
        Guid newKeyId;
        using (var first = CreateProvider(repository))
        {
            var keys = first.GetRequiredService<IKeyManager>();
            var now = DateTimeOffset.UtcNow;
            oldKeyId = keys.CreateNewKey(now.AddDays(-2), now.AddDays(30)).KeyId;
            var codec = new RespireDataProtectionValueCodec(first.GetRequiredService<IDataProtectionProvider>().CreateProtector("rotation"));
            oldPayload = codec.Encode("before"u8);
            // Data Protection's format stores its key ID after the four-byte magic header.
            await Assert.That(new Guid(oldPayload.AsSpan(4, 16))).IsEqualTo(oldKeyId);
            newKeyId = keys.CreateNewKey(now.AddDays(-1), now.AddDays(60)).KeyId;
        }
        using var second = CreateProvider(repository);
        var rotated = new RespireDataProtectionValueCodec(second.GetRequiredService<IDataProtectionProvider>().CreateProtector("rotation"));
        var newPayload = rotated.Encode("after"u8);
        await Assert.That(new Guid(newPayload.AsSpan(4, 16))).IsEqualTo(newKeyId);
        await Assert.That(Encoding.UTF8.GetString(rotated.Decode(oldPayload))).IsEqualTo("before");
        await Assert.That(Encoding.UTF8.GetString(rotated.Decode(newPayload))).IsEqualTo("after");
        second.GetRequiredService<IKeyManager>().RevokeKey(oldKeyId, "test revocation");
        // A fresh provider sees persisted revocation without racing the asynchronous key-ring refresh.
        using var third = CreateProvider(repository);
        var revoked = new RespireDataProtectionValueCodec(third.GetRequiredService<IDataProtectionProvider>().CreateProtector("rotation"));
        await Assert.That(() => revoked.Decode(oldPayload)).Throws<CryptographicException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SerializerSupportsGenericAndRuntimeTypeMethods(bool runtimeType)
    {
        var serializer = new RespireValueCodecSerializer(RespireSerializer.Default, CreateCodec(true));
        var destination = new ArrayBufferWriter<byte>();
        if (runtimeType) serializer.Serialize(destination, typeof(string), "secret");
        else serializer.Serialize(destination, "secret");
        var decoded = runtimeType ? serializer.Deserialize(typeof(string), destination.WrittenSpan)
            : serializer.Deserialize<string>(destination.WrittenSpan);
        await Assert.That(decoded).IsEqualTo("secret");
        var corrupted = destination.WrittenSpan.ToArray();
        corrupted[^1] ^= 1;
        await Assert.That(() => runtimeType ? serializer.Deserialize(typeof(string), corrupted)
            : serializer.Deserialize<string>(corrupted)).Throws<CryptographicException>();
    }

    [Test]
    public async Task CompressionFailureDoesNotPublishPartialPlaintext()
    {
        var protector = new EphemeralDataProtectionProvider().CreateProtector("bound");
        var writer = new RespireDataProtectionValueCodec(protector, new RespireBrotliValueCodec());
        var reader = new RespireDataProtectionValueCodec(protector,
            new RespireBrotliValueCodec(new() { MaximumDecodedLength = 2 }));
        var encrypted = writer.Encode("too long"u8);
        var destination = new ArrayBufferWriter<byte>();
        destination.Write(new byte[] { 42 });
        await Assert.That(() => reader.Decode(encrypted, destination)).Throws<InvalidDataException>();
        await Assert.That(destination.WrittenSpan.SequenceEqual(new byte[] { 42 })).IsTrue();
    }

    [Test]
    public async Task RejectsNullArguments()
    {
        await Assert.That(() => new RespireDataProtectionValueCodec(null!)).Throws<ArgumentNullException>();
        var codec = CreateCodec(false);
        await Assert.That(() => codec.Encode([], null!)).Throws<ArgumentNullException>();
        await Assert.That(() => codec.Decode([], null!)).Throws<ArgumentNullException>();
    }

    private static RespireDataProtectionValueCodec CreateCodec(bool compress) => new(
        new EphemeralDataProtectionProvider().CreateProtector("tests"), compress ? new RespireBrotliValueCodec() : null);

    private static ServiceProvider CreateProvider(MemoryKeyRepository repository)
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("rotation-test")
            .AddKeyManagementOptions(options => { options.XmlRepository = repository; options.AutoGenerateKeys = false; });
        return services.BuildServiceProvider();
    }

    private sealed class MemoryKeyRepository : IXmlRepository
    {
        private readonly List<XElement> _elements = [];
        public IReadOnlyCollection<XElement> GetAllElements()
        {
            lock (_elements) return _elements.Select(element => new XElement(element)).ToArray();
        }
        public void StoreElement(XElement element, string friendlyName)
        {
            lock (_elements) _elements.Add(new XElement(element));
        }
    }

    private sealed class ObservingCodec : IRespireValueCodec
    {
        public int DecodeCalls { get; private set; }
        public byte[] Encode(ReadOnlySpan<byte> payload) => payload.ToArray();
        public byte[] Decode(ReadOnlySpan<byte> payload) { DecodeCalls++; return payload.ToArray(); }
    }
}
