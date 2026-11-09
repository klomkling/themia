using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.SecondFactor.DependencyInjection;
using Xunit;

namespace Themia.Modules.Identity.SecondFactor.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void AddThemiaIdentitySecondFactor_ShouldReplaceTheDefaultNotRequiredPolicy()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISecondFactorPolicy>(NotRequiredSecondFactorPolicy.Instance);

        services.AddThemiaIdentitySecondFactor();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ISecondFactorPolicy));
        Assert.Equal(typeof(MfaSecondFactorPolicy), descriptor.ImplementationType);
    }
}
