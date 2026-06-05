using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Themia.Exceptional;

/// <summary>Shared registration used by provider packages (e.g. Themia.Exceptional.PostgreSql).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the exception store over a provider-supplied <see cref="IExceptionalSqlDialect"/> plus validated options.
    /// Provider packages call this after registering their dialect.
    /// </summary>
    public static IServiceCollection AddThemiaExceptionalCore(this IServiceCollection services, Action<ExceptionalOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ExceptionalOptions();
        configure(options);
        options.Validate();
        services.TryAddSingleton(options);

        services.TryAddSingleton<IExceptionStore>(sp =>
            new ExceptionStoreEngine(sp.GetRequiredService<IExceptionalSqlDialect>(), options));

        return services;
    }
}
