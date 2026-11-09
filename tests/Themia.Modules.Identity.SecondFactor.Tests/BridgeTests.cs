using Fido2NetLib;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.SecondFactor;
using Xunit;

namespace Themia.Modules.Identity.SecondFactor.Tests;

public sealed class BridgeTests
{
    private readonly FakeService _service = new();
    private readonly FakeCompletion _completion = new();

    private SecondFactorBridge Bridge(SecondFactorTicketData? ticket, MfaSubject? subject = null) => Bridge(ticket, new FakeSubjects(subject ?? Samples.Subject));

    private SecondFactorBridge Bridge(SecondFactorTicketData? ticket, FakeSubjects subjects) =>
        new(_service, subjects, new FakeTickets(ticket), _completion);

    [Fact]
    public async Task VerifyTotp_ShouldCompleteWithTheProof_WhenTheCodeIsRight()
    {
        var result = await Bridge(Samples.Ticket(SecondFactorStage.Verify)).VerifyTotpAsync(Samples.Pending(SecondFactorStage.Verify), Guid.NewGuid(), "123456");

        Assert.True(result.Succeeded);
        var proof = Assert.Single(_completion.Completed);
        Assert.Equal(SessionProofStrength.Strong, proof.Strength);
        Assert.Equal(SessionRestriction.None, proof.Restriction);
    }

    [Fact]
    public async Task VerifyTotp_ShouldIssueNothing_WhenTheCodeIsWrong()
    {
        _service.Result = new SecondFactorResult { Outcome = SecondFactorOutcome.Incorrect };

        var result = await Bridge(Samples.Ticket(SecondFactorStage.Verify)).VerifyTotpAsync(Samples.Pending(SecondFactorStage.Verify), Guid.NewGuid(), "000000");

        Assert.False(result.Succeeded);
        Assert.Equal(SecondFactorOutcome.Incorrect, result.Verification?.Outcome);
        Assert.Empty(_completion.Completed);
    }

    [Fact]
    public async Task VerifyTotp_ShouldBeRefused_WhenThePendingStageIsEnrol()
    {
        var result = await Bridge(Samples.Ticket(SecondFactorStage.Enrol)).VerifyTotpAsync(Samples.Pending(SecondFactorStage.Enrol), Guid.NewGuid(), "123456");

        Assert.False(result.Succeeded);
        Assert.Null(result.Verification);
        Assert.Empty(_completion.Completed);
    }

    [Fact]
    public async Task VerifyTotp_ShouldBeRefused_WhenTheTicketStageDiffersFromTheCallersClaim()
    {
        var result = await Bridge(Samples.Ticket(SecondFactorStage.Enrol)).VerifyTotpAsync(Samples.Pending(SecondFactorStage.Verify), Guid.NewGuid(), "123456");

        Assert.Null(result.Verification);
        Assert.Empty(_completion.Completed);
    }

    [Fact]
    public async Task VerifyTotp_ShouldBeRefused_WhenTheTicketIsUnreadable()
    {
        var result = await Bridge(null).VerifyTotpAsync(Samples.Pending(SecondFactorStage.Verify), Guid.NewGuid(), "123456");

        Assert.Null(result.Verification);
        Assert.Empty(_completion.Completed);
    }

    [Fact]
    public async Task VerifyTotp_ShouldBeRefused_WhenTheUserIsGone()
    {
        var bridge = new SecondFactorBridge(_service, new FakeSubjects(null), new FakeTickets(Samples.Ticket(SecondFactorStage.Verify)), _completion);

        var result = await bridge.VerifyTotpAsync(Samples.Pending(SecondFactorStage.Verify), Guid.NewGuid(), "123456");

        Assert.Null(result.Verification);
    }

    [Fact]
    public async Task VerifyTotp_ShouldBeRefusedBeforeAnyProofRuns_WhenTheSecurityStampChanged()
    {
        var bridge = new SecondFactorBridge(_service, new FakeSubjects(Samples.Subject, "newer"), new FakeTickets(Samples.Ticket(SecondFactorStage.Verify)), _completion);

        var result = await bridge.VerifyTotpAsync(Samples.Pending(SecondFactorStage.Verify), Guid.NewGuid(), "123456");

        Assert.Null(result.Verification);
        Assert.Null(_service.SeenPrimary);
        Assert.Empty(_completion.Completed);
    }

    [Fact]
    public async Task Verify_ShouldTakeThePrimaryFromTheTicket_NotFromTheCaller()
    {
        await Bridge(Samples.Ticket(SecondFactorStage.Verify, SecondFactorMethod.External, "line")).VerifyTotpAsync(Samples.Pending(SecondFactorStage.Verify), Guid.NewGuid(), "123456");

        Assert.Equal(new MfaPrimary(MfaPrimaryKind.External, "line"), _service.SeenPrimary);
    }

    [Fact]
    public async Task VerifyRecoveryCode_ShouldOpenARecoveryOnlySession()
    {
        _service.Result = Samples.Verified(MfaStrength.Strong, recovery: true);

        await Bridge(Samples.Ticket(SecondFactorStage.Verify)).VerifyRecoveryCodeAsync(Samples.Pending(SecondFactorStage.Verify), "abcd-efgh");

        Assert.Equal(SessionRestriction.RecoveryOnly, Assert.Single(_completion.Completed).Restriction);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Verify_ShouldMarkTheSessionUpgradeDue_WhenAnUpgradeOrSpareIsMandatory(bool upgrade, bool spare)
    {
        _service.Requirement = Samples.Requirement(MfaRequirementLevel.Verify, upgrade, spare);

        await Bridge(Samples.Ticket(SecondFactorStage.Verify)).VerifyTotpAsync(Samples.Pending(SecondFactorStage.Verify), Guid.NewGuid(), "123456");

        Assert.Equal(SessionRestriction.UpgradeDue, Assert.Single(_completion.Completed).Restriction);
    }

    [Fact]
    public async Task ConfirmTotpEnrol_ShouldCompleteUnrestricted_ForAnEnrolTicket()
    {
        _service.Requirement = Samples.Requirement(MfaRequirementLevel.Enrol, upgrade: true);

        var result = await Bridge(Samples.Ticket(SecondFactorStage.Enrol)).ConfirmTotpEnrolAsync(Samples.Pending(SecondFactorStage.Enrol), "e1", "123456", "phone");

        Assert.True(result.Succeeded);
        Assert.Equal(SessionRestriction.None, Assert.Single(_completion.Completed).Restriction);
    }

    [Fact]
    public async Task TheBridge_ShouldNotExposeAnOtpEnrolMethod_BecauseEnrollingACodeProvesNothing()
    {
        var names = typeof(ISecondFactorBridge).GetMethods().Select(m => m.Name);

        Assert.DoesNotContain(names, n => n.Contains("EnrolOtp", StringComparison.Ordinal));
        Assert.Equal(0, _service.EnrolOtpCalls);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task VerifyRecoveryCode_ShouldStoreAWeakProof_WhenTheServiceReportsTheCodeAsStrong()
    {
        // The real service reports a recovery code as Strong (SecondFactorService); the stored proof must not be.
        _service.Result = Samples.Verified(MfaStrength.Strong, recovery: true);

        await Bridge(Samples.Ticket(SecondFactorStage.Verify)).VerifyRecoveryCodeAsync(Samples.Pending(SecondFactorStage.Verify), "abcd-efgh");

        var proof = Assert.Single(_completion.Completed);
        Assert.Equal(SessionProofStrength.Weak, proof.Strength);
        Assert.Equal(SessionRestriction.RecoveryOnly, proof.Restriction);
    }

    [Fact]
    public async Task VerifyTotp_ShouldBeRefusedBeforeAnyProofRuns_WhenTheTicketIsForAnAppChannel()
    {
        var result = await Bridge(Samples.Ticket(SecondFactorStage.Verify, SecondFactorMethod.Custom, "line"))
            .VerifyTotpAsync(Samples.Pending(SecondFactorStage.Verify), Guid.NewGuid(), "123456");

        Assert.False(result.Succeeded);
        Assert.Null(result.Verification);
        Assert.Null(_service.SeenPrimary);
        Assert.Empty(_completion.Completed);
    }

    [Fact]
    public async Task VerifyTotp_ShouldRunAsTheTicketsTenant()
    {
        var subjects = new FakeSubjects(Samples.Subject);
        var tenant = TenantId.From("t1");

        await Bridge(Samples.Ticket(SecondFactorStage.Verify, tenant: tenant), subjects)
            .VerifyTotpAsync(Samples.Pending(SecondFactorStage.Verify), Guid.NewGuid(), "123456");

        Assert.NotEmpty(subjects.TenantsSeen);
        Assert.All(subjects.TenantsSeen, seen => Assert.Equal(tenant, seen));
    }
}
