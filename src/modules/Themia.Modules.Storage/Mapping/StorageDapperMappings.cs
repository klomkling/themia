using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Storage.Entities;

namespace Themia.Modules.Storage.Mapping;

/// <summary>Registers Themia Storage entity mappings (schema-qualified table names) into a Dapper <see cref="EntityMappingRegistry"/>.</summary>
public static class StorageDapperMappings
{
    /// <summary>Registers the Storage entity mappings. The snake_case column convention is kept; only the table names are schema-qualified.</summary>
    /// <param name="registry">The registry to populate.</param>
    public static void Apply(EntityMappingRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        // The snake_case convention maps ETag → "e_tag", but the schema (and the EF config) use "etag".
        var columnOverrides = new Dictionary<string, string> { ["ETag"] = "etag" };
        registry.Register<StorageObject>(EntityMapping.ForConvention<StorageObject>("storage.storage_objects", columnOverrides));
    }
}
