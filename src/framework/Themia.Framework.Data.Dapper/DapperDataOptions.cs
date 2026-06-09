namespace Themia.Framework.Data.Dapper;

/// <summary>Options for the Dapper data layer.</summary>
public sealed class DapperDataOptions
{
    /// <summary>When true, tenant queries also match rows with a NULL tenant_id (global/shared records).</summary>
    public bool IncludeGlobalRecordsForTenants { get; set; }

    /// <summary>Optional hook to register per-entity mapping overrides.</summary>
    public Action<EntityMappingRegistryConfigurator>? ConfigureMappings { get; set; }
}

/// <summary>Fluent helper to register entity mapping overrides.</summary>
public sealed class EntityMappingRegistryConfigurator(Mapping.EntityMappingRegistry registry)
{
    /// <summary>Registers an explicit mapping for <typeparamref name="T"/>.</summary>
    public EntityMappingRegistryConfigurator Map<T>(Mapping.EntityMapping mapping)
    {
        registry.Register<T>(mapping);
        return this;
    }
}
