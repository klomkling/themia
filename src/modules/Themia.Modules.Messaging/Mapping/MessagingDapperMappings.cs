using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Messaging.Entities;

namespace Themia.Modules.Messaging.Mapping;

/// <summary>Registers the Themia Messaging entity mappings (schema-qualified <c>messaging.*</c> table
/// names) into a Dapper <see cref="EntityMappingRegistry"/>, so the Dapper peer reads and writes the exact
/// same columns as the EF peer over the FluentMigrator-owned schema.</summary>
public static class MessagingDapperMappings
{
    /// <summary>Registers the Messaging entity mappings. Columns follow the snake_case convention, which
    /// matches the EF config and the migration one-for-one.</summary>
    /// <param name="registry">The registry to populate.</param>
    public static void Apply(EntityMappingRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register<MessageOutboxEntry>(
            EntityMapping.ForConvention<MessageOutboxEntry>("messaging.outbox_messages", null));
    }
}
