using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.Imaging;
using Themia.Storage;

namespace Themia.Conversations.Internal;

/// <summary>Fails the host at start when attachments are switched on without the storage and image processor they need, instead of at the first upload.</summary>
internal sealed class AttachmentSupportCheck(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IStorageProvider>() is null)
        {
            throw new InvalidOperationException("Conversation attachments need an IStorageProvider: register Themia.Storage and an engine.");
        }

        if (services.GetService<IImageProcessor>() is null)
        {
            throw new InvalidOperationException("Conversation attachments need an IImageProcessor: call AddThemiaImaging(). Images are re-encoded so their metadata is dropped.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
