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
    public Task OnLoginFailedAsync(string userName, LoginFailureReason reason, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.Authentication,
            Outcome = AuditOutcome.Failure,
            EventType = "LOGIN_FAILED",
            ActorName = userName,
            Reason = reason.ToString(),
        }, payload: null, cancellationToken);

    /// <inheritdoc />
    public Task OnLoginDeniedAsync(string userName, string? denialReason, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.Authentication,
            Outcome = AuditOutcome.Denied,
            EventType = "LOGIN_DENIED",
            ActorName = userName,
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
    public Task OnExternalLoginFailedAsync(string provider, ExternalLoginOutcome reason, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.Authentication,
            Outcome = AuditOutcome.Failure,
            EventType = "EXTERNAL_LOGIN_FAILED",
            EntityType = "Provider",
            EntityId = provider,
            Reason = reason.ToString(),
        }, payload: null, cancellationToken);

    /// <inheritdoc />
    public Task OnExternalLoginDeniedAsync(string provider, string? denialReason, CancellationToken cancellationToken = default) =>
        RecordAsync(new AuditEntry
        {
            Category = AuditCategory.Authentication,
            Outcome = AuditOutcome.Denied,
            EventType = "EXTERNAL_LOGIN_DENIED",
            EntityType = "Provider",
            EntityId = provider,
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
}
