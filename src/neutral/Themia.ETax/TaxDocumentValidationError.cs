namespace Themia.ETax;

/// <summary>One failed check.</summary>
/// <param name="Code">Why it failed.</param>
/// <param name="Field">The snapshot field, in the persisted snake_case path form (e.g. <c>seller.branch</c>, <c>lines[0].description</c>).</param>
public readonly record struct TaxDocumentValidationError(TaxDocumentValidationCode Code, string Field);
