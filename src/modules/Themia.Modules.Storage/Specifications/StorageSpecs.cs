using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Modules.Storage.Entities;

namespace Themia.Modules.Storage.Specifications;

/// <summary>The object with the given logical key within the ambient tenant (the framework tenant filter
/// isolates it).</summary>
internal sealed class StorageObjectByKeySpec : Specification<StorageObject>
{
    public StorageObjectByKeySpec(string key) => Where(o => o.Key == key);
}

/// <summary>All objects in the ambient tenant (soft-deleted rows are excluded by the framework filter).
/// Used to sum current usage for the per-tenant quota check.</summary>
internal sealed class AllStorageObjectsSpec : Specification<StorageObject>
{
    public AllStorageObjectsSpec() => Where(o => o.SizeBytes >= 0);
}
