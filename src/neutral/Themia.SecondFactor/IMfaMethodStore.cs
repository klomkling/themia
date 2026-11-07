namespace Themia.SecondFactor;

/// <summary>A method to persist. Passkey rows carry the credential fields; TOTP rows carry the protected secret.</summary>
public sealed record NewMfaMethod
{
    /// <summary>The tenant, or <see langword="null"/> for a platform user.</summary>
    public string? TenantId { get; init; }

    /// <summary>The adopter's user id.</summary>
    public required string UserId { get; init; }

    /// <summary>The method kind.</summary>
    public required MfaMethodKind Kind { get; init; }

    /// <summary>A user-visible label.</summary>
    public required string Label { get; init; }

    /// <summary>Passkey: the credential id.</summary>
    public byte[]? CredentialId { get; init; }

    /// <summary>Passkey: the COSE public key.</summary>
    public byte[]? PublicKey { get; init; }

    /// <summary>Passkey: the WebAuthn user handle.</summary>
    public byte[]? UserHandle { get; init; }

    /// <summary>Passkey: the counter recorded at registration.</summary>
    public long SignCount { get; init; }

    /// <summary>TOTP: the secret, protected at rest by the adopter's <c>ISecondFactorSecretProtector</c>.</summary>
    public string? SecretProtected { get; init; }

    /// <summary>TOTP: the scheme that protected <see cref="SecretProtected"/>, so schemes can coexist.</summary>
    public string? ProtectionScheme { get; init; }

    /// <summary>TOTP: the step of the code that confirmed enrolment, so that code cannot be replayed at sign-in.</summary>
    public long LastUsedStep { get; init; } = -1;

    /// <summary>When the method was confirmed.</summary>
    public required DateTimeOffset EnrolledAt { get; init; }
}

/// <summary>Whether <see cref="IMfaMethodStore.AddAsync"/> stored the method.</summary>
public enum MfaEnrolOutcome
{
    /// <summary>The method was stored.</summary>
    Added,

    /// <summary>The passkey credential id is already bound to an account.</summary>
    DuplicateCredential,
}

/// <summary>A stored passkey with everything needed to verify an assertion.</summary>
public sealed record StoredPasskey(
    Guid MethodId,
    string? TenantId,
    string UserId,
    byte[] CredentialId,
    byte[] PublicKey,
    byte[] UserHandle,
    long SignCount);

/// <summary>A stored TOTP secret.</summary>
public sealed record StoredTotp(Guid MethodId, string SecretProtected, string ProtectionScheme, long LastUsedStep);

/// <summary>Credential store: one row per method. Every call is scoped to (tenant, user); a null tenant means a platform user.</summary>
public interface IMfaMethodStore
{
    /// <summary>All of the user's methods, disabled ones included.</summary>
    Task<IReadOnlyList<MfaMethod>> ListAsync(string? tenantId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Stores a method. A passkey credential id is unique across all accounts.</summary>
    Task<MfaEnrolOutcome> AddAsync(NewMfaMethod method, CancellationToken cancellationToken = default);

    /// <summary>Removes one of the user's methods. Returns <see langword="false"/> when it is not theirs.</summary>
    Task<bool> RemoveAsync(string? tenantId, string userId, Guid methodId, CancellationToken cancellationToken = default);

    /// <summary>Loads a passkey by credential id alone (discoverable sign-in). The caller must check the returned user handle.</summary>
    Task<StoredPasskey?> FindPasskeyAsync(byte[] credentialId, CancellationToken cancellationToken = default);

    /// <summary>The credential ids of the user's usable passkeys, for an allow-list.</summary>
    Task<IReadOnlyList<byte[]>> ListPasskeyCredentialIdsAsync(string? tenantId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Loads a usable TOTP method of the user.</summary>
    Task<StoredTotp?> FindTotpAsync(string? tenantId, string userId, Guid methodId, CancellationToken cancellationToken = default);

    /// <summary>Atomically advances the TOTP step; <see langword="false"/> when <paramref name="step"/> is not newer (replay).</summary>
    Task<bool> TryAdvanceTotpStepAsync(string? tenantId, string userId, Guid methodId, long step, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically records a new sign counter. Accepts a strictly higher counter, and 0 after 0 (synced passkeys
    /// report 0 on purpose); <see langword="false"/> means a regression or a lost race.
    /// </summary>
    Task<bool> TryUpdateSignCountAsync(string? tenantId, string userId, Guid methodId, long newSignCount, DateTimeOffset now, CancellationToken cancellationToken = default);
}
