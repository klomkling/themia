namespace Themia.Storage;

/// <summary>Whether an object is world-readable. Chosen when the object is written and <b>immutable</b>:
/// visibility selects the storage container, and no operation moves an object between containers.</summary>
public enum StorageVisibility
{
    /// <summary>Reachable only through the authenticated module endpoints or a presigned URL.</summary>
    Private,

    /// <summary>World-readable at a permanent, unsigned URL (via <c>IStorageProvider.GetPublicUrl</c>, added in the module wrapper).</summary>
    Public,
}
