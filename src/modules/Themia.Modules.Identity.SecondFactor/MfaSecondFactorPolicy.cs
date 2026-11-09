using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.SecondFactor;

namespace Themia.Modules.Identity.SecondFactor;

/// <summary>
/// Answers Identity's second-factor question from <see cref="ISecondFactorService"/>: the user's enrolled methods, the per-app and
/// per-role rule, and the strength the primary check allows. A user who cannot be found is <see cref="SecondFactorRequirement.Denied"/>.
/// </summary>
/// <remarks>
/// A rule that is optional (not mandatory) and a user with no method yields <see cref="SecondFactorRequirement.NotRequired"/>, so a
/// caller who passed only the password can still enrol the first factor afterwards; roles that must be protected belong in the
/// mandatory rule (the <c>MfaPolicy</c> merges the rules of every role the user holds).
/// </remarks>
internal sealed class MfaSecondFactorPolicy(ISecondFactorService service, ISecondFactorSubjectResolver subjects) : ISecondFactorPolicy
{
    public async ValueTask<SecondFactorRequirement> EvaluateAsync(SecondFactorContext context, CancellationToken cancellationToken = default)
    {
        var subject = await subjects.ResolveAsync(context.UserId, cancellationToken).ConfigureAwait(false);
        if (subject is null)
        {
            return SecondFactorRequirement.Denied;
        }

        var requirement = await service.EvaluateAsync(subject, SecondFactorMapping.ToPrimary(context.Method, context.Provider), cancellationToken).ConfigureAwait(false);
        return SecondFactorMapping.ToRequirement(requirement.Level);
    }
}

internal static class SecondFactorMapping
{
    // A phone-code primary must not be followed by a code over the same channel, and no weak method can back an external provider
    // (LINE, SSO) or an app-minted session, so those use the External primary, under which only strong methods count (fail closed).
    internal static MfaPrimary ToPrimary(SecondFactorMethod method, string? provider) => method switch
    {
        SecondFactorMethod.Password => new MfaPrimary(MfaPrimaryKind.Password),
        SecondFactorMethod.Otp => new MfaPrimary(MfaPrimaryKind.External, provider),
        SecondFactorMethod.External => new MfaPrimary(MfaPrimaryKind.External, provider),
        SecondFactorMethod.Custom => new MfaPrimary(MfaPrimaryKind.External, provider),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unknown second-factor primary method."),
    };

    internal static SecondFactorRequirement ToRequirement(MfaRequirementLevel level) => level switch
    {
        MfaRequirementLevel.NotRequired => SecondFactorRequirement.NotRequired,
        MfaRequirementLevel.Verify => SecondFactorRequirement.Verify,
        MfaRequirementLevel.Enrol => SecondFactorRequirement.Enrol,
        MfaRequirementLevel.Denied => SecondFactorRequirement.Denied,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown second-factor requirement level."),
    };
}
