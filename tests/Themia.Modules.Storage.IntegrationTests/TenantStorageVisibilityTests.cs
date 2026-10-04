using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Themia.Modules.Storage.DependencyInjection;
using Themia.Modules.Storage.IntegrationTests.Fixtures;
using Themia.Storage;
using Xunit;

namespace Themia.Modules.Storage.IntegrationTests;

file sealed class VisibilityStubCurrentUserAccessor(string? userId) : ICurrentUserAccessor
{
    public string? UserId { get; } = userId;
}

/// <summary>A CDN purger that records each URL and always refuses, as Cloudflare does when rate-limited.</summary>
file sealed class RefusingCdnPurger : ICdnPurger
{
    public List<Uri> Urls { get; } = [];

    public Task PurgeAsync(Uri url, CancellationToken cancellationToken = default)
    {
        Urls.Add(url);
        throw new CdnPurgeException(url, "Simulated refused purge.", httpStatus: 429);
    }
}

/// <summary>Captures every message logged, across every category.</summary>
file sealed class MessageCapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (messages)
            {
                return messages.ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (messages)
            {
                messages.Add(formatter(state, exception));
            }
        }
    }
}

/// <summary>Exercises visibility-aware writes/reads on the real EF/Postgres-backed <see cref="ITenantStorage"/>:
/// addressing the right container, immutability of visibility once written, and <see cref="ITenantStorage.GetPublicUrlAsync"/>.
/// Uses the same real-<see cref="ServiceCollection"/> harness style as <see cref="StorageConformanceTests"/>
/// (<c>AddThemiaStorage().UseLocal(...)</c>), configured with a public container and a single fixed tenant
/// (<c>t1</c>). The scope is built INLINE in each <see cref="Fact"/> (not via <see cref="IAsyncLifetime"/>) —
/// mirroring <see cref="StorageConformanceTests.NewScope"/> exactly — because the ambient
/// <c>TenantContextAccessor</c> AsyncLocal set when the <c>DbContext</c> is first constructed does not
/// reliably flow from an <c>IAsyncLifetime.InitializeAsync</c> phase into the test method's own execution.</summary>
[Trait("Category", "Integration")]
public sealed class TenantStorageVisibilityTests(PostgresStorageFixture fixture) : IClassFixture<PostgresStorageFixture>
{
    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    private sealed record Scope(ServiceProvider Provider, AsyncServiceScope Inner) : IAsyncDisposable
    {
        public ITenantStorage Storage => Inner.ServiceProvider.GetRequiredService<ITenantStorage>();

        public async ValueTask DisposeAsync() { await Inner.DisposeAsync(); await Provider.DisposeAsync(); }
    }

    private Scope NewScope(Action<IServiceCollection>? configureServices = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = fixture.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("t1")));
        services.AddThemiaPostgres<TestStorageDbContext>(configuration);
        services.AddThemiaDataRepositories<TestStorageDbContext>();

        services.AddThemiaStorage()
            .UseLocal(o =>
            {
                o.RootPath = Path.Combine(Path.GetTempPath(), "themia-storage-visibility-it", Guid.NewGuid().ToString("N"));
                o.SigningKey = "integration-signing-key-please-change!";
                o.PublicRootPath = Path.Combine(Path.GetTempPath(), "themia-storage-visibility-it-public", Guid.NewGuid().ToString("N"));
                o.PublicBaseUrl = "https://cdn.example.com";
            });

        services.RemoveAll<ICurrentUserAccessor>();
        services.AddSingleton<ICurrentUserAccessor>(new VisibilityStubCurrentUserAccessor("test-user"));

        configureServices?.Invoke(services);

        var provider = services.BuildServiceProvider();
        return new Scope(provider, provider.CreateAsyncScope());
    }

    [Fact]
    public async Task Put_public_then_GetPublicUrl_returns_the_absolute_url()
    {
        await fixture.ResetAsync();
        await using var s = NewScope();

        await s.Storage.PutAsync("hero.jpg", Bytes("x"), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));

        var url = await s.Storage.GetPublicUrlAsync("hero.jpg");

        Assert.True(url.IsAbsoluteUri);
        Assert.Equal("https://cdn.example.com/t1/hero.jpg", url.ToString());
    }

    [Fact]
    public async Task GetPublicUrl_throws_for_a_private_object()
    {
        await fixture.ResetAsync();
        await using var s = NewScope();

        await s.Storage.PutAsync("invoice.pdf", Bytes("x"), new StoragePutOptions("application/pdf"));

        // The failure must land at the CALL SITE, not as a 403 in someone's browser.
        await Assert.ThrowsAsync<StorageNotPublicException>(() => s.Storage.GetPublicUrlAsync("invoice.pdf"));
    }

    [Fact]
    public async Task GetPublicUrl_throws_for_a_missing_object()
    {
        await fixture.ResetAsync();
        await using var s = NewScope();

        await Assert.ThrowsAsync<StorageNotPublicException>(() => s.Storage.GetPublicUrlAsync("nope.jpg"));
    }

    [Fact]
    public async Task A_public_object_reads_back_through_the_public_container()
    {
        await fixture.ResetAsync();
        await using var s = NewScope();

        await s.Storage.PutAsync("hero.jpg", Bytes("public-bytes"), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));

        var read = await s.Storage.GetAsync("hero.jpg");

        Assert.NotNull(read);
        Assert.Equal("public-bytes", await new StreamReader(read!.Content).ReadToEndAsync());
    }

    [Fact]
    public async Task Re_putting_an_existing_key_with_a_different_visibility_throws()
    {
        await fixture.ResetAsync();
        await using var s = NewScope();

        await s.Storage.PutAsync("hero.jpg", Bytes("x"), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));

        // Visibility is immutable. The alternatives are both SILENT failures: writing at the new visibility
        // orphans the old blob; writing at the old one ignores the caller and leaves the app believing it
        // published a photo that is still private.
        var ex = await Assert.ThrowsAsync<StorageValidationException>(() =>
            s.Storage.PutAsync("hero.jpg", Bytes("y"), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Private)));
        Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetUploadUrl_at_a_different_visibility_than_an_existing_object_throws()
    {
        await fixture.ResetAsync();
        await using var s = NewScope();

        await s.Storage.PutAsync("hero.jpg", Bytes("x"), new StoragePutOptions("image/jpeg"));

        // Same immutability rule as PutAsync, but on the presigned-upload path: minting a PUT into the
        // other container would orphan the new bytes and leave CompleteUploadAsync statting the wrong one.
        var ex = await Assert.ThrowsAsync<StorageValidationException>(() =>
            s.Storage.GetUploadUrlAsync("hero.jpg", "image/jpeg", 10, TimeSpan.FromMinutes(5), StorageVisibility.Public));
        Assert.Contains("immutable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_a_public_object_still_completes_when_the_cdn_purge_is_refused_and_says_so_in_the_log()
    {
        // The real PurgingStorageProvider over the real Local provider, a public key, and a purger that refuses
        // (coord #0153, spec section 8). The module's delete is best-effort: the logical delete is already
        // committed and the call completes. What the log says must be true: the blob IS gone and nothing
        // retries the purge, so it must not claim a reconcile sweep will clean up.
        await fixture.ResetAsync();
        var purger = new RefusingCdnPurger();
        var logs = new MessageCapturingLoggerProvider();
        await using var s = NewScope(services =>
        {
            var descriptor = services.Last(d => d.ServiceType == typeof(IStorageProvider));
            services.Remove(descriptor);
            services.AddSingleton<IStorageProvider>(sp =>
                new PurgingStorageProvider((IStorageProvider)descriptor.ImplementationFactory!(sp), purger));
            services.AddLogging(b => { b.AddProvider(logs); b.SetMinimumLevel(LogLevel.Trace); });
        });
        await s.Storage.PutAsync("hero.jpg", Bytes("x"), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));
        var backend = s.Provider.GetRequiredService<IStorageProvider>();
        var physicalKey = StorageScope.PhysicalKey(new TenantId("t1"), "hero.jpg", StorageVisibility.Public);
        Assert.True(await backend.ExistsAsync(physicalKey));

        await s.Storage.DeleteAsync("hero.jpg"); // must not throw

        Assert.False(await s.Storage.ExistsAsync("hero.jpg"));
        Assert.False(await backend.ExistsAsync(physicalKey));
        Assert.Equal("https://cdn.example.com/t1/hero.jpg", Assert.Single(purger.Urls).ToString());
        Assert.Contains(logs.Messages, m => m.Contains("CDN purge failed") && m.Contains("hero.jpg"));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("reconcile sweep"));
    }
}
