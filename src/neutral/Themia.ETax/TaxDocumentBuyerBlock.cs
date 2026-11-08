using System.Text.RegularExpressions;

namespace Themia.ETax;

/// <summary>
/// The buyer block of a full tax document as the PDF prints it, so an app preview, tick or hash cannot drift from the printed document.
/// Full form only: an abbreviated document prints no buyer block whatever version it records.
/// </summary>
/// <remarks>
/// The lines are what a browser lays out, not the raw strings. Name and (templates 4 to 7) address collapse runs of white space into one space;
/// the address of templates 1 to 3 keeps its line breaks (one line per break), each line collapsed and trimmed; empty lines are dropped. The
/// heading ("ผู้ซื้อ / Buyer") is not a line. Apps should hash <c>templateVersion + Lines</c> at confirmation, recompute at issue and refuse on a
/// mismatch, so a template change between the two forces a re-confirm instead of printing something else.
/// </remarks>
public static class TaxDocumentBuyerBlock
{
    private const string FlatTaxIdLabel = "เลขประจำตัวผู้เสียภาษี / Tax ID:";
    private const string PagedTaxIdLabel = "เลขประจำตัวผู้เสียภาษี (Tax ID)";

    private static readonly Regex Whitespace = new(@"[\s\u00A0]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LineBreak = new(@"\r\n|\n|\r", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The buyer lines of a document issued, or to be issued, under <paramref name="templateVersion"/>, in print order.</summary>
    /// <param name="buyer">The buyer from the snapshot.</param>
    /// <param name="templateVersion">The version recorded on an issued document (<c>TaxDocumentRecord.TemplateVersion</c>).</param>
    /// <returns>Templates 1 to 3: name, address lines, <c>เลขประจำตัวผู้เสียภาษี / Tax ID: 0105536000001</c>, branch label.
    /// Templates 4 to 7: name, address on one line, <c>เลขประจำตัวผู้เสียภาษี (Tax ID) 0 1055 36000 00 1 สำนักงานใหญ่ (Head office)</c>
    /// (the branch only with a tax id, as printed).</returns>
    /// <exception cref="ArgumentException"><paramref name="templateVersion"/> is not a supported version.</exception>
    public static IReadOnlyList<string> Lines(TaxParty buyer, string templateVersion)
    {
        ArgumentNullException.ThrowIfNull(buyer);
        if (!TaxDocumentTemplates.IsSupported(templateVersion))
            throw new ArgumentException($"'{templateVersion}' is not a supported template version.", nameof(templateVersion));

        return TaxDocumentTemplates.PageSizeOf(templateVersion) is null ? FlatLines(buyer) : PagedLines(buyer);
    }

    /// <summary>The buyer lines for the version the service will issue under, so a preview follows the configuration instead of a copy of it.</summary>
    /// <param name="buyer">The buyer from the snapshot.</param>
    /// <param name="options">The same options object the document service is registered with.</param>
    /// <returns>See <see cref="Lines(TaxParty, string)"/>.</returns>
    public static IReadOnlyList<string> Lines(TaxParty buyer, TaxDocumentTemplateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Lines(buyer, options.TemplateVersion);
    }

    private static List<string> FlatLines(TaxParty buyer)
    {
        var lines = new List<string> { Collapse(buyer.Name) };
        if (!string.IsNullOrEmpty(buyer.Address))
        {
            foreach (var line in LineBreak.Split(buyer.Address))
            {
                var collapsed = Collapse(line);
                if (collapsed.Length > 0)
                    lines.Add(collapsed);
            }
        }

        if (!string.IsNullOrEmpty(buyer.TaxId))
            lines.Add($"{FlatTaxIdLabel} {buyer.TaxId}");
        if (TaxPartyText.FlatBranch(buyer.Branch) is { } branch)
            lines.Add(branch);
        return lines;
    }

    private static List<string> PagedLines(TaxParty buyer)
    {
        var lines = new List<string> { Collapse(buyer.Name) };
        if (!string.IsNullOrEmpty(buyer.Address))
            lines.Add(Collapse(buyer.Address));

        if (!string.IsNullOrEmpty(buyer.TaxId))
        {
            var taxLine = $"{PagedTaxIdLabel} {TaxPartyText.GroupedTaxId(buyer.TaxId)}";
            if (TaxPartyText.PagedBranchTh(buyer.Branch) is { } th)
                taxLine += $" {th} ({TaxPartyText.PagedBranchEn(buyer.Branch)})";
            lines.Add(taxLine);
        }

        return lines;
    }

    // What a browser does with white-space: normal: runs (including line breaks and no-break spaces) become one space, ends are trimmed.
    private static string Collapse(string text) => Whitespace.Replace(text, " ").Trim();
}
