namespace Themia.Modules.Identity.Abstractions;

/// <summary>How an <see cref="IIdentityAnonymiser.AnonymiseAsync"/> call ended.</summary>
/// <remarks>An enum rather than a bool so a later state breaks an exhaustive <c>switch</c> instead of compiling
/// silently at every existing call site.</remarks>
public enum AnonymiseOutcome
{
    /// <summary>The user's identity data was scrubbed by this call.</summary>
    Anonymised,

    /// <summary>The user was already a tombstone; any stray child rows were still removed.</summary>
    AlreadyAnonymised,

    /// <summary>No user with that id is visible in the current tenant scope.</summary>
    NotFound,

    /// <summary>An <see cref="IUserLifecycleHooks"/> implementation refused it. See the reason.</summary>
    Refused,
}

/// <summary>The outcome of <see cref="IIdentityAnonymiser.AnonymiseAsync"/>.</summary>
public readonly record struct AnonymiseResult
{
    private AnonymiseResult(AnonymiseOutcome outcome, string? reason, string? refusalCode)
    {
        Outcome = outcome;
        Reason = reason;
        RefusalCode = refusalCode;
    }

    /// <summary>How the call ended.</summary>
    public AnonymiseOutcome Outcome { get; }

    /// <summary>The hook's reason when <see cref="Outcome"/> is <see cref="AnonymiseOutcome.Refused"/>; otherwise null.</summary>
    public string? Reason { get; }

    /// <summary>The hook's machine-readable code when it gave one; otherwise null.</summary>
    public string? RefusalCode { get; }

    /// <summary>Whether the user is anonymised after the call (now or already).</summary>
    public bool Succeeded => Outcome is AnonymiseOutcome.Anonymised or AnonymiseOutcome.AlreadyAnonymised;

    /// <summary>The user was anonymised by this call.</summary>
    public static AnonymiseResult Anonymised() => new(AnonymiseOutcome.Anonymised, null, null);

    /// <summary>The user was already anonymised.</summary>
    public static AnonymiseResult AlreadyAnonymised() => new(AnonymiseOutcome.AlreadyAnonymised, null, null);

    /// <summary>No such user in the current tenant scope.</summary>
    public static AnonymiseResult NotFound() => new(AnonymiseOutcome.NotFound, null, null);

    /// <summary>A hook refused the call.</summary>
    /// <param name="reason">The hook's reason. It must not contain personal data.</param>
    /// <param name="refusalCode">The hook's machine-readable code, or null.</param>
    public static AnonymiseResult Refused(string reason, string? refusalCode) =>
        new(AnonymiseOutcome.Refused, reason, refusalCode);
}

/// <summary>
/// Scrubs the identity data of a closed account while keeping the user row, so foreign keys from application
/// tables still resolve.
/// </summary>
/// <remarks>
/// <para>
/// The user row becomes an inactive tombstone: user name <c>anonymised-{id:N}</c>, no e-mail, phone or
/// password, a new security stamp. Its refresh and user tokens, external logins, claims and OTP challenges
/// (including ones keyed only by the old phone number) are deleted; role memberships are kept. The call is
/// idempotent and runs in one unit of work.
/// </para>
/// <para>
/// <b>Tenant scope.</b> A tenant user can be anonymised only from that tenant's scope, a platform user only
/// from the platform scope; anything else is <see cref="AnonymiseOutcome.NotFound"/>. A background job sets the
/// scope with <c>TenantContextAccessor.BeginScope(tenantId)</c> before calling.
/// </para>
/// <para>
/// <b>Not covered.</b> Only Identity's own tables. Audit actor columns, notification logs, conversations and
/// application tables have their own erasure. OTP challenges with no user id and a phone number the user never
/// held cannot be tied to the user and expire by their TTL. The security-stamp cache is per process, so on a
/// multi-instance host an old access token can stay valid for up to
/// <see cref="IdentityModuleOptions.SecurityStampCacheDuration"/> when stamp validation is on.
/// </para>
/// </remarks>
public interface IIdentityAnonymiser
{
    /// <summary>Anonymises the user.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<AnonymiseResult> AnonymiseAsync(Guid userId, CancellationToken cancellationToken = default);
}
