using Themia.Audit;
using Themia.Audit.Http;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Audit;

/// <summary>
/// Turns every <see cref="IIdentityEventObserver"/> event into an <see cref="AuditEntry"/> (design §10):
/// every one of the thirteen methods is implemented, not a chosen few, so an audit trail that covers
/// password login while a Google sign-in leaves no trace never ships. Registered through
/// <c>AddThemiaAuditIdentityObserver</c>, a separate opt-in call (design §11) so a host without Identity
/// never has to reference this type.
/// </summary>
/// <remarks>
/// Every entry is <see cref="AuditCategory.Authentication"/> except <see cref="OnUserMutatedAsync"/>,
/// which is <see cref="AuditCategory.UserLifecycle"/> — both are hardwired to
/// <c>AuditTransactionPolicy.Never</c> by <see cref="TransactionalAuditRecorder"/>, so this class never
/// needs to think about an ambient transaction. <see cref="AuditEntry.Reason"/> carries the internal
/// reason the uniform 401 hides from the client: the failing enum's own name, or the hook's denial
/// string.
/// </remarks>
public sealed class AuditingIdentityObserver : IIdentityEventObserver
{
    // Appended when a client-supplied field is clipped below, so a reader can tell a truncated
    // identifier from a genuine one that happens to end mid-word.
    private const string TruncationMarker = "…[truncated]";

    private readonly IAuditRecorder recorder;
    private readonly AuditHttpEnricher enricher;

    /// <summary>Creates the observer over <paramref name="recorder"/> and <paramref name="enricher"/>.</summary>
    /// <param name="recorder">Records every mapped entry. Tenant resolution and the hardwired
    /// <c>Never</c> transaction policy for <see cref="AuditCategory.Authentication"/> and
    /// <see cref="AuditCategory.UserLifecycle"/> both live here, not in this class.</param>
    /// <param name="enricher">Fills <see cref="AuditEntry.IpAddress"/> and <see cref="AuditEntry.UserAgent"/>
    /// from the current request. Nothing else in the write path applies it, so this class must.</param>
    public AuditingIdentityObserver(IAuditRecorder recorder, AuditHttpEnricher enricher)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(enricher);

        this.recorder = recorder;
        this.enricher = enricher;
    }

    /// <inheritdoc />
    public Task OnLoginSucceededAsync(Guid userId, string userName, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.Authentication,
            Outcome = AuditOutcome.Success,
            EventType = "LOGIN_SUCCEEDED",
            ActorId = userId.ToString(),
            ActorName = userName,
        }, payload: null, cancellationToken);

    /// <inheritdoc />
    /// <remarks><paramref name="userName"/> is the raw, client-supplied login identifier — untrusted
    /// origin, unbounded length (<c>AuthenticationFlow.LoginAsync</c> only checks
    /// <c>ThrowIfNullOrWhiteSpace</c>). Clipped, not passed through: a caller padding a brute-force
    /// attempt past <see cref="AuditEntry.MaxActorNameLength"/> must not be able to make
    /// <see cref="AuditEntry.Validate"/> throw and silently delete this row — that would let the
    /// caller turn off their own audit trail. See <see cref="ClipUntrusted"/>.</remarks>
    public Task OnLoginFailedAsync(string userName, LoginFailureReason reason, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.Authentication,
            Outcome = AuditOutcome.Failure,
            EventType = "LOGIN_FAILED",
            ActorName = ClipUntrusted(userName, AuditEntry.MaxActorNameLength),
            Reason = reason.ToString(),
        }, payload: null, cancellationToken);

    /// <inheritdoc />
    /// <remarks><paramref name="userName"/> is the raw, client-supplied login identifier — same
    /// untrusted-origin, unbounded-length shape as <see cref="OnLoginFailedAsync"/>; clipped for the
    /// same reason.</remarks>
    public Task OnLoginDeniedAsync(string userName, string? denialReason, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.Authentication,
            Outcome = AuditOutcome.Denied,
            EventType = "LOGIN_DENIED",
            ActorName = ClipUntrusted(userName, AuditEntry.MaxActorNameLength),
            Reason = denialReason,
        }, payload: null, cancellationToken);

    /// <inheritdoc />
    public Task OnExternalLoginSucceededAsync(
        Guid userId, string provider, bool wasCreated, bool wasLinked, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.Authentication,
            Outcome = AuditOutcome.Success,
            EventType = "EXTERNAL_LOGIN_SUCCEEDED",
            ActorId = userId.ToString(),
            EntityType = "Provider",
            EntityId = provider,
        }, payload: new { wasCreated, wasLinked }, cancellationToken);

    /// <inheritdoc />
    /// <remarks><paramref name="provider"/> here comes straight from the <c>{provider}</c> route value
    /// of the external-login endpoint — untrusted origin, unbounded length — unlike the same parameter
    /// on <see cref="OnExternalLoginSucceededAsync"/>, which is only reached after the provider registry
    /// resolved it to a known, bounded name. Clipped for the same reason as
    /// <see cref="OnLoginFailedAsync"/>.</remarks>
    public Task OnExternalLoginFailedAsync(string provider, ExternalLoginOutcome reason, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.Authentication,
            Outcome = AuditOutcome.Failure,
            EventType = "EXTERNAL_LOGIN_FAILED",
            EntityType = "Provider",
            EntityId = ClipUntrusted(provider, AuditEntry.MaxEntityIdLength),
            Reason = reason.ToString(),
        }, payload: null, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Same untrusted-origin, unbounded-length shape as <see cref="OnExternalLoginFailedAsync"/>;
    /// clipped for the same reason.</remarks>
    public Task OnExternalLoginDeniedAsync(string provider, string? denialReason, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.Authentication,
            Outcome = AuditOutcome.Denied,
            EventType = "EXTERNAL_LOGIN_DENIED",
            EntityType = "Provider",
            EntityId = ClipUntrusted(provider, AuditEntry.MaxEntityIdLength),
            Reason = denialReason,
        }, payload: null, cancellationToken);

    /// <inheritdoc />
    public Task OnRefreshSucceededAsync(Guid userId, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.Authentication,
            Outcome = AuditOutcome.Success,
            EventType = "REFRESH_SUCCEEDED",
            ActorId = userId.ToString(),
        }, payload: null, cancellationToken);

    /// <inheritdoc />
    public Task OnRefreshDeniedAsync(
        string? userName, string? denialReason, bool rotationCommitted, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.Authentication,
            Outcome = AuditOutcome.Denied,
            EventType = "REFRESH_DENIED",
            ActorName = userName,
            Reason = denialReason,
        }, payload: new { rotationCommitted }, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="userId"/> is populated for <see cref="RefreshOutcome.ReuseDetected"/> — the single
    /// most actionable event in the refresh flow — and null for <see cref="RefreshOutcome.Invalid"/>,
    /// which has no owner by construction. Mapped straight to <see cref="AuditEntry.ActorId"/> so a
    /// replay an operator can act on is attributable, not merely logged.
    /// </remarks>
    public Task OnRefreshFailedAsync(Guid? userId, RefreshOutcome outcome, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.Authentication,
            Outcome = AuditOutcome.Failure,
            EventType = "REFRESH_FAILED",
            ActorId = userId?.ToString(),
            Reason = outcome.ToString(),
        }, payload: null, cancellationToken);

    /// <inheritdoc />
    public Task OnLogoutAsync(Guid? userId, bool allSessions, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.Authentication,
            Outcome = AuditOutcome.Success,
            EventType = "LOGOUT",
            ActorId = userId?.ToString(),
        }, payload: new { allSessions }, cancellationToken);

    /// <inheritdoc />
    public Task OnLockedOutAsync(Guid userId, DateTimeOffset lockoutEnd, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.Authentication,
            Outcome = AuditOutcome.Success,
            EventType = "LOCKED_OUT",
            ActorId = userId.ToString(),
        }, payload: new { lockoutEnd }, cancellationToken);

    /// <inheritdoc />
    public Task OnUserMutatedAsync(Guid userId, UserMutation mutation, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.UserLifecycle,
            Outcome = AuditOutcome.Success,
            EventType = "USER_MUTATED",
            ActorId = userId.ToString(),
        }, payload: new { mutation = mutation.ToString() }, cancellationToken);

    /// <inheritdoc />
    public Task OnUserMutationRefusedAsync(
        Guid userId, UserMutation mutation, string reason, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.UserLifecycle,
            Outcome = AuditOutcome.Denied,
            EventType = "USER_MUTATION_REFUSED",
            ActorId = userId.ToString(),
            Reason = reason,
        }, payload: new { mutation = mutation.ToString() }, cancellationToken);

    // The single write path every mapped event funnels through: enrich with IP/user-agent (nothing else
    // in this write path applies the enricher), then record. Never touches tenant or transaction policy
    // — TransactionalAuditRecorder owns both.
    private Task RecordAsync(AuditEntry entry, object? payload, CancellationToken cancellationToken) =>
        recorder.RecordAsync(enricher.Enrich(entry), payload, cancellationToken).AsTask();

    /// <summary>
    /// Clips a client-supplied value to <paramref name="maxLength"/> instead of letting
    /// <see cref="AuditEntry.Validate"/> reject it. <see cref="AuditEntry.Validate"/>'s reject-on-overflow
    /// behaviour is correct for a value the application itself names and supplies, where an over-length
    /// value is a programming error worth surfacing; it is wrong for a value an unauthenticated caller
    /// controls, where rejecting the entire entry destroys the audit row instead of merely losing detail —
    /// strictly worse, since it lets that caller erase the very attempt being recorded. The marker is
    /// appended within <paramref name="maxLength"/>, never past it, so the stored value still fits the
    /// column <see cref="AuditEntry.Validate"/> guards.
    /// </summary>
    private static string? ClipUntrusted(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        var keep = Math.Max(0, maxLength - TruncationMarker.Length);
        return string.Concat(value.AsSpan(0, keep), TruncationMarker);
    }
}
