namespace Themia.Challenges;

/// <summary>
/// Per-purpose tuning: the secret's shape, how long it lives, how many wrong guesses it tolerates,
/// how many can be outstanding at once, and the two rate-limit windows. Every setter validates
/// eagerly — an invalid value throws from the assignment itself, inside the <c>configure</c> callback
/// passed to <see cref="ChallengeOptions.ConfigurePurpose"/>, rather than surfacing later from some
/// separate validation sweep. That keeps the failure at the call site that caused it.
/// </summary>
public sealed class PurposeOptions
{
    private ChallengeFormat _format = ChallengeFormat.Numeric(6);
    private TimeSpan _ttl = TimeSpan.FromMinutes(5);
    private int _maxAttempts = 5;
    private int _maxLiveChallenges = 1;
    private (int Limit, TimeSpan Window) _perScopeWindow = (3, TimeSpan.FromMinutes(15));
    private (int Limit, TimeSpan Window) _perKeyWindow = (20, TimeSpan.FromHours(1));

    /// <summary>The shape of the secret this purpose issues. Defaults to a 6-digit numeric code.</summary>
    /// <exception cref="ArgumentNullException">Assigned <see langword="null"/>.</exception>
    public ChallengeFormat Format
    {
        get => _format;
        set => _format = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>How long an issued secret remains valid. Defaults to 5 minutes.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Assigned a value that is zero or negative.</exception>
    public TimeSpan Ttl
    {
        get => _ttl;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _ttl = value;
        }
    }

    /// <summary>
    /// How many incorrect verify attempts a single issued secret tolerates before it is exhausted.
    /// Defaults to 5. Not removable — only its value is tunable, because an unbounded number of
    /// guesses defeats the purpose of a short numeric code.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Assigned a value that is zero or negative.</exception>
    public int MaxAttempts
    {
        get => _maxAttempts;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxAttempts = value;
        }
    }

    /// <summary>
    /// How many un-consumed, unexpired challenges may exist at once for a single scope. Defaults to 1:
    /// issuing a new secret supersedes any earlier one still outstanding for the same key and purpose.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Assigned a value that is zero or negative.</exception>
    public int MaxLiveChallenges
    {
        get => _maxLiveChallenges;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxLiveChallenges = value;
        }
    }

    /// <summary>
    /// The rate limit on issuance for one scope (key + purpose + tenant): at most <c>Limit</c> secrets
    /// may be issued within <c>Window</c>. Defaults to 3 per 15 minutes. Narrower than
    /// <see cref="PerKeyWindow"/>, which caps the same key across every purpose.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><c>Limit</c> or <c>Window</c> is zero or negative.</exception>
    public (int Limit, TimeSpan Window) PerScopeWindow
    {
        get => _perScopeWindow;
        set => _perScopeWindow = ValidateWindow(value);
    }

    /// <summary>
    /// The rate limit on issuance for one key across all purposes: at most <c>Limit</c> secrets may be
    /// issued to the same key within <c>Window</c>, regardless of purpose. Defaults to 20 per hour.
    /// Wider than <see cref="PerScopeWindow"/> — it exists to cap an attacker cycling through purposes
    /// against the same phone number or email address.
    /// <para>
    /// This is a cost ceiling, not a brute-force defense — <see cref="MaxAttempts"/> already stops
    /// brute force on a single issued secret. Keep this limit far above what a real user ever reaches:
    /// a real user asks once or twice, so even 10 already gives an attacker who merely knows the
    /// victim's phone number or email a cheap way to burn the ceiling and lock that person out of
    /// issuance until the window elapses. Widening it (currently 20) costs nothing against that
    /// attack — the attempt cap is what actually protects the secret — but a low value converts "an
    /// attacker knows your phone number" into "you can't receive an OTP for an hour". Do not lower
    /// this "to be safe"; it does not make brute force harder and it does make lockout easier.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><c>Limit</c> or <c>Window</c> is zero or negative.</exception>
    public (int Limit, TimeSpan Window) PerKeyWindow
    {
        get => _perKeyWindow;
        set => _perKeyWindow = ValidateWindow(value);
    }

    private static (int Limit, TimeSpan Window) ValidateWindow((int Limit, TimeSpan Window) value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.Limit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value.Window, TimeSpan.Zero);
        return value;
    }
}

/// <summary>
/// Per-purpose configuration for <c>Themia.Challenges</c>. A purpose (e.g. <c>"login"</c>,
/// <c>"password-reset"</c>) must be configured with <see cref="ConfigurePurpose"/> before any
/// challenge can be issued or verified against it — there is no implicit default purpose.
/// </summary>
public sealed class ChallengeOptions
{
    private readonly Dictionary<string, PurposeOptions> _purposes = new(StringComparer.Ordinal);
    private int _challengeRetentionHours = 24;

    /// <summary>
    /// Whether the background retention purge (<see cref="Internal.ChallengePurgeService"/>) runs at
    /// all. Defaults to <see langword="true"/> — this schema is new, so there is no pre-existing history
    /// enabling it could destroy, unlike an outbox where flipping this on for an existing deployment
    /// would delete accumulated rows on the first run.
    /// </summary>
    public bool PurgeEnabled { get; set; } = true;

    /// <summary>
    /// How long a challenge row survives after it is no longer live (consumed or expired) before the
    /// purge hard-deletes it. Defaults to 24 hours. Applies only to the <c>challenges</c> table —
    /// <c>challenge_rate_windows</c> rows are purged on their own elapsed-window rule regardless of this
    /// setting: a window must outlive the challenges it counted, or purging it early hands an attacker a
    /// free reset of the per-key ceiling that bounds the SMS bill. See
    /// <see cref="IChallengeDialect.PurgeExpiredSql"/> and <see cref="IChallengeDialect.PurgeElapsedWindowsSql"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Assigned a value that is zero or negative.</exception>
    public int ChallengeRetentionHours
    {
        get => _challengeRetentionHours;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _challengeRetentionHours = value;
        }
    }

    /// <summary>
    /// The widest rate-limit window configured across every purpose registered so far — the longer of
    /// each purpose's <see cref="PurposeOptions.PerScopeWindow"/> and <see cref="PurposeOptions.PerKeyWindow"/>
    /// durations. <see cref="Internal.ChallengePurgeService"/> uses this to compute how long a
    /// <c>challenge_rate_windows</c> row must survive: a fixed retention shorter than the widest
    /// configured window would purge a counter a still-active window depends on, silently resetting the
    /// cost ceiling the two-table split exists to protect (see <see cref="IChallengeDialect.PurgeElapsedWindowsSql"/>).
    /// Returns <see cref="TimeSpan.Zero"/> if no purpose has been configured yet, which the caller treats
    /// as "nothing to purge" rather than "purge everything".
    /// </summary>
    internal TimeSpan WidestConfiguredWindow()
    {
        var widest = TimeSpan.Zero;
        foreach (var purpose in _purposes.Values)
        {
            if (purpose.PerScopeWindow.Window > widest)
            {
                widest = purpose.PerScopeWindow.Window;
            }

            if (purpose.PerKeyWindow.Window > widest)
            {
                widest = purpose.PerKeyWindow.Window;
            }
        }

        return widest;
    }

    /// <summary>
    /// Configures (or reconfigures) a purpose. Validation is eager: an invalid value throws from the
    /// property setter inside <paramref name="configure"/>, not from a later validation pass.
    /// </summary>
    /// <param name="purpose">The purpose name, e.g. <c>"login"</c> or <c>"password-reset"</c>.</param>
    /// <param name="configure">Callback that sets the purpose's tunables.</param>
    /// <exception cref="ArgumentException"><paramref name="purpose"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    public void ConfigurePurpose(string purpose, Action<PurposeOptions> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(configure);

        var options = _purposes.TryGetValue(purpose, out var existing) ? existing : new PurposeOptions();
        configure(options);
        _purposes[purpose] = options;
    }

    /// <summary>Retrieves the configuration for a purpose.</summary>
    /// <param name="purpose">The purpose name.</param>
    /// <returns>The purpose's configuration.</returns>
    /// <exception cref="ArgumentException"><paramref name="purpose"/> is null or empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="purpose"/> was never configured via <see cref="ConfigurePurpose"/>.
    /// </exception>
    public PurposeOptions GetPurpose(string purpose)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);

        if (_purposes.TryGetValue(purpose, out var options))
        {
            return options;
        }

        throw new InvalidOperationException(
            $"Purpose '{purpose}' was never configured. Call {nameof(ChallengeOptions)}.{nameof(ConfigurePurpose)}(\"{purpose}\", ...) before issuing or verifying against it.");
    }

    /// <summary>
    /// Validates the options. A no-op today: every tunable, on this type and on
    /// <see cref="PurposeOptions"/>, already validates eagerly from its own property setter (see the
    /// type's remarks), so there is nothing left to check at the top level. Called from
    /// <c>AddThemiaChallenges</c> regardless, matching every other Themia options type's registration
    /// flow, and as the seam a future package-level invariant (e.g. "at least one purpose configured")
    /// would hang off.
    /// </summary>
    public void Validate()
    {
    }
}
