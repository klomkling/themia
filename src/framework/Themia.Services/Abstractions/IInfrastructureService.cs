namespace Themia.Services.Abstractions;

/// <summary>
/// Marker interface for infrastructure services that handle technical concerns.
/// </summary>
/// <remarks>
/// <para><strong>Infrastructure Services</strong> handle technical concerns such as:</para>
/// <list type="bullet">
///   <item>Email and notification delivery</item>
///   <item>File storage and retrieval</item>
///   <item>SMS and messaging</item>
///   <item>Caching strategies</item>
///   <item>Logging and monitoring</item>
///   <item>Authentication and authorization</item>
/// </list>
///
/// <para><strong>Examples:</strong></para>
/// <list type="bullet">
///   <item>Email service (SMTP, SendGrid, etc.)</item>
///   <item>File storage service (Azure Blob, S3, local file system)</item>
///   <item>SMS service (Twilio, etc.)</item>
///   <item>Cache service</item>
/// </list>
///
/// <para><strong>Characteristics:</strong></para>
/// <list type="bullet">
///   <item>Abstract away infrastructure details from domain layer</item>
///   <item>May have multiple implementations (e.g., local vs cloud storage)</item>
///   <item>Handle connection management and error handling</item>
///   <item>Should be easily swappable for testing</item>
/// </list>
///
/// <para><strong>Lifecycle:</strong> Typically registered as <c>Scoped</c> or <c>Singleton</c> in DI container.</para>
/// </remarks>
/// <example>
/// <code>
/// public interface IEmailService : IInfrastructureService
/// {
///     Task SendAsync(EmailMessage message, CancellationToken ct);
///     Task SendBulkAsync(IEnumerable&lt;EmailMessage&gt; messages, CancellationToken ct);
/// }
///
/// public interface IStorageService : IInfrastructureService
/// {
///     Task&lt;string&gt; UploadAsync(Stream content, string fileName, CancellationToken ct);
///     Task&lt;Stream&gt; DownloadAsync(string fileId, CancellationToken ct);
///     Task DeleteAsync(string fileId, CancellationToken ct);
/// }
///
/// // Implementation
/// public class AzureBlobStorageService : IStorageService
/// {
///     private readonly BlobServiceClient _blobClient;
///
///     public async Task&lt;string&gt; UploadAsync(Stream content, string fileName, CancellationToken ct)
///     {
///         var containerClient = _blobClient.GetBlobContainerClient("uploads");
///         var blobClient = containerClient.GetBlobClient(fileName);
///         await blobClient.UploadAsync(content, ct);
///         return blobClient.Uri.ToString();
///     }
/// }
/// </code>
/// </example>
public interface IInfrastructureService : IService
{
}
