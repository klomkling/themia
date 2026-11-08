namespace Themia.ETax;

/// <summary>The words the templates print for a party's branch and tax id. One source for the render models and <see cref="TaxDocumentBuyerBlock"/>.</summary>
internal static class TaxPartyText
{
    private const string HeadOfficeBranch = "00000";
    private static readonly int[] TaxIdGroups = [1, 4, 5, 2, 1];

    /// <summary>Templates 1 to 3: one label, printed even without a tax id.</summary>
    public static string? FlatBranch(string? branch) =>
        branch is null ? null : branch == HeadOfficeBranch ? "สำนักงานใหญ่ / Head office" : $"สาขา / Branch {branch}";

    /// <summary>Templates 4 to 7, Thai half.</summary>
    public static string? PagedBranchTh(string? branch) =>
        branch is null ? null : branch == HeadOfficeBranch ? "สำนักงานใหญ่" : "สาขาที่ " + branch;

    /// <summary>Templates 4 to 7, English half.</summary>
    public static string? PagedBranchEn(string? branch) =>
        branch is null ? null : branch == HeadOfficeBranch ? "Head office" : "Branch " + branch;

    /// <summary>A 13-digit id prints grouped the way the Revenue Department writes it (1 4 5 2 1); anything else prints as given.</summary>
    public static string GroupedTaxId(string id)
    {
        if (id.Length != 13 || !id.All(char.IsAsciiDigit))
            return id;

        var parts = new List<string>(TaxIdGroups.Length);
        var at = 0;
        foreach (var size in TaxIdGroups)
        {
            parts.Add(id.Substring(at, size));
            at += size;
        }

        return string.Join(' ', parts);
    }
}
