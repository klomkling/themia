namespace Themia.SecondFactor;

/// <summary>An enrolled method as the policy sees it (no secret material).</summary>
/// <param name="Id">The method row id.</param>
/// <param name="Kind">The method kind.</param>
/// <param name="Label">A user-visible label ("iPhone", "Authenticator").</param>
/// <param name="EnrolledAt">When the method was confirmed.</param>
/// <param name="DisabledAt">When the method was disabled, or <see langword="null"/> while usable.</param>
public sealed record MfaMethod(Guid Id, MfaMethodKind Kind, string Label, DateTimeOffset EnrolledAt, DateTimeOffset? DisabledAt)
{
    /// <summary>Whether the method can currently be used to verify.</summary>
    public bool IsUsable => DisabledAt is null;
}

/// <summary>The user being evaluated. <see cref="TenantId"/> is <see langword="null"/> for platform users.</summary>
/// <param name="UserId">The adopter's user id (Guid "D" form for Identity users).</param>
/// <param name="TenantId">The tenant, or <see langword="null"/> for a platform user.</param>
/// <param name="Roles">The user's role names.</param>
public sealed record MfaSubject(string UserId, string? TenantId, IReadOnlySet<string> Roles);

/// <summary>How the user already proved who they are, before the second factor.</summary>
public enum MfaPrimaryKind
{
    /// <summary>Username + password.</summary>
    Password,

    /// <summary>A link or code delivered by email.</summary>
    EmailLink,

    /// <summary>A federated or social provider (LINE, SSO).</summary>
    External,
}

/// <summary>The primary authentication in front of the second factor.</summary>
/// <param name="Kind">The primary kind.</param>
/// <param name="Provider">The provider name for <see cref="MfaPrimaryKind.External"/>.</param>
public sealed record MfaPrimary(MfaPrimaryKind Kind, string? Provider = null);

/// <summary>What the sign-in must do next.</summary>
public enum MfaRequirementLevel
{
    /// <summary>No second factor is needed.</summary>
    NotRequired,

    /// <summary>The user must pass one of their enrolled methods.</summary>
    Verify,

    /// <summary>The user has no usable method and must enrol the first one.</summary>
    Enrol,
}

/// <summary>The policy's answer for one sign-in.</summary>
public sealed record MfaRequirement
{
    /// <summary>What the sign-in must do next.</summary>
    public required MfaRequirementLevel Level { get; init; }

    /// <summary>Kinds the user may enrol now (offered by the rule and allowed by the primary).</summary>
    public required IReadOnlyCollection<MfaMethodKind> EnrolKinds { get; init; }

    /// <summary>Enrolled methods usable to verify under this rule and primary.</summary>
    public required IReadOnlyCollection<MfaMethod> UsableMethods { get; init; }

    /// <summary>The user holds fewer usable methods than the rule's minimum.</summary>
    public bool SpareNeeded { get; init; }

    /// <summary>The caller should record the first-prompt time so the grace period starts counting.</summary>
    public bool SpareNagShouldStart { get; init; }

    /// <summary>The grace period is over: after verifying, the user must add a method before continuing.</summary>
    public bool SpareMandatory { get; init; }
}
