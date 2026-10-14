using Microsoft.Extensions.Options;

namespace Respire.DependencyInjection;

internal sealed record RespireOptionsRegistration(
    string Name, Func<IServiceProvider, RespireOptions> Create, string ConfigurationPath);

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
        if (registration is null) return new RespireOptions();
        try
        {
            return registration.Create(provider);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new OptionsValidationException(name, typeof(RespireOptions),
                [$"Invalid Respire configuration in '{registration.ConfigurationPath}': {exception.Message}"]);
        }
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
