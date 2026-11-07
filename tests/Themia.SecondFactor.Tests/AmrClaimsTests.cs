using Themia.SecondFactor;

using Xunit;

namespace Themia.SecondFactor.Tests;

public sealed class AmrClaimsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Trusted = ["https://ezyassets.example"];

    private static FederatedAssertion Assertion(string[] amr, DateTimeOffset? authTime, string issuer = "https://ezyassets.example") =>
        new(issuer, amr, authTime);

    [Fact]
    public void ForSecondFactor_ShouldEmitMfa_WhenStrong()
    {
        var amr = AmrClaims.ForSecondFactor(MfaMethodKind.Passkey);

        Assert.Contains(AmrClaims.Mfa, amr);
        Assert.Contains(AmrClaims.SoftwareKey, amr);
    }

    [Fact]
    public void ForSecondFactor_ShouldNotEmitMfa_WhenWeak()
    {
        var amr = AmrClaims.ForSecondFactor(MfaMethodKind.SmsOtp);

        Assert.DoesNotContain(AmrClaims.Mfa, amr);
        Assert.Contains(AmrClaims.Sms, amr);
    }

    [Fact]
    public void ForRecoveryCode_ShouldBeStrongMfa()
    {
        Assert.Contains(AmrClaims.Mfa, AmrClaims.ForRecoveryCode());
    }

    [Fact]
    public void Satisfies_ShouldAccept_WhenTrustedStrongFresh()
    {
        var result = FederatedMfaVerifier.Satisfies(
            Assertion([AmrClaims.Password, AmrClaims.Mfa, AmrClaims.Otp], Now.AddMinutes(-2)), Trusted, TimeSpan.FromMinutes(10), Now);

        Assert.True(result.Succeeded());
    }

    [Theory]
    [InlineData("https://evil.example", FederatedMfaOutcome.UntrustedIssuer)]
    public void Satisfies_ShouldRefuse_WhenIssuerNotTrusted(string issuer, FederatedMfaOutcome expected)
    {
        var result = FederatedMfaVerifier.Satisfies(
            Assertion([AmrClaims.Mfa, AmrClaims.Otp], Now, issuer), Trusted, TimeSpan.FromMinutes(10), Now);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Satisfies_ShouldRefuse_WhenMfaMissing()
    {
        var result = FederatedMfaVerifier.Satisfies(
            Assertion([AmrClaims.Password, AmrClaims.Otp], Now), Trusted, TimeSpan.FromMinutes(10), Now);

        Assert.Equal(FederatedMfaOutcome.MissingMfa, result);
    }

    [Fact]
    public void Satisfies_ShouldRefuse_WhenMfaClaimedButOnlyWeakMethod()
    {
        var result = FederatedMfaVerifier.Satisfies(
            Assertion([AmrClaims.Password, AmrClaims.Mfa, AmrClaims.Sms], Now), Trusted, TimeSpan.FromMinutes(10), Now);

        Assert.Equal(FederatedMfaOutcome.NoStrongMethod, result);
    }

    [Fact]
    public void Satisfies_ShouldRefuse_WhenAuthTimeMissing()
    {
        var result = FederatedMfaVerifier.Satisfies(
            Assertion([AmrClaims.Mfa, AmrClaims.Otp], null), Trusted, TimeSpan.FromMinutes(10), Now);

        Assert.Equal(FederatedMfaOutcome.MissingAuthTime, result);
    }

    [Fact]
    public void Satisfies_ShouldRefuse_WhenAuthTimeStale()
    {
        var result = FederatedMfaVerifier.Satisfies(
            Assertion([AmrClaims.Mfa, AmrClaims.Otp], Now.AddMinutes(-11)), Trusted, TimeSpan.FromMinutes(10), Now);

        Assert.Equal(FederatedMfaOutcome.StaleAuthTime, result);
    }

    [Fact]
    public void Satisfies_ShouldRefuse_WhenAuthTimeFarInFuture()
    {
        var result = FederatedMfaVerifier.Satisfies(
            Assertion([AmrClaims.Mfa, AmrClaims.Otp], Now.AddMinutes(10)), Trusted, TimeSpan.FromMinutes(10), Now);

        Assert.Equal(FederatedMfaOutcome.StaleAuthTime, result);
    }
}
