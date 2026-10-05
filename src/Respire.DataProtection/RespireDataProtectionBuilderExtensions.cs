using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace Respire.DataProtection;

/// <summary>Configures Respire storage for ASP.NET Core DataProtection keys.</summary>
public static class RespireDataProtectionBuilderExtensions
{
    /// <summary>Persists DataProtection keys in the specified Redis list using an existing client.</summary>
    /// <param name="builder">The DataProtection builder.</param>
    /// <param name="clientFactory">Returns a caller-owned client. The repository does not dispose it.</param>
    /// <param name="key">The Redis list key shared by application instances.</param>
    /// <returns>The builder, for further configuration.</returns>
    public static IDataProtectionBuilder PersistKeysToRespire(
        this IDataProtectionBuilder builder, Func<IRespireClient> clientFactory, RespireKey key)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(clientFactory);
        builder.Services.Configure<KeyManagementOptions>(options =>
            options.XmlRepository = new RespireXmlRepository(clientFactory, key));
        return builder;
    }
}
