using Fido2NetLib;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.SecondFactor;
using Xunit;

namespace Themia.Modules.Identity.SecondFactor.Tests;

public sealed class PolicyAndSessionTests
{
    private readonly FakeService _service = new();
    private readonly FakeProofStore _store = new();
    private readonly IRefreshTokenService _tokens;
    private readonly FakeSessionLiveness _liveness;

    public PolicyAndSessionTests() => (_tokens, _liveness) = FakeSessionLiveness.Create();

    private static SecondFactorContext Context(SecondFactorMethod method, string? provider = null) =>
        new(Samples.UserId, null, method, provider);

    [Theory]
    [InlineData(MfaRequirementLevel.NotRequired, SecondFactorRequirement.NotRequired)]
    [InlineData(MfaRequirementLevel.Verify, SecondFactorRequirement.Verify)]
    [InlineData(MfaRequirementLevel.Enrol, SecondFactorRequirement.Enrol)]
    [InlineData(MfaRequirementLevel.Denied, SecondFactorRequirement.Denied)]
    public async Task Policy_ShouldMapEveryLevel(MfaRequirementLevel level, SecondFactorRequirement expected)
    {
        _service.Requirement = Samples.Requirement(level);

        var actual = await new MfaSecondFactorPolicy(_service, new FakeSubjects(Samples.Subject)).EvaluateAsync(Context(SecondFactorMethod.Password));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Policy_ShouldDeny_WhenTheUserCannotBeResolved()
    {
        var actual = await new MfaSecondFactorPolicy(_service, new FakeSubjects(null)).EvaluateAsync(Context(SecondFactorMethod.Password));

        Assert.Equal(SecondFactorRequirement.Denied, actual);
    }

    [Theory]
    [InlineData(SecondFactorMethod.Password, null, MfaPrimaryKind.Password)]
    [InlineData(SecondFactorMethod.Otp, null, MfaPrimaryKind.External)]
    [InlineData(SecondFactorMethod.External, "line", MfaPrimaryKind.External)]
    [InlineData(SecondFactorMethod.Custom, "sso", MfaPrimaryKind.External)]
    public async Task Policy_ShouldMapThePrimary(SecondFactorMethod method, string? provider, MfaPrimaryKind kind)
    {
        await new MfaSecondFactorPolicy(_service, new FakeSubjects(Samples.Subject)).EvaluateAsync(Context(method, provider));

        Assert.Equal(new MfaPrimary(kind, provider), _service.SeenPrimary);
    }

    [Fact]
    public async Task Reader_ShouldReturnNull_WhenTheSessionHasNoRow()
    {
        Assert.Null(await new SessionProofReader(_store).GetAsync(Samples.UserId, Samples.SessionId));
    }

    [Theory]
    [InlineData(SessionRestriction.None, false)]
    [InlineData(SessionRestriction.RecoveryOnly, true)]
    [InlineData(SessionRestriction.UpgradeDue, true)]
    public async Task Reader_ShouldTreatEveryRestrictionAsEnrolOnly(SessionRestriction restriction, bool expected)
    {
        _store.Snapshot = new SessionProofSnapshot("otp", SessionProofStrength.Strong, Samples.Now, restriction);

        var proof = await new SessionProofReader(_store).GetAsync(Samples.UserId, Samples.SessionId);

        Assert.Equal(expected, proof?.RecoveryCodeOnly);
    }

    private SessionSecondFactorEnrolment Enrolment() =>
        new(_service, new FakeSubjects(Samples.Subject), new SessionProofReader(_store), _store, _tokens);

    [Fact]
    public async Task Enrol_ShouldLiftTheRestriction_WhenAStrongMethodWasConfirmed()
    {
        _store.Snapshot = new SessionProofSnapshot("otp", SessionProofStrength.Weak, Samples.Now, SessionRestriction.RecoveryOnly);

        var result = await Enrolment().ConfirmTotpEnrolAsync(Samples.UserId, Samples.SessionId, "e1", "123456", "phone");

        Assert.True(result.Succeeded);
        Assert.Equal(1, _store.Lifts);
        Assert.True(_service.SeenProof?.RecoveryCodeOnly);
    }

    [Fact]
    public async Task Enrol_ShouldKeepTheRestriction_WhenTheEnrolmentFailed()
    {
        _service.Result = new SecondFactorResult { Outcome = SecondFactorOutcome.Incorrect };

        await Enrolment().ConfirmTotpEnrolAsync(Samples.UserId, Samples.SessionId, "e1", "000000", "phone");

        Assert.Equal(0, _store.Lifts);
    }

    [Fact]
    public async Task Enrol_ShouldKeepTheRestriction_WhenTheEnrolledMethodIsWeak()
    {
        _service.Result = Samples.Verified(MfaStrength.Weak);

        await Enrolment().ConfirmTotpEnrolAsync(Samples.UserId, Samples.SessionId, "e1", "123456", "phone");

        Assert.Equal(0, _store.Lifts);
    }

    [Fact]
    public async Task Enrol_ShouldPassNoProof_WhenTheSessionHasNoRow()
    {
        await Enrolment().ConfirmTotpEnrolAsync(Samples.UserId, Samples.SessionId, "e1", "123456", "phone");

        Assert.Equal(1, _service.EnrolConfirmCalls);
        Assert.Null(_service.SeenProof);
    }

    // A revoked session (admin or break-glass reset, expiry, another user's) must not enrol a factor, whatever its proof row says.
    [Fact]
    public async Task Enrol_ShouldRefuseTotp_WhenTheSessionIsNotLive()
    {
        _liveness.Live = false;

        var result = await Enrolment().ConfirmTotpEnrolAsync(Samples.UserId, Samples.SessionId, "e1", "123456", "phone");

        Assert.Equal(SecondFactorOutcome.NotAvailable, result.Outcome);
        Assert.Equal(0, _service.EnrolConfirmCalls);
        Assert.Equal(0, _store.Lifts);
    }

    [Fact]
    public async Task Enrol_ShouldRefusePasskey_WhenTheSessionIsNotLive()
    {
        _liveness.Live = false;

        var result = await Enrolment().CompletePasskeyEnrolAsync(Samples.UserId, Samples.SessionId, new AuthenticatorAttestationRawResponse(), "key");

        Assert.Equal(SecondFactorOutcome.NotAvailable, result.Outcome);
        Assert.Equal(0, _service.EnrolConfirmCalls);
        Assert.Equal(0, _store.Lifts);
    }

    [Fact]
    public async Task Enrol_ShouldRefuse_WhenTheSessionWasLiveAndIsThenRevoked()
    {
        _store.Snapshot = new SessionProofSnapshot("otp", SessionProofStrength.Weak, Samples.Now, SessionRestriction.RecoveryOnly);
        var enrolment = Enrolment();
        Assert.True((await enrolment.ConfirmTotpEnrolAsync(Samples.UserId, Samples.SessionId, "e1", "123456", "phone")).Succeeded);

        _liveness.Live = false;
        var afterReset = await enrolment.ConfirmTotpEnrolAsync(Samples.UserId, Samples.SessionId, "e2", "123456", "phone");

        Assert.Equal(SecondFactorOutcome.NotAvailable, afterReset.Outcome);
        Assert.Equal(1, _service.EnrolConfirmCalls);
    }
}
