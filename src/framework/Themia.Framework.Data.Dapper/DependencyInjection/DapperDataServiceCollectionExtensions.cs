using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Auditing;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Repositories;
using Themia.Framework.Data.Dapper.Tenancy;
using Themia.Framework.Data.Dapper.UnitOfWork;

namespace Themia.Framework.Data.Dapper.DependencyInjection;

/// <summary>DI registration for the engine-agnostic Dapper data services.</summary>
public static class DapperDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the engine-agnostic Dapper data services. An engine package (e.g. PostgreSql) must also
    /// register an <see cref="Connection.IDapperConnectionFactory"/> and <see cref="Sql.ISqlCompiler"/>.
    /// </summary>
    public static IServiceCollection AddThemiaDapperCore(this IServiceCollection services, Action<DapperDataOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        DapperConfiguration.EnsureConfigured();
        var options = new DapperDataOptions();
        configure?.Invoke(options);

        // Reuse the registry a prior call registered instead of registering a second one. A second
        // instance would win resolution while the mappings modules contributed sat on the first, so every
        // module-mapped table would silently fall back to its convention name.
        var registry = services.FindEntityMappingRegistry();
        if (registry is null)
        {
            registry = new EntityMappingRegistry();
            services.AddSingleton(registry);
        }

        options.ConfigureMappings?.Invoke(new EntityMappingRegistryConfigurator(registry));
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICurrentUserAccessor, NullCurrentUserAccessor>();
        services.TryAddScoped<IDataFilterScope, DataFilterScope>();
        // Default SQLSTATE-based unique-violation detection (PostgreSQL/MySQL). The SQL Server engine
        // package replaces this with a SqlException.Number-based interpreter, since SqlClient does not
        // surface a usable SqlState. TryAdd keeps the engine package's prior replacement, if any.
        services.TryAddSingleton<ISqlExceptionInterpreter, SqlStateUniqueConstraintInterpreter>();
        services.AddScoped<DapperConnectionContext>();
        services.AddScoped<IDapperConnectionContext>(sp => sp.GetRequiredService<DapperConnectionContext>());
        services.AddScoped<ITenantQueryFactory, TenantQueryFactory>();
        services.AddScoped<DapperUnitOfWork>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<DapperUnitOfWork>());
        services.AddScoped<IPendingOperationSink>(sp => sp.GetRequiredService<DapperUnitOfWork>());
        services.AddScoped(typeof(IReadRepository<,>), typeof(DapperReadRepository<,>));
        services.AddScoped(typeof(IRepository<,>), typeof(DapperRepository<,>));
        return services;
    }
}
