namespace Themia.AspNetCore.DataProtection;

/// <summary>One stored key-ring element, with the identity needed to delete it.</summary>
/// <param name="Id">Primary key of the row.</param>
/// <param name="Xml">The raw stored XML.</param>
public sealed record DataProtectionKeyRecord(long Id, string? Xml);

/// <summary>
/// Persistence for the Data Protection key ring, kept separate from the
/// <see cref="Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository"/> adapter so swapping the
/// store (a different engine, or something other than SQL) is a change in one place.
/// </summary>
/// <remarks>
/// Synchronous by design: <c>IXmlRepository</c> is a synchronous interface, so an async store here would
/// only be consumed by blocking on it.
/// </remarks>
public interface IDataProtectionKeyStore
{
    /// <summary>Every stored element, oldest first.</summary>
    IReadOnlyList<DataProtectionKeyRecord> GetAll();

    /// <summary>Appends one element.</summary>
    void StoreXml(string? friendlyName, string xml);

    /// <summary>
    /// Deletes one row. Returns whether a row was actually removed.
    /// </summary>
    bool Delete(long id);
}
