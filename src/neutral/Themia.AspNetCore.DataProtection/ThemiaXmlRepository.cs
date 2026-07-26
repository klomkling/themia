using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Themia.AspNetCore.DataProtection;

/// <summary>
/// Adapts Data Protection's <see cref="IXmlRepository"/> onto <see cref="IDataProtectionKeyStore"/>, so the
/// key ring lives in one shared database rather than a per-instance filesystem.
/// </summary>
/// <remarks>
/// Without a shared store, every instance of a horizontally-scaled app generates its own key ring: auth
/// cookies, antiforgery tokens, and anything else wrapped by a <c>DataProtector</c> stop round-tripping the
/// moment a request lands on a different instance than the one that issued them.
///
/// <para>On .NET 10 and later this also implements <c>IDeletableXmlRepository</c>, so
/// <c>IKeyManager.DeleteKeys</c> works and revoked key material can actually be removed from the table. That
/// interface does not exist on .NET 8, where the framework has no deletion API at all — hence the
/// target-framework-specific public API surface for this package.</para>
/// </remarks>
public sealed class ThemiaXmlRepository :
#if NET10_0_OR_GREATER
    IDeletableXmlRepository
#else
    IXmlRepository
#endif
{
    private readonly IDataProtectionKeyStore keys;

    /// <summary>Creates the repository over <paramref name="keys"/>.</summary>
    public ThemiaXmlRepository(IDataProtectionKeyStore keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        this.keys = keys;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">A stored row could not be parsed as XML.</exception>
    public IReadOnlyCollection<XElement> GetAllElements() => Read().Select(x => x.Element).ToList();

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        keys.StoreXml(friendlyName, element.ToString(SaveOptions.DisableFormatting));
    }

    /// <summary>
    /// Reads and parses the whole ring, failing on the first row that will not parse.
    /// </summary>
    /// <remarks>
    /// Deliberately fails closed rather than skipping bad rows, for two reasons a "skip one row, keep the
    /// rest" policy gets wrong:
    /// <list type="number">
    /// <item>Not every row is a key. <c>XmlKeyManager</c> writes <c>&lt;revocation&gt;</c> elements through
    /// this same repository, so a dropped row can be a revocation — its key then loads as live and
    /// unrevoked, quietly reinstating key material an operator had revoked after a compromise.</item>
    /// <item>Skipping cannot distinguish one damaged row from a systematically unreadable table (a charset
    /// change, a mangled restore). Returning the survivors of that is an <em>empty</em> ring, which Data
    /// Protection cannot tell apart from a fresh deployment: it mints a new key and signs out every user,
    /// while the application reports healthy.</item>
    /// </list>
    /// The built-in filesystem and registry repositories fail closed for the same reason.
    /// </remarks>
    private List<ParsedElement> Read()
    {
        var parsed = new List<ParsedElement>();

        foreach (var record in keys.GetAll())
        {
            if (string.IsNullOrWhiteSpace(record.Xml))
                throw new InvalidOperationException(
                    $"Themia.AspNetCore.DataProtection: the Data Protection key row with id {record.Id} has no " +
                    "XML. Refusing to continue: dropping it could discard a key revocation, and treating the " +
                    "remainder as the whole ring can silently re-key the application.");

            try
            {
                parsed.Add(new ParsedElement(record.Id, XElement.Parse(record.Xml)));
            }
            catch (XmlException ex)
            {
                throw new InvalidOperationException(
                    $"Themia.AspNetCore.DataProtection: the Data Protection key row with id {record.Id} is not " +
                    "well-formed XML. Refusing to continue: dropping it could discard a key revocation, and " +
                    "treating the remainder as the whole ring can silently re-key the application. Repair or " +
                    "remove the row deliberately.", ex);
            }
        }

        return parsed;
    }

    private sealed record ParsedElement(long Id, XElement Element);

#if NET10_0_OR_GREATER
    /// <inheritdoc />
    public bool DeleteElements(Action<IReadOnlyCollection<IDeletableElement>> chooseElements)
    {
        ArgumentNullException.ThrowIfNull(chooseElements);

        var candidates = Read().Select(p => new DeletableElement(p.Id, p.Element)).ToList();
        chooseElements(candidates);

        // Contract: delete in increasing DeletionOrder, and if any deletion fails, skip the rest.
        foreach (var chosen in candidates.Where(c => c.DeletionOrder is not null).OrderBy(c => c.DeletionOrder))
        {
            if (!keys.Delete(chosen.Id))
                return false;
        }

        return true;
    }

    private sealed class DeletableElement(long id, XElement element) : IDeletableElement
    {
        public long Id { get; } = id;

        public XElement Element { get; } = element;

        public int? DeletionOrder { get; set; }
    }
#endif
}
