using Fido2NetLib;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.SecondFactor;

namespace Themia.Modules.Identity.SecondFactor.Tests;

internal static class Samples
{
    public static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    public static MfaSubject Subject => new(UserId.ToString("D"), null, new HashSet<string>());

    public static SecondFactorTicketData Ticket(SecondFactorStage stage, SecondFactorMethod method = SecondFactorMethod.Password, string? provider = null) =>
        new(UserId, null, method, provider, false, false, "stamp", stage);

    public static PendingSecondFactor Pending(SecondFactorStage stage) =>
        new(UserId, stage, "ticket", Now.AddMinutes(5));

    public static SecondFactorResult Verified(MfaStrength strength, bool recovery = false) => new()
    {
        Outcome = SecondFactorOutcome.Succeeded,
        Strength = strength,
        UsedRecoveryCode = recovery,
        AuthenticatedAt = Now,
        Amr = ["otp"],
    };

    public static MfaRequirement Requirement(MfaRequirementLevel level, bool upgrade = false, bool spare = false) => new()
    {
        Level = level,
        EnrolKinds = [],
        UsableMethods = [],
        UpgradeMandatory = upgrade,
        SpareMandatory = spare,
    };
}

internal sealed class FakeSubjects(MfaSubject? subject, string? stamp = "stamp") : ISecondFactorSubjectResolver
{
    public Task<string?> GetSecurityStampAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(stamp);

    public Task<MfaSubject?> ResolveAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(subject);
}

internal sealed class FakeTickets(SecondFactorTicketData? data) : ISecondFactorTicketService
{
    public Task<PendingSecondFactor> IssueAsync(SecondFactorTicketData d, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SecondFactorTicketData?> PeekAsync(string ticket, CancellationToken cancellationToken = default) => Task.FromResult(data);

    public Task<SecondFactorTicketData?> ConsumeAsync(string ticket, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class FakeCompletion : ISecondFactorProofCompletion
{
    public List<SessionProofSnapshot> Completed { get; } = [];

    public Task<SecondFactorCompletionResult> CompleteAsync(string ticket, Guid expectedUserId, SecondFactorStage expectedStage, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The bridge must always complete with a proof.");

    public Task<SecondFactorCompletionResult> CompleteAsync(string ticket, Guid expectedUserId, SecondFactorStage expectedStage, SessionProofSnapshot proof, CancellationToken cancellationToken = default)
    {
        Completed.Add(proof);
        return Task.FromResult(SecondFactorCompletionResult.Completed(new AuthTokens("a", 60, "r"), SecondFactorMethod.Password, null, false, false));
    }

    public Task RejectAsync(string ticket, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FakeProofStore : ISessionProofStore
{
    public SessionProofSnapshot? Snapshot { get; set; }

    public int Lifts { get; private set; }

    public Task<SessionProofSnapshot?> GetAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);

    public Task<bool> SaveAsync(Guid userId, Guid sessionId, SessionProofSnapshot proof, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<bool> LiftRestrictionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        Lifts++;
        return Task.FromResult(true);
    }
}

internal sealed class FakeService : ISecondFactorService
{
    public SecondFactorResult Result { get; set; } = Samples.Verified(MfaStrength.Strong);

    public MfaRequirement Requirement { get; set; } = Samples.Requirement(MfaRequirementLevel.Verify);

    public MfaPrimary? SeenPrimary { get; private set; }

    public MfaProof? SeenProof { get; private set; }

    public int EnrolOtpCalls { get; private set; }

    public Task<MfaRequirement> EvaluateAsync(MfaSubject subject, MfaPrimary primary, CancellationToken cancellationToken = default)
    {
        SeenPrimary = primary;
        return Task.FromResult(Requirement);
    }

    public Task<SecondFactorResult> VerifyTotpAsync(MfaSubject subject, MfaPrimary primary, Guid methodId, string code, CancellationToken cancellationToken = default)
    {
        SeenPrimary = primary;
        return Task.FromResult(Result);
    }

    public Task<SecondFactorResult> VerifyRecoveryCodeAsync(MfaSubject subject, MfaPrimary primary, string code, CancellationToken cancellationToken = default) => Task.FromResult(Result);

    public Task<SecondFactorResult> VerifyOtpAsync(MfaSubject subject, MfaPrimary primary, MfaMethodKind kind, string code, CancellationToken cancellationToken = default) => Task.FromResult(Result);

    public Task<SecondFactorResult> VerifyPasskeyAsync(MfaSubject subject, MfaPrimary primary, AuthenticatorAssertionRawResponse response, CancellationToken cancellationToken = default) => Task.FromResult(Result);

    public Task<SecondFactorResult> ConfirmTotpEnrolAsync(MfaSubject subject, string enrolmentId, string code, string label, MfaProof? proof, CancellationToken cancellationToken = default)
    {
        SeenProof = proof;
        return Task.FromResult(Result);
    }

    public Task<SecondFactorResult> CompletePasskeyEnrolAsync(MfaSubject subject, AuthenticatorAttestationRawResponse response, string label, MfaProof? proof, CancellationToken cancellationToken = default)
    {
        SeenProof = proof;
        return Task.FromResult(Result);
    }

    public Task<SecondFactorResult> EnrolOtpAsync(MfaSubject subject, MfaPrimary primary, MfaMethodKind kind, MfaProof? proof, CancellationToken cancellationToken = default)
    {
        EnrolOtpCalls++;
        return Task.FromResult(Result);
    }

    public Task<IReadOnlyList<MfaMethod>> ListMethodsAsync(MfaSubject subject, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<OtpChallengeResult> BeginOtpAsync(MfaSubject subject, MfaPrimary primary, MfaMethodKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SecondFactorValue<PasskeyCeremony>> BeginPasskeyAssertionAsync(MfaSubject subject, MfaPrimary primary, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SecondFactorValue<TotpEnrolment>> BeginTotpEnrolAsync(MfaSubject subject, MfaPrimary primary, MfaProof? proof, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SecondFactorValue<PasskeyCeremony>> BeginPasskeyEnrolAsync(MfaSubject subject, MfaPrimary primary, MfaProof? proof, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<SecondFactorResult> RemoveMethodAsync(MfaSubject subject, MfaPrimary primary, Guid methodId, MfaProof? proof, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<RecoveryCodesResult> GenerateRecoveryCodesAsync(MfaSubject subject, MfaProof? proof, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
