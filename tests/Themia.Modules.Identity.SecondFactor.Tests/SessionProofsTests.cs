using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.SecondFactor;
using Xunit;

namespace Themia.Modules.Identity.SecondFactor.Tests;

public sealed class SessionProofsTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-01T08:30:00Z");

    private static SecondFactorResult Verified(MfaStrength strength, bool recovery = false, DateTimeOffset? at = null) => new()
    {
        Outcome = SecondFactorOutcome.Succeeded,
        Strength = strength,
        UsedRecoveryCode = recovery,
        AuthenticatedAt = at ?? At,
        Amr = ["otp", "mfa"],
    };

    [Fact]
    public void From_keeps_amr_time_and_restriction_of_a_strong_verification()
    {
        var snapshot = SessionProofs.From(Verified(MfaStrength.Strong), SessionRestriction.UpgradeDue);

        Assert.Equal(new SessionProofSnapshot("otp mfa", SessionProofStrength.Strong, At, SessionRestriction.UpgradeDue), snapshot);
    }

    [Fact]
    public void From_never_reads_a_recovery_code_as_strong_even_when_the_service_reports_it_strong()
    {
        var snapshot = SessionProofs.From(Verified(MfaStrength.Strong, recovery: true), SessionRestriction.RecoveryOnly);

        Assert.Equal(SessionProofStrength.Weak, snapshot.Strength);
    }

    [Fact]
    public void From_reads_a_weak_verification_as_weak()
    {
        Assert.Equal(SessionProofStrength.Weak, SessionProofs.From(Verified(MfaStrength.Weak), SessionRestriction.None).Strength);
    }

    [Fact]
    public void From_refuses_a_result_that_carries_no_time()
    {
        var failed = new SecondFactorResult { Outcome = SecondFactorOutcome.NotAvailable };

        Assert.Throws<InvalidOperationException>(() => SessionProofs.From(failed, SessionRestriction.None));
    }
}
