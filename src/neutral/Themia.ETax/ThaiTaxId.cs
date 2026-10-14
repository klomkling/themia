namespace Themia.ETax;

/// <summary>Thai taxpayer / national id (13 digits) checks.</summary>
public static class ThaiTaxId
{
    /// <summary>The number of digits.</summary>
    public const int Length = 13;

    /// <summary>True when the value is exactly 13 ASCII digits (the only check older stored profiles ever had).</summary>
    public static bool IsWellFormed(string? value) =>
        value is { Length: Length } && value.All(char.IsAsciiDigit);

    /// <summary>
    /// True when the value is well formed and its last digit is the mod-11 check digit of the first twelve:
    /// <c>(11 − Σ dᵢ × (13 − i) mod 11) mod 10</c>.
    /// </summary>
    public static bool HasValidChecksum(string? value)
    {
        if (!IsWellFormed(value))
            return false;

        var sum = 0;
        for (var i = 0; i < Length - 1; i++)
            sum += (value![i] - '0') * (Length - i);

        return (11 - sum % 11) % 10 == value![Length - 1] - '0';
    }
}
