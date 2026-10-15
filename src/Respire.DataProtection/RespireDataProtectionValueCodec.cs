using System.Buffers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Respire.Compression;

namespace Respire.DataProtection;

/// <summary>Authenticates and encrypts value bytes with ASP.NET Core Data Protection.</summary>
/// <remarks>
/// Opt in through cache ValueCodec settings, RespireValueCodecSerializer, or explicit feature
/// encode/decode delegates. An optional compression codec runs before encryption and after
/// authentication on read. All readers must use the same protector purposes and compression settings.
/// Keys, field names, and metadata are not encrypted. Components must support concurrent calls.
/// </remarks>
public sealed class RespireDataProtectionValueCodec : IRespireValueCodec
{
    private readonly IDataProtector _protector;
    private readonly IRespireValueCodec? _compression;

    /// <summary>Creates an encrypting codec with optional compression before encryption.</summary>
    /// <param name="protector">A protector scoped to the application, feature, and tenant as needed.
    /// Retain old keys to read values after rotation. The codec adds its own versioned purpose.</param>
    /// <param name="compression">Optional compression codec. Use the same codec on all readers and
    /// writers, with an appropriate decoded-size bound. Null leaves plaintext bytes uncompressed.</param>
    public RespireDataProtectionValueCodec(IDataProtector protector, IRespireValueCodec? compression = null)
    {
        ArgumentNullException.ThrowIfNull(protector);
        // Authenticate the format choice so a compressed value cannot be read as raw plaintext.
        _protector = protector.CreateProtector("Respire.DataProtection.ValueCodec.v1")
            .CreateProtector(compression is null ? "raw" : "compressed");
        _compression = compression;
    }

    /// <summary>Returns owned authenticated ciphertext, compressing first when configured.</summary>
    public byte[] Encode(ReadOnlySpan<byte> payload)
    {
        var plaintext = _compression is null ? payload.ToArray() : _compression.Encode(payload);
        try
        {
            return _protector.Protect(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Appends ciphertext to the destination after protection succeeds.</summary>
    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(payload));
    }

    /// <summary>Authenticates and decrypts a value, then decompresses it when configured.</summary>
    /// <exception cref="CryptographicException">The payload is invalid, has been tampered with,
    /// uses a different purpose or compression mode, or its key is unavailable or revoked.
    /// Unprotected legacy values are never accepted.</exception>
    public byte[] Decode(ReadOnlySpan<byte> payload)
    {
        byte[] plaintext;
        try
        {
            plaintext = _protector.Unprotect(payload.ToArray());
        }
        catch (CryptographicException exception)
        {
            throw new CryptographicException(
                "Respire payload authentication failed. Check the key ring, protector purpose, compression mode, and payload integrity.", exception);
        }

        if (_compression is null)
            return plaintext;

        try
        {
            return _compression.Decode(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Appends plaintext only after authentication and decoding succeed.</summary>
    public void Decode(ReadOnlySpan<byte> payload, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var plaintext = Decode(payload);
        try
        {
            destination.Write(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
