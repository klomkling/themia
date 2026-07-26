namespace Themia.AspNetCore.DataProtection;

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
    /// <summary>Every stored key element, as raw XML strings.</summary>
    IReadOnlyList<string> GetAllXml();

    /// <summary>Appends one key element.</summary>
    void StoreXml(string? friendlyName, string xml);
}
