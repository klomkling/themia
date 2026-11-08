using Microsoft.Extensions.Options;

namespace Themia.SecondFactor;

/// <summary>Behaviour of <see cref="ISecondFactorService"/>.</summary>
public sealed class SecondFactorOptions
{
    /// <summary>The Challenges purpose used for emailed codes. Must be configured in <c>ChallengeOptions</c>.</summary>
    public const string EmailOtpPurpose = "second-factor.email";

    /// <summary>The Challenges purpose used for texted codes. Must be configured in <c>ChallengeOptions</c>.</summary>
    public const string SmsOtpPurpose = "second-factor.sms";

    /// <summary>Per-app and per-role rules: required or optional, offered methods, minimum enrolled.</summary>
    public MfaPolicyOptions Policy { get; } = new();

    /// <summary>The issuer shown in the authenticator app. Required.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>How old a verification may be and still authorize a sensitive action.</summary>
    public TimeSpan StrongAuthMaxAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Verification attempts allowed per user (TOTP and recovery codes together) before the lock.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How long the lock lasts.</summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a passkey ceremony or pending TOTP enrolment stays valid.</summary>
    public TimeSpan CeremonyTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Recovery codes per batch.</summary>
    public int RecoveryCodeCount { get; set; } = RecoveryCodeGenerator.DefaultCount;
}

internal sealed class SecondFactorOptionsValidator : IValidateOptions<SecondFactorOptions>
{
    public ValidateOptionsResult Validate(string? name, SecondFactorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            errors.Add("Issuer is required (it is the name shown in the authenticator app).");
        }

        if (options.StrongAuthMaxAge <= TimeSpan.Zero)
        {
            errors.Add("StrongAuthMaxAge must be positive.");
        }

        if (options.MaxAttempts < 1)
        {
            errors.Add("MaxAttempts must be at least 1.");
        }

        if (options.LockDuration <= TimeSpan.Zero)
        {
            errors.Add("LockDuration must be positive.");
        }

        if (options.CeremonyTtl <= TimeSpan.Zero)
        {
            errors.Add("CeremonyTtl must be positive.");
        }

        if (options.RecoveryCodeCount < 1)
        {
            errors.Add("RecoveryCodeCount must be at least 1.");
        }

        ValidateRule("Policy.Default", options.Policy.Default, errors);
        foreach (var (role, rule) in options.Policy.Roles)
        {
            ValidateRule($"Policy.Roles[{role}]", rule, errors);
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    // A rule that offers nothing, or demands less than one method, can never be satisfied or means nothing.
    private static void ValidateRule(string name, MfaRule rule, List<string> errors)
    {
        if (rule.OfferedKinds.Count == 0)
        {
            errors.Add($"{name}: OfferedKinds must not be empty.");
        }

        if (rule.MinimumEnrolled < 1)
        {
            errors.Add($"{name}: MinimumEnrolled must be at least 1.");
        }

        if (rule.SpareGrace < TimeSpan.Zero)
        {
            errors.Add($"{name}: SpareGrace must not be negative.");
        }
    }
}
