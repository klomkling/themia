using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.ETax;
using Themia.ETax.Data;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>DI registration for Themia.ETax.Data.</summary>
public static class ETaxDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SequenceTaxDocumentNumberSource"/> as the <see cref="ITaxDocumentNumberSource"/>. Scoped, because the allocator it wraps
    /// reads the ambient tenant. Register <c>AddThemiaSequences</c> separately.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional: prefixes, period, padding, scope.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddThemiaETaxSequenceNumbers(
        this IServiceCollection services, Action<TaxDocumentNumberOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new TaxDocumentNumberOptions();
        configure?.Invoke(options);
        options.Validate();    // fail at registration, not at the first invoice
        services.TryAddSingleton(options);
        services.TryAddScoped<ITaxDocumentNumberSource, SequenceTaxDocumentNumberSource>();
        return services;
    }
}
