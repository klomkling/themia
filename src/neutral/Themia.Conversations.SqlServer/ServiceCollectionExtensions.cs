using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Conversations.Migrations;
using Themia.Data.Migrations;
using Themia.Data.Migrations.SqlServer;

namespace Themia.Conversations.SqlServer;

/// <summary>DI entry point for the SQL Server-backed <c>Themia.Conversations</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="SqlServerConversationDialect"/> as the <see cref="IConversationDialect"/> and runs
    /// <see cref="ConversationSchemaMigration"/> immediately.</summary>
    /// <remarks>Call <c>AddThemiaConversations</c> as well, in either order.</remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">SQL Server connection string.</param>
    public static IServiceCollection AddThemiaConversationsSqlServer(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<IConversationDialect>(new SqlServerConversationDialect(connectionString));

        // Register on use (coord #0126).
        MigrationEngineRegistry.Add(SqlServerMigrationEngine.Adapter);

        ThemiaMigrations.Run(SqlServerMigrationEngine.Adapter, connectionString, typeof(ConversationSchemaMigration).Assembly);

        return services;
    }
}
