namespace Themia.Payments;

/// <summary>Options shared by every adapter.</summary>
public sealed class ThemiaPaymentsOptions
{
    /// <summary>The configuration section these bind from.</summary>
    public const string SectionName = "Payments";

    /// <summary>
    /// Restricts which methods an amount may be paid with. <b>Null means no restriction</b> — the caller's
    /// own list passes through untouched.
    /// </summary>
    public PaymentMethodPolicy? MethodPolicy { get; set; }
}
