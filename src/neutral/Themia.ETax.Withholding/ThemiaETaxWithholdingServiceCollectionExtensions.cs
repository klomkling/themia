using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.ETax.Withholding;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the withholding certificate service.</summary>
public static class ThemiaETaxWithholdingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="WithholdingCertificateService"/> (scoped) with <paramref name="catalogue"/>. The app registers its own
    /// <see cref="IWithholdingCertificateStore"/> and <see cref="IWithholdingNumberSource"/>, typically scoped to the request's connection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="catalogue">The income-type catalogue new certificates are selected from.</param>
    /// <param name="configureValidation">Optional validation options for new input.</param>
    public static IServiceCollection AddThemiaETaxWithholding(
        this IServiceCollection services, IncomeTypeCatalogue catalogue, Action<WithholdingValidationOptions>? configureValidation = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalogue);

        var validation = new WithholdingValidationOptions();
        configureValidation?.Invoke(validation);

        services.TryAddSingleton(catalogue);
        services.TryAddSingleton(validation);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<WithholdingCertificateService>();
        return services;
    }
}
