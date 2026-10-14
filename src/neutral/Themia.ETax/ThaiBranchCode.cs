namespace Themia.ETax;

/// <summary>Thai establishment branch code (5 digits, <c>00000</c> = head office).</summary>
public static class ThaiBranchCode
{
    /// <summary>The number of digits.</summary>
    public const int Length = 5;

    /// <summary>True when the value is exactly 5 ASCII digits.</summary>
    public static bool IsWellFormed(string? value) =>
        value is { Length: Length } && value.All(char.IsAsciiDigit);
}
