using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Sequences;
using Xunit;

namespace Themia.ETax.Withholding.Data.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void AddThemiaETaxWithholdingSequenceNumbers_ShouldResolveFromAScopeOverTheScopedAllocator_WithScopeValidationOn()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITransactionalSequenceAllocator, NullAllocator>();
        services.AddThemiaETaxWithholdingSequenceNumbers(o => o.Shape = WithholdingNumberShape.YearSeq);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        var source = scope.ServiceProvider.GetRequiredService<IWithholdingNumberSource>();

        Assert.IsType<SequenceWithholdingNumberSource>(source);
    }

    [Fact]
    public void AddThemiaETaxWithholdingSequenceNumbers_ShouldFailAtRegistration_WhenTheOptionsAreInvalid() =>
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddThemiaETaxWithholdingSequenceNumbers(o => o.Prefix = " "));

    [Fact]
    public void AddThemiaETaxWithholdingSequenceNumbers_ShouldRejectANullCollection() =>
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddThemiaETaxWithholdingSequenceNumbers());

    private sealed class NullAllocator : ITransactionalSequenceAllocator
    {
        public Task<long> NextInTransactionAsync(System.Data.Common.DbTransaction tx, string sequenceKey, CancellationToken ct = default) => Task.FromResult(1L);

        public Task<long> NextHostInTransactionAsync(System.Data.Common.DbTransaction tx, string sequenceKey, CancellationToken ct = default) => Task.FromResult(1L);
    }
}
