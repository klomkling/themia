namespace Themia.Modules.Identity.Abstractions;

/// <summary>Options for the passwordless phone-code login (<see cref="IOtpLoginFlow"/>).</summary>
public sealed class OtpLoginOptions
{
    /// <summary>The smallest accepted <see cref="CodeHashKey"/> length, in bytes.</summary>
    public const int MinimumCodeHashKeyLength = 32;

    /// <summary>The fewest digits a code may have. Below six the per-phone failure budget is a measurable
    /// share of the code space.</summary>
    public const int MinimumCodeLength = 6;

    /// <summary>The most digits a code may have (the largest that fits an unbiased 32-bit draw).</summary>
    public const int MaximumCodeLength = 9;

    /// <summary>Digits in a code, <see cref="MinimumCodeLength"/> to <see cref="MaximumCodeLength"/>. Default 6.</summary>
    public int CodeLength { get; set; } = 6;

    /// <summary>How long a code is accepted. Default 5 minutes.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Guesses allowed against one challenge. Default 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Failed guesses allowed per phone across ALL challenges inside <see cref="FailureWindow"/>.
    /// Issuing a new challenge never resets it. Default 10.</summary>
    public int MaxFailedAttemptsPerWindow { get; set; } = 10;

    /// <summary>The rolling window the failure cap and the start cap are counted over. Default 1 hour.</summary>
    public TimeSpan FailureWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Minimum time between two starts for the same phone. Default 60 seconds.</summary>
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Starts allowed per phone inside <see cref="FailureWindow"/>. Default 5.</summary>
    public int MaxStartsPerWindow { get; set; } = 5;

    /// <summary>
    /// The server secret the stored code hash is keyed with, supplied by the application (user secrets, a
    /// key vault, an environment variable; Themia generates and stores nothing). Every instance that serves
    /// the endpoints must be given the same key, or a code started on one instance fails on another. The
    /// options are read once, when <c>AddThemiaIdentityOtpLogin</c> runs, so the key must be available at
    /// registration and a change needs a restart. At least
    /// <see cref="MinimumCodeHashKeyLength"/> random bytes, with no default. A six-digit code hashed
    /// without a key can be reversed from a leaked table in milliseconds. Rotating the key invalidates
    /// the codes in flight (they live minutes). Never logged: <see cref="ToString"/> omits it.
    /// </summary>
    public byte[] CodeHashKey { get; set; } = [];

    /// <summary>Throws when a value would weaken the guarantees or make the flow unusable.</summary>
    public void Validate()
    {
        if (CodeLength is < MinimumCodeLength or > MaximumCodeLength)
        {
            throw new ArgumentOutOfRangeException(nameof(CodeLength), CodeLength, $"Must be between {MinimumCodeLength} and {MaximumCodeLength}.");
        }

        Positive(Lifetime, nameof(Lifetime));
        Positive(FailureWindow, nameof(FailureWindow));
        if (ResendCooldown < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ResendCooldown), ResendCooldown, "Must not be negative.");
        }

        AtLeastOne(MaxAttempts, nameof(MaxAttempts));
        AtLeastOne(MaxFailedAttemptsPerWindow, nameof(MaxFailedAttemptsPerWindow));
        AtLeastOne(MaxStartsPerWindow, nameof(MaxStartsPerWindow));

        if (CodeHashKey is null || CodeHashKey.Length < MinimumCodeHashKeyLength)
        {
            throw new ArgumentException(
                $"Must be at least {MinimumCodeHashKeyLength} random bytes.", nameof(CodeHashKey));
        }
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"OtpLoginOptions(CodeLength={CodeLength}, Lifetime={Lifetime}, MaxAttempts={MaxAttempts})";

    private static void Positive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be a positive duration.");
        }
    }

    private static void AtLeastOne(int value, string name)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be at least 1.");
        }
    }
}
