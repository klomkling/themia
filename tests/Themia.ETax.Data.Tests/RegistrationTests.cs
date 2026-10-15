using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Sequences;
using Xunit;

namespace Themia.ETax.Data.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void AddThemiaETaxSequenceNumbers_ShouldResolveFromAScopeOverTheScopedAllocator_WithScopeValidationOn()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITransactionalSequenceAllocator, NullAllocator>();
        services.AddThemiaETaxSequenceNumbers(o => o.Padding = 4);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        var source = scope.ServiceProvider.GetRequiredService<ITaxDocumentNumberSource>();

        Assert.IsType<SequenceTaxDocumentNumberSource>(source);
        Assert.Same(source, scope.ServiceProvider.GetRequiredService<ITaxDocumentNumberSource>());
    }

    [Fact]
    public void AddThemiaETaxSequenceNumbers_ShouldFailAtRegistration_WhenTheOptionsAreInvalid() =>
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddThemiaETaxSequenceNumbers(o => o.Padding = 0));

    private sealed class NullAllocator : ITransactionalSequenceAllocator
    {
        public Task<long> NextInTransactionAsync(System.Data.Common.DbTransaction tx, string sequenceKey, CancellationToken ct = default) => Task.FromResult(1L);

        public Task<long> NextHostInTransactionAsync(System.Data.Common.DbTransaction tx, string sequenceKey, CancellationToken ct = default) => Task.FromResult(1L);
    }
}
