namespace Themia.ETax.Withholding;

/// <summary>Contributions the payer also paid, printed under the totals (relevant to salary income). Each is optional, in satang.</summary>
public sealed record WithholdingFunds
{
    /// <summary>กองทุนประกันสังคม (social security fund).</summary>
    public long? SocialSecuritySatang { get; init; }

    /// <summary>กองทุนสำรองเลี้ยงชีพ (provident fund).</summary>
    public long? ProvidentFundSatang { get; init; }

    /// <summary>กบข. / กองทุนบำเหน็จบำนาญข้าราชการ (government pension fund).</summary>
    public long? GovernmentPensionSatang { get; init; }
}
