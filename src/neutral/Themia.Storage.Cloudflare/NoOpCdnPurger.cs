namespace Themia.Storage.Cloudflare;

/// <summary>The purger registered when <see cref="CloudflarePurgeOptions.Enabled"/> is off: purges nothing and makes no call.</summary>
internal sealed class NoOpCdnPurger : ICdnPurger
{
    public static readonly NoOpCdnPurger Instance = new();

    public Task PurgeAsync(Uri url, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
