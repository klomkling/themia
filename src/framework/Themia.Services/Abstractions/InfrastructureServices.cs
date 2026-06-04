using Themia.Caching;

namespace Themia.Services.Abstractions;

/// <summary>
/// Email delivery abstraction.
/// </summary>
public interface IEmailService : IInfrastructureService
{
    /// <summary>Sends a single email message.</summary>
    /// <param name="message">The email message to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);

    /// <summary>Sends multiple email messages in bulk.</summary>
    /// <param name="messages">The email messages to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendBulkAsync(IEnumerable<EmailMessage> messages, CancellationToken cancellationToken = default);
}

/// <summary>
/// SMS/OTP delivery.
/// </summary>
public interface ISmsService : IInfrastructureService
{
    /// <summary>Sends an SMS message.</summary>
    /// <param name="message">The SMS message to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Push notification delivery to devices/apps.
/// </summary>
public interface IPushNotificationService : IInfrastructureService
{
    /// <summary>Sends a push notification.</summary>
    /// <param name="notification">The push notification to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAsync(PushNotification notification, CancellationToken cancellationToken = default);
}

/// <summary>
/// Binary storage abstraction (Azure Blob, S3, etc.).
/// </summary>
public interface IStorageService : IInfrastructureService
{
    /// <summary>Uploads content to storage and returns the upload result.</summary>
    /// <param name="content">The content stream to upload.</param>
    /// <param name="fileName">The file name.</param>
    /// <param name="contentType">The MIME content type.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The upload result containing the storage location.</returns>
    Task<StorageUploadResult> UploadAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Downloads content from the given storage location.</summary>
    /// <param name="location">The storage location identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A stream containing the downloaded content.</returns>
    Task<Stream> DownloadAsync(string location, CancellationToken cancellationToken = default);

    /// <summary>Deletes content at the given storage location.</summary>
    /// <param name="location">The storage location identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteAsync(string location, CancellationToken cancellationToken = default);
}

/// <summary>
/// Export documents (PDF/Excel/etc.).
/// </summary>
public interface IReportExportService : IInfrastructureService
{
    /// <summary>Exports a report based on the given request.</summary>
    /// <param name="request">The export request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The export result containing the document content.</returns>
    Task<ReportExportResult> ExportAsync(ReportExportRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Distributed caching via existing Themia cache providers.
/// </summary>
public interface ICacheProviderAccessor : IInfrastructureService
{
    /// <summary>Gets the underlying Themia cache provider.</summary>
    IThemiaCacheProvider Provider { get; }
}

/// <summary>
/// Background job scheduling.
/// </summary>
public interface IBackgroundJobScheduler : IInfrastructureService
{
    /// <summary>Schedules a named job to run at a specific UTC time.</summary>
    /// <param name="jobName">The job name.</param>
    /// <param name="runAtUtc">The UTC time to run the job.</param>
    /// <param name="payload">The job payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scheduled job identifier.</returns>
    Task<string> ScheduleAsync(string jobName, DateTimeOffset runAtUtc, object payload, CancellationToken cancellationToken = default);

    /// <summary>Enqueues a named job for immediate execution.</summary>
    /// <param name="jobName">The job name.</param>
    /// <param name="payload">The job payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the job is enqueued.</returns>
    Task EnqueueAsync(string jobName, object payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// Secrets/config provider abstraction.
/// </summary>
public interface ISecretsProvider : IInfrastructureService
{
    /// <summary>Gets a named secret value.</summary>
    /// <param name="name">The secret name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The secret value, or <see langword="null"/> if not found.</returns>
    Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>
/// Audit event sink (complements entity-level IAuditable fields).
/// </summary>
public interface IAuditLogService : IInfrastructureService
{
    /// <summary>Writes an audit event to the audit log.</summary>
    /// <param name="auditEvent">The audit event to record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Token issuance/validation (e.g., JWTs).
/// </summary>
public interface ITokenService : IInfrastructureService
{
    /// <summary>Issues a token based on the given descriptor.</summary>
    /// <param name="descriptor">The token descriptor.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The issued token string.</returns>
    Task<string> IssueTokenAsync(TokenDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>Validates a token and returns the validation result.</summary>
    /// <param name="token">The token to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The validation result containing claims if valid.</returns>
    Task<TokenValidationResult> ValidateTokenAsync(string token, CancellationToken cancellationToken = default);
}

/// <summary>
/// Application event bus abstraction.
/// </summary>
public interface IEventBus : IInfrastructureService
{
    /// <summary>Publishes a message to the given topic.</summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="topic">The topic name.</param>
    /// <param name="message">The message to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default);
}

/// <summary>Represents an email message to be sent.</summary>
/// <param name="To">The recipient address.</param>
/// <param name="Subject">The email subject.</param>
/// <param name="Body">The email body.</param>
/// <param name="IsHtml">Whether the body is HTML.</param>
/// <param name="From">Optional sender address override.</param>
/// <param name="Headers">Optional additional headers.</param>
public sealed record EmailMessage(string To, string Subject, string Body, bool IsHtml = false, string? From = null, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>Represents an SMS message to be sent.</summary>
/// <param name="To">The recipient phone number.</param>
/// <param name="Body">The message body.</param>
/// <param name="From">Optional sender number override.</param>
/// <param name="CountryCode">Optional country code.</param>
public sealed record SmsMessage(string To, string Body, string? From = null, string? CountryCode = null);

/// <summary>Represents a push notification to be sent to a device or app.</summary>
/// <param name="Target">The notification target (device token, topic, etc.).</param>
/// <param name="Title">The notification title.</param>
/// <param name="Body">The notification body.</param>
/// <param name="Data">Optional data payload.</param>
public sealed record PushNotification(string Target, string Title, string Body, IReadOnlyDictionary<string, string>? Data = null);

/// <summary>Represents the result of a storage upload operation.</summary>
/// <param name="Location">The storage location identifier (URL or path).</param>
/// <param name="ContentType">The MIME content type of the stored content.</param>
/// <param name="SizeBytes">The size of the stored content in bytes.</param>
public sealed record StorageUploadResult(string Location, string ContentType, long SizeBytes);

/// <summary>Represents a report export request.</summary>
/// <param name="TemplateName">The template to use for export.</param>
/// <param name="Parameters">Optional template parameters.</param>
/// <param name="Format">The export format (default: <c>pdf</c>).</param>
public sealed record ReportExportRequest(string TemplateName, IReadOnlyDictionary<string, string>? Parameters = null, string Format = "pdf");

/// <summary>Represents the result of a report export operation.</summary>
/// <param name="ContentType">The MIME content type of the exported document.</param>
/// <param name="Content">The exported document content.</param>
/// <param name="FileName">The suggested file name for the export.</param>
public sealed record ReportExportResult(string ContentType, byte[] Content, string FileName);

/// <summary>Represents an audit event to be recorded.</summary>
/// <param name="EventType">The type/category of the audit event.</param>
/// <param name="ActorId">The identifier of the actor who performed the action.</param>
/// <param name="ActorName">The display name of the actor.</param>
/// <param name="EntityId">The identifier of the entity affected.</param>
/// <param name="EntityType">The type name of the entity affected.</param>
/// <param name="OccurredAtUtc">The UTC time the event occurred (capture time, not the time the record was persisted).</param>
/// <param name="Metadata">Optional additional metadata.</param>
public sealed record AuditEvent(string EventType, string ActorId, string ActorName, string EntityId, string EntityType, DateTimeOffset OccurredAtUtc, IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>Describes the parameters for token issuance.</summary>
/// <param name="Subject">The token subject (e.g., user ID).</param>
/// <param name="Claims">The claims to embed in the token.</param>
/// <param name="ExpiresAtUtc">The UTC expiry time.</param>
/// <param name="Audience">Optional token audience.</param>
/// <param name="Issuer">Optional token issuer.</param>
public sealed record TokenDescriptor(string Subject, IReadOnlyDictionary<string, string> Claims, DateTimeOffset ExpiresAtUtc, string? Audience = null, string? Issuer = null);

/// <summary>Represents the result of a token validation operation.</summary>
/// <param name="IsValid">Whether the token is valid.</param>
/// <param name="Claims">The claims extracted from the token, if valid.</param>
/// <param name="Reason">The reason for validation failure, if invalid.</param>
public sealed record TokenValidationResult(bool IsValid, IReadOnlyDictionary<string, string>? Claims = null, string? Reason = null);
