namespace Themia.SecondFactor;

/// <summary>Pure policy: decides what a sign-in must do for a second factor. No I/O.</summary>
public interface IMfaPolicy
{
    /// <summary>Evaluates the requirement for one sign-in.</summary>
    /// <param name="subject">The user.</param>
    /// <param name="primary">How the user already authenticated.</param>
    /// <param name="methods">The user's enrolled methods (disabled ones are ignored).</param>
    /// <param name="spareNagStartedAt">When the spare-method prompt first appeared, or <see langword="null"/>.</param>
    /// <param name="now">The current time.</param>
    MfaRequirement Evaluate(
        MfaSubject subject,
        MfaPrimary primary,
        IReadOnlyCollection<MfaMethod> methods,
        DateTimeOffset? spareNagStartedAt,
        DateTimeOffset now);
}

/// <summary>Default <see cref="IMfaPolicy"/> driven by <see cref="MfaPolicyOptions"/>.</summary>
public sealed class MfaPolicy : IMfaPolicy
{
    private static readonly MfaMethodKind[] AllKinds = Enum.GetValues<MfaMethodKind>();

    private readonly MfaPolicyOptions options;

    /// <summary>Creates the policy.</summary>
    public MfaPolicy(MfaPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <inheritdoc />
    public MfaRequirement Evaluate(
        MfaSubject subject,
        MfaPrimary primary,
        IReadOnlyCollection<MfaMethod> methods,
        DateTimeOffset? spareNagStartedAt,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(methods);

        var rule = ResolveRule(subject);
        var allowed = AllKinds.Where(kind => rule.OfferedKinds.Contains(kind) && PrimaryAllows(primary, kind)).ToArray();
        var held = methods.Where(method => method.IsUsable).ToArray();
        var usable = held.Where(method => allowed.Contains(method.Kind)).ToArray();

        // A user who holds usable methods must never be treated as having none: that would answer Enrol (letting
        // someone with only the primary add their own factor) or NotRequired (silently dropping protection the user
        // opted into). Methods the rule or primary no longer offers are still verified, then replaced.
        if (usable.Length == 0 && held.Length > 0)
        {
            return FallbackToHeldMethods(primary, held, allowed);
        }

        // Enrol only at zero held methods: with one method already present, an Enrol answer would let
        // someone holding just the password add their own factor and complete the sign-in unproven.
        if (usable.Length == 0)
        {
            return new MfaRequirement
            {
                Level = rule.Required ? MfaRequirementLevel.Enrol : MfaRequirementLevel.NotRequired,
                EnrolKinds = allowed,
                UsableMethods = usable,
            };
        }

        var spareNeeded = rule.Required && usable.Length < rule.MinimumEnrolled;
        var graceOver = spareNagStartedAt is { } started && now - started >= rule.SpareGrace;
        return new MfaRequirement
        {
            Level = MfaRequirementLevel.Verify,
            EnrolKinds = allowed,
            UsableMethods = usable,
            SpareNeeded = spareNeeded,
            SpareMandatory = spareNeeded && (rule.SpareGrace <= TimeSpan.Zero || graceOver),
            SpareNagShouldStart = spareNeeded && rule.SpareGrace > TimeSpan.Zero && spareNagStartedAt is null,
        };
    }

    private static MfaRequirement FallbackToHeldMethods(MfaPrimary primary, MfaMethod[] held, MfaMethodKind[] allowed)
    {
        // A method the primary cannot rely on (same channel as the primary, or weak under an external primary) proves nothing new.
        var provable = held.Where(method => PrimaryAllows(primary, method.Kind)).ToArray();
        return new MfaRequirement
        {
            Level = provable.Length == 0 ? MfaRequirementLevel.Denied : MfaRequirementLevel.Verify,
            EnrolKinds = allowed,
            UsableMethods = provable,
            UpgradeMandatory = provable.Length > 0,
        };
    }

    // Strictest across the default and every matching role: required if any says so, highest minimum,
    // shortest grace, intersection of offered kinds.
    private MfaRule ResolveRule(MfaSubject subject)
    {
        var rules = new List<MfaRule> { options.Default };
        rules.AddRange(subject.Roles.Where(options.Roles.ContainsKey).Select(role => options.Roles[role]));

        return new MfaRule
        {
            Required = rules.Any(rule => rule.Required),
            MinimumEnrolled = rules.Max(rule => rule.MinimumEnrolled),
            SpareGrace = rules.Min(rule => rule.SpareGrace),
            OfferedKinds = rules.Select(rule => rule.OfferedKinds).Aggregate((a, b) => a.Intersect(b).ToHashSet()),
        };
    }

    // One predicate for both the offered path and the held-methods fallback, so they cannot drift apart.
    // An external primary (LINE) is the same account as any delivery channel, so only strong kinds count;
    // an emailed link makes an emailed code the same channel.
    private static bool PrimaryAllows(MfaPrimary primary, MfaMethodKind kind) => primary.Kind switch
    {
        MfaPrimaryKind.External => kind.Strength() == MfaStrength.Strong,
        MfaPrimaryKind.EmailLink => kind != MfaMethodKind.EmailOtp,
        _ => true,
    };
}
