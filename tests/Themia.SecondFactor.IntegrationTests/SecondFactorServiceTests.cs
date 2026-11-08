using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Themia.Totp;

using Xunit;

namespace Themia.SecondFactor.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SecondFactorServiceTests : IAsyncLifetime
{
    private static readonly MfaPrimary Password = new(MfaPrimaryKind.Password);
    private static readonly MfaPrimary Line = new(MfaPrimaryKind.External, "line");
    private const string Email = "sara@example.com";
    private const string Phone = "+66800000000";

    private readonly PostgresFixture fixture;
    private ServiceHarness harness = null!;

    public SecondFactorServiceTests(PostgresFixture fixture) => this.fixture = fixture;

    public Task InitializeAsync()
    {
        harness = new ServiceHarness(fixture.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    private MfaSubject NewSubject(string? tenant = null)
    {
        var subject = ServiceHarness.NewSubject(tenant);
        harness.Contacts.Set(subject, new MfaContact(Email, Email, Phone));
        return subject;
    }

    private MfaProof StrongNow() => new(MfaStrength.Strong, harness.Clock.GetUtcNow());

    private async Task<(Guid MethodId, string Secret)> EnrolFirstTotpAsync(MfaSubject subject)
    {
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecondFactorService>();
        var totp = scope.ServiceProvider.GetRequiredService<ITotpService>();
        var begin = await service.BeginTotpEnrolAsync(subject, Password, null);
        Assert.True(begin.Succeeded);
        var confirm = await service.ConfirmTotpEnrolAsync(subject, begin.Value!.EnrolmentId, totp.GenerateCode(begin.Value.Secret), "Authenticator");
        Assert.True(confirm.Succeeded);
        var methodId = (await service.ListMethodsAsync(subject)).Single().Id;
        return (methodId, begin.Value.Secret);
    }

    private string CurrentCode(string secret)
    {
        using var scope = harness.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ITotpService>().GenerateCode(secret);
    }

    [Fact]
    public async Task BeginTotpEnrol_ShouldReturnQrSvgAndLabelTheAccountWithTheEmail_NotTheUserId()
    {
        var subject = NewSubject();
        using var scope = harness.CreateScope();

        var begin = await scope.ServiceProvider.GetRequiredService<ISecondFactorService>().BeginTotpEnrolAsync(subject, Password, null);

        Assert.True(begin.Succeeded);
        Assert.StartsWith("<svg", begin.Value!.QrSvg, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(Email), begin.Value.OtpAuthUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain(subject.UserId, begin.Value.OtpAuthUri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains($"secret={begin.Value.Secret}", begin.Value.OtpAuthUri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BeginTotpEnrol_ShouldRefuse_WhenTheOnlyLabelIsTheUserId()
    {
        var subject = ServiceHarness.NewSubject();
        harness.Contacts.Set(subject, new MfaContact(subject.UserId, null, null));
        using var scope = harness.CreateScope();

        var begin = await scope.ServiceProvider.GetRequiredService<ISecondFactorService>().BeginTotpEnrolAsync(subject, Password, null);

        Assert.Equal(SecondFactorOutcome.NotAllowed, begin.Outcome);
    }

    [Fact]
    public async Task ConfirmTotpEnrol_ShouldSpendTheConfirmingCode_SoItCannotSignIn()
    {
        var subject = NewSubject();
        var (methodId, secret) = await EnrolFirstTotpAsync(subject);
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecondFactorService>();

        var replay = await service.VerifyTotpAsync(subject, Password, methodId, CurrentCode(secret));
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        var next = await service.VerifyTotpAsync(subject, Password, methodId, CurrentCode(secret));

        Assert.Equal(SecondFactorOutcome.Replayed, replay.Outcome);
        Assert.True(next.Succeeded);
        Assert.Equal(MfaStrength.Strong, next.Strength);
        Assert.Contains(AmrClaims.Mfa, next.Amr);
    }

    [Fact]
    public async Task ConfirmTotpEnrol_ShouldKeepThePendingEnrolment_WhenTheCodeIsATypo()
    {
        var subject = NewSubject();
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecondFactorService>();
        var totp = scope.ServiceProvider.GetRequiredService<ITotpService>();
        var begin = await service.BeginTotpEnrolAsync(subject, Password, null);

        var typo = await service.ConfirmTotpEnrolAsync(subject, begin.Value!.EnrolmentId, "000000", "Authenticator");
        var retry = await service.ConfirmTotpEnrolAsync(subject, begin.Value.EnrolmentId, totp.GenerateCode(begin.Value.Secret), "Authenticator");

        Assert.Equal(SecondFactorOutcome.Incorrect, typo.Outcome);
        Assert.True(retry.Succeeded);
    }

    [Fact]
    public async Task ConfirmTotpEnrol_ShouldRefuseAnotherUsersEnrolment()
    {
        var owner = NewSubject();
        var other = NewSubject();
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecondFactorService>();
        var totp = scope.ServiceProvider.GetRequiredService<ITotpService>();
        var begin = await service.BeginTotpEnrolAsync(owner, Password, null);

        var stolen = await service.ConfirmTotpEnrolAsync(other, begin.Value!.EnrolmentId, totp.GenerateCode(begin.Value.Secret), "Mine");

        Assert.Equal(SecondFactorOutcome.CeremonyExpired, stolen.Outcome);
        Assert.Empty(await service.ListMethodsAsync(other));
    }

    [Fact]
    public async Task Enrol_ShouldNeedAFreshStrongProof_WhenTheUserAlreadyHoldsAMethod()
    {
        var subject = NewSubject();
        await EnrolFirstTotpAsync(subject);
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecondFactorService>();

        var none = await service.BeginTotpEnrolAsync(subject, Password, null);
        var weak = await service.BeginTotpEnrolAsync(subject, Password, new MfaProof(MfaStrength.Weak, harness.Clock.GetUtcNow()));
        var stale = await service.BeginTotpEnrolAsync(subject, Password, new MfaProof(MfaStrength.Strong, harness.Clock.GetUtcNow().AddMinutes(-6)));
        var fresh = await service.BeginTotpEnrolAsync(subject, Password, StrongNow());

        Assert.Equal(SecondFactorOutcome.StepUpRequired, none.Outcome);
        Assert.Equal(SecondFactorOutcome.StepUpRequired, weak.Outcome);
        Assert.Equal(SecondFactorOutcome.StepUpRequired, stale.Outcome);
        Assert.True(fresh.Succeeded);
    }

    [Fact]
    public async Task Enrol_ShouldLetAWeakOnlyUserAddAStrongMethod_AfterAFreshWeakProof()
    {
        var subject = NewSubject();
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecondFactorService>();
        Assert.True((await service.EnrolOtpAsync(subject, Password, MfaMethodKind.SmsOtp, null)).Succeeded);

        var upgrade = await service.BeginTotpEnrolAsync(subject, Password, new MfaProof(MfaStrength.Weak, harness.Clock.GetUtcNow()));
        var secondWeak = await service.EnrolOtpAsync(subject, Password, MfaMethodKind.EmailOtp, new MfaProof(MfaStrength.Weak, harness.Clock.GetUtcNow()));

        Assert.True(upgrade.Succeeded);
        Assert.Equal(SecondFactorOutcome.StepUpRequired, secondWeak.Outcome);
    }

    [Fact]
    public async Task Evaluate_ShouldAnswerVerifyNotEnrol_WhenTheUserHoldsOneMethodAndMinimumIsTwo()
    {
        var subject = NewSubject();
        await EnrolFirstTotpAsync(subject);
        await using var strict = new ServiceHarness(fixture.ConnectionString, o => o.Policy.Default = MfaRule.Mandatory(minimumEnrolled: 2));
        strict.Contacts.Set(subject, new MfaContact(Email, Email, Phone));
        using var scope = strict.CreateScope();

        var requirement = await scope.ServiceProvider.GetRequiredService<ISecondFactorService>().EvaluateAsync(subject, Password);
        var enrol = await scope.ServiceProvider.GetRequiredService<ISecondFactorService>().BeginTotpEnrolAsync(subject, Password, null);

        Assert.Equal(MfaRequirementLevel.Verify, requirement.Level);
        Assert.True(requirement.SpareNeeded);
        Assert.Equal(SecondFactorOutcome.StepUpRequired, enrol.Outcome);
    }

    [Fact]
    public async Task Remove_ShouldNeedStrongProof_AndRefuseTheLastMethodOfARequiredUser()
    {
        var subject = NewSubject();
        var (methodId, _) = await EnrolFirstTotpAsync(subject);
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecondFactorService>();

        var noProof = await service.RemoveMethodAsync(subject, Password, methodId, null);
        var last = await service.RemoveMethodAsync(subject, Password, methodId, StrongNow());

        Assert.Equal(SecondFactorOutcome.StepUpRequired, noProof.Outcome);
        Assert.Equal(SecondFactorOutcome.NotAllowed, last.Outcome);
        Assert.Single(await service.ListMethodsAsync(subject));
    }

    [Fact]
    public async Task Remove_ShouldWork_WhenAnotherMethodRemains()
    {
        var subject = NewSubject();
        var (methodId, _) = await EnrolFirstTotpAsync(subject);
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecondFactorService>();
        Assert.True((await service.EnrolOtpAsync(subject, Password, MfaMethodKind.SmsOtp, StrongNow())).Succeeded);

        var removed = await service.RemoveMethodAsync(subject, Password, methodId, StrongNow());

        Assert.True(removed.Succeeded);
        Assert.Equal(MfaMethodKind.SmsOtp, (await service.ListMethodsAsync(subject)).Single().Kind);
    }

    [Fact]
    public async Task RecoveryCodes_ShouldNeedStrongProof_AndBeSingleUse()
    {
        var subject = NewSubject();
        await EnrolFirstTotpAsync(subject);
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecondFactorService>();

        var refused = await service.GenerateRecoveryCodesAsync(subject, null);
        var minted = await service.GenerateRecoveryCodesAsync(subject, StrongNow());
        var first = await service.VerifyRecoveryCodeAsync(subject, Password, minted.Codes[0]);
        var again = await service.VerifyRecoveryCodeAsync(subject, Password, minted.Codes[0]);

        Assert.Equal(SecondFactorOutcome.StepUpRequired, refused.Outcome);
        Assert.Equal(10, minted.Codes.Count);
        Assert.True(first.Succeeded);
        Assert.True(first.UsedRecoveryCode);
        Assert.Contains(AmrClaims.Mfa, first.Amr);
        Assert.Equal(SecondFactorOutcome.Incorrect, again.Outcome);
    }

    [Fact]
    public async Task RecoveryCodes_ShouldNotWorkForAnotherUser()
    {
        var owner = NewSubject();
        var other = NewSubject();
        await EnrolFirstTotpAsync(owner);
        await EnrolFirstTotpAsync(other);
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecondFactorService>();
        var minted = await service.GenerateRecoveryCodesAsync(owner, StrongNow());

        var stolen = await service.VerifyRecoveryCodeAsync(other, Password, minted.Codes[0]);

        Assert.Equal(SecondFactorOutcome.Incorrect, stolen.Outcome);
    }

    [Fact]
    public async Task Verify_ShouldAllowExactlyMaxAttempts_WhenGuessedInParallel_ThenLockEvenTheRightCode()
    {
        var subject = NewSubject();
        var (methodId, secret) = await EnrolFirstTotpAsync(subject);
        harness.Clock.Advance(TimeSpan.FromSeconds(30));

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using var scope = harness.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ISecondFactorService>().VerifyTotpAsync(subject, Password, methodId, "000000");
        }));
        using var after = harness.CreateScope();
        var right = await after.ServiceProvider.GetRequiredService<ISecondFactorService>().VerifyTotpAsync(subject, Password, methodId, CurrentCode(secret));

        Assert.Equal(5, results.Count(r => r.Outcome == SecondFactorOutcome.Incorrect));
        Assert.Equal(15, results.Count(r => r.Outcome == SecondFactorOutcome.Locked));
        Assert.Equal(SecondFactorOutcome.Locked, right.Outcome);
    }

    [Fact]
    public async Task Verify_ShouldCountTotpAndRecoveryFailuresTogether()
    {
        var subject = NewSubject();
        var (methodId, _) = await EnrolFirstTotpAsync(subject);
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecondFactorService>();

        for (var i = 0; i < 3; i++)
        {
            await service.VerifyTotpAsync(subject, Password, methodId, "000000");
            await service.VerifyRecoveryCodeAsync(subject, Password, "AAAA-AAAA-AAAA");
        }

        var locked = await service.VerifyRecoveryCodeAsync(subject, Password, "AAAA-AAAA-AAAA");
        Assert.Equal(SecondFactorOutcome.Locked, locked.Outcome);
    }

    [Fact]
    public async Task Verify_ShouldFailClosed_WhenTheProtectionSchemeIsUnknown()
    {
        var subject = NewSubject();
        var (methodId, secret) = await EnrolFirstTotpAsync(subject);
        await fixture.ExecuteAsync($"UPDATE second_factor_methods SET protection_scheme = 'other-v9' WHERE id = '{methodId}'");
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        using var scope = harness.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISecondFactorService>().VerifyTotpAsync(subject, Password, methodId, CurrentCode(secret));

        Assert.Equal(SecondFactorOutcome.NotAvailable, result.Outcome);
    }

    [Fact]
    public async Task Verify_ShouldNotAcceptAMethodOfAnotherUser()
    {
        var owner = NewSubject();
        var other = NewSubject();
        var (ownerMethod, secret) = await EnrolFirstTotpAsync(owner);
        await EnrolFirstTotpAsync(other);
        harness.Clock.Advance(TimeSpan.FromSeconds(30));
        using var scope = harness.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISecondFactorService>().VerifyTotpAsync(other, Password, ownerMethod, CurrentCode(secret));

        Assert.Equal(SecondFactorOutcome.NotAvailable, result.Outcome);
    }

    [Fact]
    public async Task Otp_ShouldSendAndVerifyACode_AsAWeakMethod()
    {
        var subject = NewSubject();
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecondFactorService>();
        Assert.True((await service.EnrolOtpAsync(subject, Password, MfaMethodKind.SmsOtp, null)).Succeeded);

        var begun = await service.BeginOtpAsync(subject, Password, MfaMethodKind.SmsOtp);
        var wrong = await service.VerifyOtpAsync(subject, Password, MfaMethodKind.SmsOtp, "000000");
        var verified = await service.VerifyOtpAsync(subject, Password, MfaMethodKind.SmsOtp, harness.Sms.Sent[^1].Code);

        Assert.True(begun.Succeeded);
        Assert.Equal(Phone, harness.Sms.Sent[^1].Destination);
        Assert.Equal(SecondFactorOutcome.Incorrect, wrong.Outcome);
        Assert.True(verified.Succeeded);
        Assert.Equal(MfaStrength.Weak, verified.Strength);
        Assert.DoesNotContain(AmrClaims.Mfa, verified.Amr);
    }

    [Fact]
    public async Task Otp_ShouldBeUnavailable_WhenThePrimaryIsExternal()
    {
        var subject = NewSubject();
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecondFactorService>();
        await service.EnrolOtpAsync(subject, Password, MfaMethodKind.SmsOtp, null);

        var begun = await service.BeginOtpAsync(subject, Line, MfaMethodKind.SmsOtp);

        Assert.False(begun.Succeeded);
        Assert.Empty(harness.Sms.Sent);
    }

    [Fact]
    public async Task Otp_ShouldRefundTheRateLimitSlot_WhenDeliveryFails()
    {
        var subject = NewSubject();
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISecondFactorService>();
        await service.EnrolOtpAsync(subject, Password, MfaMethodKind.EmailOtp, null);
        harness.Email.Fail = true;

        var failed = await service.BeginOtpAsync(subject, Password, MfaMethodKind.EmailOtp);
        harness.Email.Fail = false;
        var retried = await service.BeginOtpAsync(subject, Password, MfaMethodKind.EmailOtp);

        Assert.Equal(SecondFactorOutcome.CannotIssue, failed.Outcome);
        Assert.True(retried.Succeeded);
    }

    [Fact]
    public void Options_ShouldRejectAnEmptyIssuerAndAnUnsatisfiableRule()
    {
        var validator = new SecondFactorOptionsValidator();
        var options = new SecondFactorOptions();
        options.Policy.Roles["admin"] = new MfaRule { Required = true, OfferedKinds = new HashSet<MfaMethodKind>() };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Issuer", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("OfferedKinds", result.FailureMessage, StringComparison.Ordinal);
    }
}
