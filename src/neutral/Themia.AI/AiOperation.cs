namespace Themia.AI;

/// <summary>Which configured model a call resolves to.</summary>
/// <remarks>
/// Not a capability — every provider serves all of these through the same endpoint. The operation
/// exists so configuration can point completion and translation at different models for the same
/// provider (e.g. a cheaper model for translation than for open-ended completion).
/// </remarks>
public enum AiOperation
{
    /// <summary>Reserved. A real call never sends this — treat it as a bug if seen.</summary>
    Unspecified = 0,

    /// <summary>An open-ended completion request.</summary>
    Completion,

    /// <summary>A translation request.</summary>
    Translation,
}
