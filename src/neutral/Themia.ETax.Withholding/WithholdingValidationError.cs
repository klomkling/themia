namespace Themia.ETax.Withholding;

/// <summary>One failed check.</summary>
/// <param name="Code">Why it failed.</param>
/// <param name="Field">The snapshot field, in the persisted snake_case path form (e.g. <c>payee.tax_id</c>, <c>rows[0].date_paid</c>).</param>
public readonly record struct WithholdingValidationError(WithholdingValidationCode Code, string Field);
