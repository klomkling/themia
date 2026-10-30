using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Conversations.Migrations;
using Themia.Data.Migrations;
using Themia.Data.Migrations.PostgreSql;
using Npgsql;
using Themia.Data.Probes;

namespace Themia.Conversations.PostgreSql;

/// <summary>DI entry point for the PostgreSQL-backed <c>Themia.Conversations</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="PostgresConversationDialect"/> as the <see cref="IConversationDialect"/> and runs
    /// <see cref="ConversationSchemaMigration"/> immediately.</summary>
    /// <remarks>Call <c>AddThemiaConversations</c> as well, in either order.</remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    public static IServiceCollection AddThemiaConversationsPostgres(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<IConversationDialect>(new PostgresConversationDialect(connectionString));

        // Register on use (coord #0126).
        MigrationEngineRegistry.Add(PostgresMigrationEngine.Adapter);

        ThemiaMigrations.Run(PostgresMigrationEngine.Adapter, connectionString, typeof(ConversationSchemaMigration).Assembly);

        services.AddPostgresSchemaProbe(
            "Themia.Conversations",
            _ =>
            {
                var connection = new NpgsqlConnection(connectionString);
                connection.Open();
                return connection;
            },
            ["conversations", "conversation_participants", "conversation_messages", "conversation_attachments", "conversation_guest_sessions", "conversation_events", "conversation_tombstones"]);

        return services;
    }
}
