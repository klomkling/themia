using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Themia.Imaging;
using Themia.Storage;
using Themia.Storage.Local;

namespace Themia.Conversations.Internal;

/// <summary>Fails the host at start when attachments are switched on without the storage and image processor they need, instead of at the first upload.</summary>
internal sealed class AttachmentSupportCheck(IServiceProvider services, ILogger<AttachmentSupportCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var storage = services.GetService<IStorageProvider>();
        if (storage is null)
        {
            throw new InvalidOperationException("Conversation attachments need an IStorageProvider: register Themia.Storage and an engine.");
        }

        if (services.GetService<IImageProcessor>() is null)
        {
            throw new InvalidOperationException("Conversation attachments need an IImageProcessor: call AddThemiaImaging(). Images are re-encoded so their metadata is dropped.");
        }

        if (storage is LocalStorageProvider)
        {
            logger.LogWarning(
                "Conversation attachments use the local storage provider, which cannot force a download: a stored PDF is served inline from the app origin. Use S3 or another backend that signs Content-Disposition outside development.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
