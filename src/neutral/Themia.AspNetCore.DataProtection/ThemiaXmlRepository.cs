using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging;

namespace Themia.AspNetCore.DataProtection;

/// <summary>
/// Adapts Data Protection's <see cref="IXmlRepository"/> onto <see cref="IDataProtectionKeyStore"/>, so the
/// key ring lives in one shared database rather than a per-instance filesystem.
/// </summary>
/// <remarks>
/// Without a shared store, every instance of a horizontally-scaled app generates its own key ring: auth
/// cookies, antiforgery tokens, and anything else wrapped by a <c>DataProtector</c> stop round-tripping the
/// moment a request lands on a different instance than the one that issued them.
/// </remarks>
public sealed class ThemiaXmlRepository : IXmlRepository
{
    private readonly IDataProtectionKeyStore keys;
    private readonly ILogger<ThemiaXmlRepository> logger;

    /// <summary>Creates the repository over <paramref name="keys"/>.</summary>
    public ThemiaXmlRepository(IDataProtectionKeyStore keys, ILogger<ThemiaXmlRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(logger);
        this.keys = keys;
        this.logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var elements = new List<XElement>();

        foreach (var xml in keys.GetAllXml())
        {
            try
            {
                elements.Add(XElement.Parse(xml));
            }
            catch (XmlException ex)
            {
                // One unparseable row (a partial write, a manual edit, encoding damage) must not take the
                // whole key ring down with it. Returning nothing would fail every unprotect operation in the
                // application; returning the rest means at worst one key is unavailable.
                logger.LogError(
                    ex, "Skipping an unparseable Data Protection key row; the remaining keys still load.");
            }
        }

        return elements;
    }

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        keys.StoreXml(friendlyName, element.ToString(SaveOptions.DisableFormatting));
    }
}
