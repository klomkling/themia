namespace Themia.Generators.Abstractions.Diagnostics;

/// <summary>
/// Reserved diagnostic ID ranges for Themia source generators and analyzers.
/// Consumer-authored generators should pick IDs from <see cref="ConsumerRange"/>
/// to avoid future collisions with our reserved ranges.
/// </summary>
public static class DiagnosticIdRange
{
    /// <summary>Reserved for the Themia DI source generator.</summary>
    public const string ThemiaDIRange = "THEMIA001-THEMIA099";

    /// <summary>Reserved for future Themia misuse analyzers.</summary>
    public const string ThemiaAnalyzersRange = "THEMIA100-THEMIA199";

    /// <summary>Recommended range for consumer-authored generator diagnostics.</summary>
    public const string ConsumerRange = "THEMIA200+";
}
