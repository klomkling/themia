using System.Collections.Frozen;

namespace Themia.Payments.TwoCTwoP;

/// <summary>
/// The codes of 2C2P's "Payment Scheme / Process by" list (https://developer.2c2p.com/docs/reference-codes-payment-scheme) that its own
/// <c>Type</c> column files under Card and Wallet. A payment inquiry's <c>paymentScheme</c> and <c>channelCode</c> are values of this list, so the
/// type is what says how the shopper paid. Copied from the page as of 2026-10-30; a code 2C2P adds later is simply unmapped (<see langword="null"/>).
/// </summary>
internal static class TwoCTwoPProcessByCodes
{
    /// <summary>Codes of Type "Card" (Visa VI, MasterCard MA, JCB JC, UnionPay UP, American Express AM, ...).</summary>
    public static readonly FrozenSet<string> Cards = FrozenSet.ToFrozenSet(
    [
        "AM", "CP", "DI", "DN", "JC", "KD", "KP", "KZ", "LR", "MA", "MP", "NC", "OT", "PS", "RB", "RP", "UA", "UP", "VI", "WI",
    ], StringComparer.OrdinalIgnoreCase);

    /// <summary>Codes of Type "Wallet" (TrueMoney TM, ShopeePay SH, LINE Pay LP, PaoTang PP, ...), including the pay-later and installment wallets 2C2P files there.</summary>
    public static readonly FrozenSet<string> Wallets = FrozenSet.ToFrozenSet(
    [
        "A01", "A03", "A06", "A12", "AA", "AC", "AE", "AH", "AI", "AL", "AO", "AP", "AT", "AU", "AY", "AZ", "BA", "BD", "BH", "BL", "BO", "BP", "CA",
        "CB", "CY", "DA", "DC", "DF", "DK", "DL", "DM", "DP", "FM", "FN", "FP", "FPL", "G08", "G12", "GC", "GO", "GP", "GPI", "GPP", "HC", "HM",
        "HO", "I01", "I03", "I06", "I12", "ID", "IP", "JK", "JP", "KB", "KK", "KW", "KY", "LA", "LP", "LW", "MB", "MC", "MD", "ME", "MM", "MR", "MS",
        "MV", "MX", "MY", "NP", "NV", "OC", "OK", "OP", "OV", "PA", "PC", "PM", "PO", "PP", "PT", "PW", "PY", "RK", "S01", "S02", "S03", "S04",
        "S05", "S06", "S12", "S18", "S24", "SB", "SBL", "SBP", "SE", "SH", "SM", "SP", "SS", "TG", "TM", "TN", "TO", "TP", "VC", "VE", "VN", "VP",
        "VT", "WA", "WC", "WG", "WH", "WP", "WX", "ZP",
    ], StringComparer.OrdinalIgnoreCase);
}
