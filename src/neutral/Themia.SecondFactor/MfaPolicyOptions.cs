namespace Themia.SecondFactor;

/// <summary>One required/optional rule: which methods are offered and how many a user must hold.</summary>
public sealed class MfaRule
{
    /// <summary>Default grace before a missing spare method becomes mandatory (owner, 2026-11-08).</summary>
    public static readonly TimeSpan DefaultSpareGrace = TimeSpan.FromDays(14);

    /// <summary>Whether the user must have a second factor.</summary>
    public bool Required { get; init; }

    /// <summary>Methods offered under this rule. Defaults to every kind.</summary>
    public IReadOnlySet<MfaMethodKind> OfferedKinds { get; init; } = new HashSet<MfaMethodKind>(Enum.GetValues<MfaMethodKind>());

    /// <summary>The number of usable methods a required user should hold (2 = one spare).</summary>
    public int MinimumEnrolled { get; init; } = 1;

    /// <summary>How long a user may sign in below <see cref="MinimumEnrolled"/> before adding a spare is mandatory.
    /// <see cref="TimeSpan.Zero"/> means mandatory at once (admin roles).</summary>
    public TimeSpan SpareGrace { get; init; } = DefaultSpareGrace;

    /// <summary>A rule that needs no second factor.</summary>
    public static MfaRule Optional() => new();

    /// <summary>A rule that requires a second factor.</summary>
    /// <param name="minimumEnrolled">Usable methods a user should hold.</param>
    /// <param name="offered">Offered kinds, or <see langword="null"/> for all.</param>
    /// <param name="spareGrace">Grace before a spare is mandatory, or <see langword="null"/> for 14 days.</param>
    public static MfaRule Mandatory(int minimumEnrolled = 1, IEnumerable<MfaMethodKind>? offered = null, TimeSpan? spareGrace = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumEnrolled, 1);
        var rule = new MfaRule
        {
            Required = true,
            MinimumEnrolled = minimumEnrolled,
            SpareGrace = spareGrace ?? DefaultSpareGrace,
        };
        return offered is null ? rule : new MfaRule
        {
            Required = rule.Required,
            MinimumEnrolled = rule.MinimumEnrolled,
            SpareGrace = rule.SpareGrace,
            OfferedKinds = offered.ToHashSet(),
        };
    }
}

/// <summary>Per-app and per-role second-factor rules. The strictest matching rule wins.</summary>
public sealed class MfaPolicyOptions
{
    /// <summary>The rule for every user. Defaults to optional.</summary>
    public MfaRule Default { get; set; } = MfaRule.Optional();

    /// <summary>Extra rules by role name; merged strictest-wins with <see cref="Default"/>.</summary>
    public Dictionary<string, MfaRule> Roles { get; } = new(StringComparer.Ordinal);
}
