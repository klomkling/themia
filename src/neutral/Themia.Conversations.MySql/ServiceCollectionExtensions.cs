using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Conversations.Migrations;
using Themia.Data.Migrations;
using Themia.Data.Migrations.MySql;

namespace Themia.Conversations.MySql;

/// <summary>DI entry point for the MySQL 8.0.13+-backed <c>Themia.Conversations</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="MySqlConversationDialect"/> as the <see cref="IConversationDialect"/> and runs
    /// <see cref="ConversationSchemaMigration"/> immediately.</summary>
    /// <remarks>Call <c>AddThemiaConversations</c> as well, in either order.</remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">MySQL 8.0.13+ connection string.</param>
    public static IServiceCollection AddThemiaConversationsMySql(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<IConversationDialect>(new MySqlConversationDialect(connectionString));

        // Register on use (coord #0126).
        MigrationEngineRegistry.Add(MySqlMigrationEngine.Adapter);

        ThemiaMigrations.Run(MySqlMigrationEngine.Adapter, connectionString, typeof(ConversationSchemaMigration).Assembly);

        return services;
    }
}
