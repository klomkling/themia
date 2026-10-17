using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;

namespace Themia.Framework.Core.Tests.Tenancy;

public class TenantContextAccessorScopeTests : IDisposable
{
    private readonly TenantId? original = TenantContextAccessor.CurrentTenantId;

    public void Dispose() => TenantContextAccessor.CurrentTenantId = original;

    [Fact]
    public void BeginScope_SetsTheTenantForTheBlockAndRestoresIt()
    {
        TenantContextAccessor.CurrentTenantId = new TenantId("outer");

        using (TenantContextAccessor.BeginScope(new TenantId("acme")))
        {
            Assert.Equal(new TenantId("acme"), TenantContextAccessor.CurrentTenantId);
        }

        Assert.Equal(new TenantId("outer"), TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public void BeginScope_CanScopeToNoTenant()
    {
        TenantContextAccessor.CurrentTenantId = new TenantId("outer");

        using (TenantContextAccessor.BeginScope(null))
        {
            Assert.Null(TenantContextAccessor.CurrentTenantId);
        }

        Assert.Equal(new TenantId("outer"), TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public void BeginScope_RestoresTheTenantWhenTheBlockThrows()
    {
        TenantContextAccessor.CurrentTenantId = new TenantId("outer");

        Action action = () =>
        {
            using (TenantContextAccessor.BeginScope(new TenantId("acme")))
            {
                throw new InvalidOperationException("boom");
            }
        };

        Assert.Throws<InvalidOperationException>(action);

        Assert.Equal(new TenantId("outer"), TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public void BeginScope_NestedScopesRestoreInOrder_AndDisposeTwiceIsHarmless()
    {
        TenantContextAccessor.CurrentTenantId = new TenantId("outer");
        var outerScope = TenantContextAccessor.BeginScope(new TenantId("a"));
        var innerScope = TenantContextAccessor.BeginScope(new TenantId("b"));

        innerScope.Dispose();
        Assert.Equal(new TenantId("a"), TenantContextAccessor.CurrentTenantId);
        innerScope.Dispose();
        Assert.Equal(new TenantId("a"), TenantContextAccessor.CurrentTenantId);
        outerScope.Dispose();
        Assert.Equal(new TenantId("outer"), TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public async Task BeginScope_DoesNotLeakToTheCallerAcrossAnAwait()
    {
        TenantContextAccessor.CurrentTenantId = new TenantId("outer");

        async Task InsideAsync()
        {
            using (TenantContextAccessor.BeginScope(new TenantId("acme")))
            {
                await Task.Yield();
                Assert.Equal(new TenantId("acme"), TenantContextAccessor.CurrentTenantId);
            }
        }

        await InsideAsync();

        Assert.Equal(new TenantId("outer"), TenantContextAccessor.CurrentTenantId);
    }
}
