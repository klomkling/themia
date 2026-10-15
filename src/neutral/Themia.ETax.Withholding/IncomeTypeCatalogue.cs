using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Themia.ETax.Withholding;

/// <summary>
/// A versioned, immutable set of <see cref="IncomeTypeEntry"/> the app selects from. A published <see cref="Version"/> is never
/// edited: a change ships as a new version, and <see cref="ContentHash"/> lets a test pin each shipped version so an in-place edit
/// fails the build.
/// </summary>
public sealed partial class IncomeTypeCatalogue
{
    private const int MaxCodeLength = 64;

    private readonly Dictionary<string, IncomeTypeEntry> _byCode;

    /// <summary>Creates a catalogue, copying <paramref name="entries"/>.</summary>
    /// <param name="version">The catalogue version, recorded in every certificate issued from it.</param>
    /// <param name="entries">The entries; codes must be unique.</param>
    /// <exception cref="ArgumentException">The version is blank, there are no entries, or an entry is unusable (blank text, bad code, nothing allowed, duplicate code).</exception>
    public IncomeTypeCatalogue(string version, IEnumerable<IncomeTypeEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(entries);

        Version = version;
        Entries = entries.Select(Copy).OrderBy(e => e.Code, StringComparer.Ordinal).ToArray();
        if (Entries.Count == 0)
            throw new ArgumentException("A catalogue needs at least one entry.", nameof(entries));

        _byCode = new Dictionary<string, IncomeTypeEntry>(Entries.Count, StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            Check(entry);
            if (!_byCode.TryAdd(entry.Code, entry))
                throw new ArgumentException($"Duplicate income type code '{entry.Code}'.", nameof(entries));
        }

        ContentHash = ComputeContentHash(Version, Entries);
    }

    /// <summary>The catalogue version.</summary>
    public string Version { get; }

    /// <summary>The entries, ordered by code.</summary>
    public IReadOnlyList<IncomeTypeEntry> Entries { get; }

    /// <summary>
    /// Lowercase hex SHA-256 over a canonical text of the version and every entry (entries and allowed lists in a fixed order).
    /// Independent of the order the entries were supplied in.
    /// </summary>
    public string ContentHash { get; }

    /// <summary>Finds an entry by its code (ordinal, case-sensitive).</summary>
    public bool TryGet(string? code, out IncomeTypeEntry? entry)
    {
        entry = null;
        return code is not null && _byCode.TryGetValue(code, out entry);
    }

    private static IncomeTypeEntry Copy(IncomeTypeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry with
        {
            AllowedFilingForms = entry.AllowedFilingForms?.ToArray()!,
            AllowedPayeeKinds = entry.AllowedPayeeKinds?.ToArray()!,
        };
    }

    private static void Check(IncomeTypeEntry e)
    {
        if (string.IsNullOrEmpty(e.Code) || e.Code.Length > MaxCodeLength || !CodePattern().IsMatch(e.Code))
            throw new ArgumentException($"Income type code '{e.Code}' must be 1-{MaxCodeLength} lowercase letters, digits and hyphens.", nameof(e));
        if (string.IsNullOrWhiteSpace(e.Section) || string.IsNullOrWhiteSpace(e.LabelTh))
            throw new ArgumentException($"Income type '{e.Code}' needs a section and a label.", nameof(e));
        if (e.SubItem is not null && string.IsNullOrWhiteSpace(e.SubItem))
            throw new ArgumentException($"Income type '{e.Code}' has a blank sub-item; use null for none.", nameof(e));
        if (!Enum.IsDefined(e.FormRow))
            throw new ArgumentException($"Income type '{e.Code}' needs a defined form row.", nameof(e));
        if (e.AllowedFilingForms is not { Count: > 0 } || !e.AllowedFilingForms.All(f => Enum.IsDefined(f)))
            throw new ArgumentException($"Income type '{e.Code}' needs at least one defined filing form.", nameof(e));
        if (e.AllowedPayeeKinds is not { Count: > 0 } || !e.AllowedPayeeKinds.All(k => Enum.IsDefined(k)))
            throw new ArgumentException($"Income type '{e.Code}' needs at least one defined payee kind.", nameof(e));
    }

    private static string ComputeContentHash(string version, IReadOnlyList<IncomeTypeEntry> entries)
    {
        var text = new StringBuilder("version=").Append(Escape(version)).Append('\n');
        foreach (var e in entries)
        {
            text.Append(Escape(e.Code)).Append('|')
                .Append(Escape(e.Section)).Append('|')
                .Append(Escape(e.SubItem ?? string.Empty)).Append('|')
                .Append(Escape(e.LabelTh)).Append('|')
                .Append(e.FormRow.ToString()).Append('|')
                .Append(string.Join(',', e.AllowedFilingForms.Distinct().OrderBy(f => (int)f).Select(f => f.ToString()))).Append('|')
                .Append(string.Join(',', e.AllowedPayeeKinds.Distinct().OrderBy(k => (int)k).Select(k => k.ToString())))
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
