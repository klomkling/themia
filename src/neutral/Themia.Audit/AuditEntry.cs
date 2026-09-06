namespace Themia.Audit;

/// <summary>
/// One audit event, framework-neutral and immutable. The database key is a separate <c>bigint</c>
/// generated at write time; <see cref="EventUid"/> is the client-generated public identifier used to
/// address a row from outside the store (e.g. a dashboard detail route).
/// </summary>
public sealed record AuditEntry
{
    /// <summary>The longest accepted <see cref="EventType"/>, matching the <c>event_type</c> column width.</summary>
    public const int MaxEventTypeLength = 100;

    /// <summary>The longest accepted <see cref="ActorId"/>, matching the <c>actor_id</c> column width.</summary>
    public const int MaxActorIdLength = 256;

    /// <summary>The longest accepted <see cref="ActorName"/>, matching the <c>actor_name</c> column width.</summary>
    public const int MaxActorNameLength = 256;

    /// <summary>The longest accepted <see cref="EntityType"/>, matching the <c>entity_type</c> column width.</summary>
    public const int MaxEntityTypeLength = 256;

    /// <summary>The longest accepted <see cref="EntityId"/>, matching the <c>entity_id</c> column width.</summary>
    public const int MaxEntityIdLength = 256;

    /// <summary>The longest accepted <see cref="Reason"/>, matching the <c>reason</c> column width.</summary>
    public const int MaxReasonLength = 256;

    /// <summary>The longest accepted <see cref="CorrelationId"/>, matching the <c>correlation_id</c> column width.</summary>
    public const int MaxCorrelationIdLength = 128;

    /// <summary>The longest accepted <see cref="IpAddress"/>, matching the <c>ip_address</c> column width.</summary>
    public const int MaxIpAddressLength = 64;

    /// <summary>The longest accepted <see cref="UserAgent"/>, matching the <c>user_agent</c> column width.</summary>
    public const int MaxUserAgentLength = 512;

    /// <summary>Client-generated public identifier. The database key is a separate <c>bigint</c>.</summary>
    public Guid EventUid { get; init; } = Guid.NewGuid();

    /// <summary>The owning tenant, or <see langword="null"/> for a host-level event.</summary>
    public string? TenantId { get; init; }

    /// <summary>Broad classification of the event. <see cref="AuditCategory.Unspecified"/> is rejected by <see cref="Validate"/>.</summary>
    public AuditCategory Category { get; init; }

    /// <summary>The event's name, e.g. <c>PROPOSAL_ACCEPTED</c>, <c>LOGIN_FAILED</c>, <c>PASSWORD_CHANGED</c>.</summary>
    public required string EventType { get; init; }

    /// <summary>The event's result. <see cref="AuditOutcome.Unspecified"/> is rejected by <see cref="Validate"/>.</summary>
    public AuditOutcome Outcome { get; init; }

    /// <summary>The acting identity, or <see langword="null"/> when unauthenticated — e.g. a failed login for an unknown identifier.</summary>
    public string? ActorId { get; init; }

    /// <summary>A display name for the acting identity.</summary>
    public string? ActorName { get; init; }

    /// <summary>The type of the entity the event concerns.</summary>
    public string? EntityType { get; init; }

    /// <summary>The identifier of the entity the event concerns.</summary>
    public string? EntityId { get; init; }

    /// <summary>The time the event was captured, not the time it was persisted.</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// The caller's IP address. Populated automatically only for Identity events recorded through
    /// <c>Themia.Modules.Audit</c>'s <c>AuditingIdentityObserver</c>, which applies
    /// <c>Themia.Audit.Http.AuditHttpEnricher</c> before recording; for any other write path (a direct
    /// <see cref="IAuditRecorder"/> call, or the <c>IAuditLogService</c> adapter), supplying it is the
    /// caller's responsibility. Truncated, not rejected, by <see cref="Normalize"/>.
    /// </summary>
    public string? IpAddress { get; init; }

    /// <summary>
    /// The caller's user agent. Populated automatically only for Identity events recorded through
    /// <c>Themia.Modules.Audit</c>'s <c>AuditingIdentityObserver</c>, which applies
    /// <c>Themia.Audit.Http.AuditHttpEnricher</c> before recording; for any other write path (a direct
    /// <see cref="IAuditRecorder"/> call, or the <c>IAuditLogService</c> adapter), supplying it is the
    /// caller's responsibility. Truncated, not rejected, by <see cref="Normalize"/>.
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>An identifier correlating this event with others from the same request or operation.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>The internal reason for the outcome, e.g. a <c>LoginFailureReason</c> or <c>DenialReason</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// A redacted JSON payload describing the event. Set by the recorder from a payload object; never
    /// assigned by a caller outside this assembly — the "recorder serializes, callers never supply a
    /// string" invariant is enforced by the compiler, not a doc comment.
    /// </summary>
    public string? Data { get; internal init; }

    /// <summary>
    /// Validates the entry. Adopter-named fields (<see cref="EventType"/>, <see cref="ActorId"/>,
    /// <see cref="ActorName"/>, <see cref="EntityType"/>, <see cref="EntityId"/>, <see cref="Reason"/>,
    /// <see cref="CorrelationId"/>) throw when they exceed their column width — truncating them would
    /// merge two distinct events into one. <see cref="Category"/> and <see cref="Outcome"/> throw when
    /// unset (<c>Unspecified</c>) or out of range.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// An adopter-named field exceeds its column width, or <see cref="Category"/> or <see cref="Outcome"/>
    /// is unset or undefined.
    /// </exception>
    public void Validate()
    {
        ValidateLength(EventType, MaxEventTypeLength, nameof(EventType));
        ValidateLength(ActorId, MaxActorIdLength, nameof(ActorId));
        ValidateLength(ActorName, MaxActorNameLength, nameof(ActorName));
        ValidateLength(EntityType, MaxEntityTypeLength, nameof(EntityType));
        ValidateLength(EntityId, MaxEntityIdLength, nameof(EntityId));
        ValidateLength(Reason, MaxReasonLength, nameof(Reason));
        ValidateLength(CorrelationId, MaxCorrelationIdLength, nameof(CorrelationId));

        ValidateEnum(Category, nameof(Category));
        ValidateEnum(Outcome, nameof(Outcome));
    }

    /// <summary>
    /// Returns a copy with the caller-address fields (<see cref="UserAgent"/>, <see cref="IpAddress"/>)
    /// clipped to their column width. Unlike <see cref="Validate"/>, these are truncated rather than
    /// rejected: unlike <see cref="EventType"/>/<see cref="ActorId"/>/<see cref="EntityId"/> and the
    /// other adopter-named fields, <see cref="UserAgent"/> and <see cref="IpAddress"/> are supplementary
    /// context rather than part of what identifies the event — that holds whether they were filled by
    /// <c>AuditHttpEnricher</c> or supplied directly by a caller — so a clipped value is still the same
    /// event.
    /// </summary>
    /// <returns>A copy of this entry with <see cref="UserAgent"/> and <see cref="IpAddress"/> clipped.</returns>
    public AuditEntry Normalize() => this with
    {
        UserAgent = Clip(UserAgent, MaxUserAgentLength),
        IpAddress = Clip(IpAddress, MaxIpAddressLength),
    };

    private static void ValidateLength(string? value, int maxLength, string paramName)
    {
        if (value is not null && value.Length > maxLength)
        {
            throw new ArgumentException(
                $"Must be at most {maxLength} characters (was {value.Length}) to fit the column it is stored in.",
                paramName);
        }
    }

    private static void ValidateEnum<TEnum>(TEnum value, string paramName)
        where TEnum : struct, Enum
    {
        if (EqualityComparer<TEnum>.Default.Equals(value, default) || !Enum.IsDefined(value))
        {
            throw new ArgumentException($"Must be a defined, non-default {typeof(TEnum).Name} value.", paramName);
        }
    }

    private static string? Clip(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
