using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.EFCore.Repositories;
using Themia.Framework.Data.EFCore.UnitOfWork;

namespace Themia.Framework.Data.EFCore.Extensions;

/// <summary>Registers the shared repository/unit-of-work contracts backed by the EF Core ThemiaDbContext.</summary>
public static class RepositoryServiceCollectionExtensions
{
    /// <summary>Registers the shared <see cref="IReadRepository{T,TKey}"/>/<see cref="IRepository{T,TKey}"/>/<see cref="IUnitOfWork"/> over <typeparamref name="TContext"/>.</summary>
    public static IServiceCollection AddThemiaDataRepositories<TContext>(this IServiceCollection services)
        where TContext : ThemiaDbContext
    {
        services.TryAddScoped<IDataFilterScope, DataFilterScope>();
        services.AddScoped<ThemiaDbContext>(sp => sp.GetRequiredService<TContext>());
        services.AddScoped(typeof(IReadRepository<,>), typeof(EfReadRepository<,>));
        services.AddScoped(typeof(IRepository<,>), typeof(EfRepository<,>));
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        return services;
    }
}
