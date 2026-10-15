using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.ETax.Withholding;
using Themia.ETax.Withholding.Data;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the framework-side parts of Themia.ETax.Withholding.</summary>
public static class ETaxWithholdingDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SequenceWithholdingNumberSource"/> as the (scoped) <see cref="IWithholdingNumberSource"/>. Needs Themia's
    /// transactional sequence allocator (<c>AddThemiaSequences</c>). The options are validated here, not at the first certificate.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional number format configuration.</param>
    public static IServiceCollection AddThemiaETaxWithholdingSequenceNumbers(
        this IServiceCollection services, Action<WithholdingNumberOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new WithholdingNumberOptions();
        configure?.Invoke(options);
        options.Validate();
        services.TryAddSingleton(options);
        services.TryAddScoped<IWithholdingNumberSource, SequenceWithholdingNumberSource>();
        return services;
    }
}
