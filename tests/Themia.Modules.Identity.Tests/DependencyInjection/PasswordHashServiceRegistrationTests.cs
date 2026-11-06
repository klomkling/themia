using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.DependencyInjection;
using Xunit;

namespace Themia.Modules.Identity.Tests.DependencyInjection;

/// <summary><see cref="IUserPasswordHashService"/> is the default <c>UserService</c> seen through a second interface
/// (coord #0202): the same scoped instance, and a clear error for an adopter whose own <see cref="IUserService"/>
/// does not implement it.</summary>
public class PasswordHashServiceRegistrationTests
{
    [Fact]
    public void Resolves_to_the_same_instance_as_IUserService()
    {
        using var scope = BuildScope(_ => DispatchProxy.Create<IUserServiceWithHash, InertProxy>());

        Assert.Same(scope.ServiceProvider.GetRequiredService<IUserService>(),
            scope.ServiceProvider.GetRequiredService<IUserPasswordHashService>());
    }

    [Fact]
    public void A_replaced_IUserService_without_the_interface_fails_loudly_at_resolve_time()
    {
        using var scope = BuildScope(_ => DispatchProxy.Create<IUserService, InertProxy>());

        var ex = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IUserPasswordHashService>());

        Assert.Contains(nameof(IUserPasswordHashService), ex.Message);
    }

    // The adopter's service is registered first, so AddThemiaIdentityCore's TryAdd leaves it in place.
    private static IServiceScope BuildScope(Func<IServiceProvider, IUserService> adoptersService)
    {
        var services = new ServiceCollection();
        services.AddScoped(adoptersService);
        services.AddThemiaIdentityCore();
        return services.BuildServiceProvider().CreateScope();
    }

    public interface IUserServiceWithHash : IUserService, IUserPasswordHashService;

    public class InertProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();
    }
}
