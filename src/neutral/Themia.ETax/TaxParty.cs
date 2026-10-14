namespace Themia.ETax;

/// <summary>The seller or buyer as printed on the document.</summary>
public sealed record TaxParty
{
    /// <summary>Legal or display name. The app resolves its own fallback (e.g. profile name, contact name) before issuing.</summary>
    public required string Name { get; init; }

    /// <summary>13-digit tax id. Required for the seller, optional for the buyer.</summary>
    public string? TaxId { get; init; }

    /// <summary>5-digit branch code. Required for the seller, optional for the buyer.</summary>
    public string? Branch { get; init; }

    /// <summary>Address as printed. Required for the seller.</summary>
    public string? Address { get; init; }
}
