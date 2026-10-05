using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace Respire.DataProtection;

/// <summary>Configures Respire storage for ASP.NET Core DataProtection keys.</summary>
public static class RespireDataProtectionBuilderExtensions
{
    /// <summary>Persists keys using a client resolved from the application's service provider.</summary>
    /// <param name="builder">The DataProtection builder.</param>
    /// <param name="clientFactory">Resolves a caller-owned client from the root provider for each repository operation.</param>
    /// <param name="key">The Redis list key shared by application instances. Its bytes are copied during registration.</param>
    /// <returns>The builder, for further configuration.</returns>
    /// <remarks>The factory must be cheap and thread-safe. Resolve a singleton client rather than a scoped service.</remarks>
    public static IDataProtectionBuilder PersistKeysToRespire(
        this IDataProtectionBuilder builder, Func<IServiceProvider, IRespireClient> clientFactory, RespireKey key)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(clientFactory);
        var ownedKey = key.Snapshot();
        builder.Services.AddOptions<KeyManagementOptions>().Configure<IServiceProvider>((options, services) =>
            options.XmlRepository = new RespireXmlRepository(() => clientFactory(services), ownedKey));
        return builder;
    }

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
        var repository = new RespireXmlRepository(clientFactory, key);
        builder.Services.Configure<KeyManagementOptions>(options => options.XmlRepository = repository);
        return builder;
    }
}
