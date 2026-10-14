using Microsoft.Extensions.Options;

namespace Respire.DependencyInjection;

internal sealed record RespireOptionsRegistration(
    string Name, Func<IServiceProvider, RespireOptions> Create);

// OptionsFactory's configure/post-configure/validate pipeline remains available even though
// the initial value is an immutable record produced by our configuration mapper.
internal sealed class RespireOptionsFactory(
    IEnumerable<IConfigureOptions<RespireOptions>> setups,
    IEnumerable<IPostConfigureOptions<RespireOptions>> postConfigures,
    IEnumerable<IValidateOptions<RespireOptions>> validations,
    IEnumerable<RespireOptionsRegistration> registrations,
    IServiceProvider provider) : OptionsFactory<RespireOptions>(setups, postConfigures, validations)
{
    protected override RespireOptions CreateInstance(string name)
    {
        var registration = registrations.FirstOrDefault(item => item.Name == name);
        return registration is null ? new RespireOptions() : registration.Create(provider);
    }
}

// Reuse the client's cross-property and nested-policy validation. Duplicating these rules in
// data annotations would let the generated validator drift from actual client requirements.
internal sealed class RespireOptionsValidator : IValidateOptions<RespireOptions>
{
    public ValidateOptionsResult Validate(string? name, RespireOptions options)
    {
        try
        {
            options.ValidateAndSnapshot();
            // Configuration-bound endpoint objects need a host check at startup; core validation
            // otherwise leaves empty TCP hosts to connection setup.
            if (options.Endpoints.Concat(options.ReplicaEndpoints).Any(endpoint => string.IsNullOrWhiteSpace(endpoint.Host)))
                return ValidateOptionsResult.Fail("Respire endpoint Host must not be empty.");
            return ValidateOptionsResult.Success;
        }
        catch (Exception exception) when (exception is RespireConfigurationException or ArgumentException)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}
