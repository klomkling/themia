using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Themia.ETax.Tests;

public sealed class DocumentRegistrationTests
{
    private sealed class StubNumbers : ITaxDocumentNumberSource
    {
        public Task<string> NextNumberAsync(System.Data.Common.DbTransaction tx, string sellerId, TaxDocumentKind kind, DateOnly issueDate, CancellationToken ct = default) =>
            Task.FromResult("N");
    }

    private sealed class StubMailer : ITaxDocumentMailer
    {
        public Task<TaxDocumentSendResult> SendAsync(TaxDocumentEmail email, CancellationToken ct = default) => Task.FromResult(TaxDocumentSendResult.Sent);
    }

    [Fact]
    public void AddThemiaETaxDocuments_ShouldResolveTheServiceAndTheSender_WhenTheAppSuppliesThePorts()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITaxDocumentStore>(new InMemoryTaxDocumentStore());
        services.AddSingleton<ITaxDocumentNumberSource, StubNumbers>();
        services.AddSingleton<ITaxDocumentMailer, StubMailer>();
        services.AddThemiaETaxDocuments(o => o.RevenueDepartmentAddress = "rd@example.test");
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetRequiredService<TaxDocumentService>());
        Assert.NotNull(sp.GetRequiredService<TaxDocumentSender>());
        Assert.NotNull(sp.GetRequiredService<ITaxDocumentFailureHandler>());
    }

    [Fact]
    public void AddThemiaETaxDocuments_ShouldFailAtRegistration_WhenTheRevenueDepartmentAddressIsMissing() =>
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddThemiaETaxDocuments(_ => { }));

    [Theory]
    [InlineData(0, "MaxAttempts")]
    [InlineData(-1, "BatchSize")]
    public void Options_ShouldRejectNonPositiveLimits(int value, string which)
    {
        var options = new TaxDocumentSendOptions { RevenueDepartmentAddress = "rd@example.test" };
        if (which == "MaxAttempts") options.MaxAttempts = value; else options.BatchSize = value;

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddThemiaETaxDocuments(o =>
        {
            o.RevenueDepartmentAddress = options.RevenueDepartmentAddress;
            o.MaxAttempts = options.MaxAttempts;
            o.BatchSize = options.BatchSize;
        }));
    }
}
