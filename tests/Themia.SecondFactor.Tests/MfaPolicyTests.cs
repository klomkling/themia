using Themia.SecondFactor;

using Xunit;

namespace Themia.SecondFactor.Tests;

public sealed class MfaPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly MfaSubject Staff = new("u1", null, new HashSet<string> { "staff" });
    private static readonly MfaPrimary Password = new(MfaPrimaryKind.Password);
    private static readonly MfaPrimary Line = new(MfaPrimaryKind.External, "line");

    private static MfaMethod Method(MfaMethodKind kind, bool disabled = false) =>
        new(Guid.NewGuid(), kind, "label", Now.AddDays(-30), disabled ? Now.AddDays(-1) : null);

    private static MfaPolicy Policy(Action<MfaPolicyOptions>? configure = null)
    {
        var options = new MfaPolicyOptions();
        configure?.Invoke(options);
        return new MfaPolicy(options);
    }

    [Fact]
    public void Evaluate_ShouldBeNotRequired_WhenOptionalAndNothingEnrolled()
    {
        var result = Policy().Evaluate(Staff, Password, [], null, Now);

        Assert.Equal(MfaRequirementLevel.NotRequired, result.Level);
    }

    [Fact]
    public void Evaluate_ShouldVerify_WhenOptionalButUserEnrolled()
    {
        var result = Policy().Evaluate(Staff, Password, [Method(MfaMethodKind.Totp)], null, Now);

        Assert.Equal(MfaRequirementLevel.Verify, result.Level);
    }

    [Fact]
    public void Evaluate_ShouldEnrol_WhenRequiredAndNoUsableMethod()
    {
        var policy = Policy(o => o.Default = MfaRule.Mandatory());

        var result = policy.Evaluate(Staff, Password, [], null, Now);

        Assert.Equal(MfaRequirementLevel.Enrol, result.Level);
        Assert.Contains(MfaMethodKind.Passkey, result.EnrolKinds);
    }

    [Fact]
    public void Evaluate_ShouldVerifyNotEnrol_WhenOneMethodButMinimumIsTwo()
    {
        // Enrol here would let someone holding only the password add their own factor.
        var policy = Policy(o => o.Default = MfaRule.Mandatory(minimumEnrolled: 2));

        var result = policy.Evaluate(Staff, Password, [Method(MfaMethodKind.Totp)], null, Now);

        Assert.Equal(MfaRequirementLevel.Verify, result.Level);
        Assert.True(result.SpareNeeded);
    }

    [Fact]
    public void Evaluate_ShouldIgnoreDisabledMethods()
    {
        var policy = Policy(o => o.Default = MfaRule.Mandatory());

        var result = policy.Evaluate(Staff, Password, [Method(MfaMethodKind.Totp, disabled: true)], null, Now);

        Assert.Equal(MfaRequirementLevel.Enrol, result.Level);
    }

    [Fact]
    public void Evaluate_ShouldVerifyHeldWeakMethodThenUpgrade_WhenPrimaryIsExternal()
    {
        // Enrol here would let someone holding only the LINE account add their own TOTP and skip the SMS factor.
        var policy = Policy(o => o.Default = MfaRule.Mandatory());

        var result = policy.Evaluate(Staff, Line, [Method(MfaMethodKind.SmsOtp)], null, Now);

        Assert.Equal(MfaRequirementLevel.Verify, result.Level);
        Assert.True(result.UpgradeMandatory);
        Assert.Single(result.UsableMethods);
        Assert.DoesNotContain(MfaMethodKind.SmsOtp, result.EnrolKinds);
        Assert.DoesNotContain(MfaMethodKind.EmailOtp, result.EnrolKinds);
    }

    [Fact]
    public void Evaluate_ShouldEnrolOnlyStrongKinds_WhenPrimaryIsExternalAndNothingHeld()
    {
        var policy = Policy(o => o.Default = MfaRule.Mandatory());

        var result = policy.Evaluate(Staff, Line, [], null, Now);

        Assert.Equal(MfaRequirementLevel.Enrol, result.Level);
        Assert.All(result.EnrolKinds, kind => Assert.Equal(MfaStrength.Strong, kind.Strength()));
    }

    [Fact]
    public void Evaluate_ShouldNotOfferEmailOtp_WhenPrimaryIsEmailLink()
    {
        var policy = Policy(o => o.Default = MfaRule.Mandatory());

        var result = policy.Evaluate(Staff, new MfaPrimary(MfaPrimaryKind.EmailLink), [], null, Now);

        Assert.DoesNotContain(MfaMethodKind.EmailOtp, result.EnrolKinds);
        Assert.Contains(MfaMethodKind.SmsOtp, result.EnrolKinds);
    }

    [Fact]
    public void Evaluate_ShouldOnlyOfferConfiguredKindsForEnrolment()
    {
        var policy = Policy(o => o.Default = MfaRule.Mandatory(offered: [MfaMethodKind.Passkey, MfaMethodKind.Totp]));

        var result = policy.Evaluate(Staff, Password, [], null, Now);

        Assert.Equal(MfaRequirementLevel.Enrol, result.Level);
        Assert.Equal([MfaMethodKind.Passkey, MfaMethodKind.Totp], result.EnrolKinds.OrderBy(k => k));
    }

    [Fact]
    public void Evaluate_ShouldVerifyThenUpgrade_WhenHeldMethodIsNoLongerOffered()
    {
        var policy = Policy(o => o.Default = MfaRule.Mandatory(offered: [MfaMethodKind.Passkey, MfaMethodKind.Totp]));

        var result = policy.Evaluate(Staff, Password, [Method(MfaMethodKind.SmsOtp)], null, Now);

        Assert.Equal(MfaRequirementLevel.Verify, result.Level);
        Assert.True(result.UpgradeMandatory);
        Assert.Equal(MfaMethodKind.SmsOtp, result.UsableMethods.Single().Kind);
    }

    [Fact]
    public void Evaluate_ShouldStillVerify_WhenRuleIsOptionalButUserHoldsAnUnofferedMethod()
    {
        var narrowed = Policy(o => o.Default = new MfaRule { OfferedKinds = new HashSet<MfaMethodKind> { MfaMethodKind.Passkey } });

        var result = narrowed.Evaluate(Staff, Password, [Method(MfaMethodKind.Totp)], null, Now);

        Assert.Equal(MfaRequirementLevel.Verify, result.Level);
    }

    [Fact]
    public void Evaluate_ShouldDeny_WhenOnlyHeldMethodSharesTheEmailPrimaryChannel()
    {
        var policy = Policy(o => o.Default = MfaRule.Mandatory());

        var result = policy.Evaluate(Staff, new MfaPrimary(MfaPrimaryKind.EmailLink), [Method(MfaMethodKind.EmailOtp)], null, Now);

        Assert.Equal(MfaRequirementLevel.Denied, result.Level);
        Assert.Empty(result.UsableMethods);
    }

    [Fact]
    public void Evaluate_ShouldUseStrictestRule_WhenRolesDiffer()
    {
        var policy = Policy(o =>
        {
            o.Roles["admin"] = MfaRule.Mandatory(offered: [MfaMethodKind.Passkey, MfaMethodKind.Totp]);
        });
        var admin = new MfaSubject("u2", null, new HashSet<string> { "staff", "admin" });

        var result = policy.Evaluate(admin, Password, [], null, Now);

        Assert.Equal(MfaRequirementLevel.Enrol, result.Level);
        Assert.DoesNotContain(MfaMethodKind.SmsOtp, result.EnrolKinds);
    }

    [Fact]
    public void Evaluate_ShouldRequestNagStart_WhenSpareMissingAndGraceNotStarted()
    {
        var policy = Policy(o => o.Default = MfaRule.Mandatory(minimumEnrolled: 2));

        var result = policy.Evaluate(Staff, Password, [Method(MfaMethodKind.Totp)], null, Now);

        Assert.True(result.SpareNagShouldStart);
        Assert.False(result.SpareMandatory);
    }

    [Fact]
    public void Evaluate_ShouldMakeSpareMandatory_WhenGraceExpired()
    {
        var policy = Policy(o => o.Default = MfaRule.Mandatory(minimumEnrolled: 2));

        var result = policy.Evaluate(Staff, Password, [Method(MfaMethodKind.Totp)], Now.AddDays(-14), Now);

        Assert.True(result.SpareMandatory);
    }

    [Fact]
    public void Evaluate_ShouldKeepSparePending_WhenInsideGrace()
    {
        var policy = Policy(o => o.Default = MfaRule.Mandatory(minimumEnrolled: 2));

        var result = policy.Evaluate(Staff, Password, [Method(MfaMethodKind.Totp)], Now.AddDays(-13), Now);

        Assert.True(result.SpareNeeded);
        Assert.False(result.SpareMandatory);
        Assert.False(result.SpareNagShouldStart);
    }

    [Fact]
    public void Evaluate_ShouldMakeSpareMandatoryImmediately_WhenGraceIsZero()
    {
        var policy = Policy(o => o.Roles["admin"] = MfaRule.Mandatory(minimumEnrolled: 2, spareGrace: TimeSpan.Zero));
        var admin = new MfaSubject("u2", null, new HashSet<string> { "admin" });

        var result = policy.Evaluate(admin, Password, [Method(MfaMethodKind.Totp)], null, Now);

        Assert.True(result.SpareMandatory);
    }

    [Fact]
    public void Evaluate_ShouldNotNeedSpare_WhenMinimumSatisfied()
    {
        var policy = Policy(o => o.Default = MfaRule.Mandatory(minimumEnrolled: 2));

        var result = policy.Evaluate(Staff, Password, [Method(MfaMethodKind.Totp), Method(MfaMethodKind.Passkey)], null, Now);

        Assert.False(result.SpareNeeded);
    }

    [Fact]
    public void Strength_ShouldBeStrongForPasskeyAndTotp_AndWeakForOtp()
    {
        Assert.Equal(MfaStrength.Strong, MfaMethodKind.Passkey.Strength());
        Assert.Equal(MfaStrength.Strong, MfaMethodKind.Totp.Strength());
        Assert.Equal(MfaStrength.Weak, MfaMethodKind.EmailOtp.Strength());
        Assert.Equal(MfaStrength.Weak, MfaMethodKind.SmsOtp.Strength());
    }
}
