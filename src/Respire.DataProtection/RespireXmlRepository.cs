using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Respire.DataProtection;

/// <summary>Stores DataProtection XML elements in a Redis list compatible with Microsoft's Redis repository.</summary>
/// <remarks>
/// The factory returns an existing client owned by the caller. This repository never disposes it.
/// The synchronous DataProtection contract waits for Respire operations to complete.
/// Stored XML is not encrypted by this repository; configure DataProtection key encryption separately.
/// </remarks>
public sealed class RespireXmlRepository : IXmlRepository
{
    private readonly Func<IRespireClient> _clientFactory;
    private readonly RespireKey _key;

    /// <summary>Creates a repository using the supplied client factory and Redis list key.</summary>
    public RespireXmlRepository(Func<IRespireClient> clientFactory, RespireKey key)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        _clientFactory = clientFactory;
        _key = key.Snapshot();
    }

    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var values = GetClient().WithReadFrom(RespireReadFrom.Primary)
            .Lists.RangeAsync(_key).AsTask().GetAwaiter().GetResult();
        var elements = new XElement[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            // A malformed entry could contain revocation data. Fail rather than return a partial ring.
            elements[i] = XElement.Parse(values[i]);
        }

        return Array.AsReadOnly(elements);
    }

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        // friendlyName is metadata for file repositories; Redis stores only the XML, without expiry.
        GetClient().Lists.RightPushAsync(_key, element.ToString(SaveOptions.DisableFormatting))
            .AsTask().GetAwaiter().GetResult();
    }

    private IRespireClient GetClient()
        => _clientFactory() ?? throw new InvalidOperationException("The DataProtection client factory returned null.");
}
