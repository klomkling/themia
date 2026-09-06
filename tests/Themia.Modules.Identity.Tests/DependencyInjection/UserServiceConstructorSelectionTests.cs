using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Hashing;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.DependencyInjection;

/// <summary>Proves <see cref="UserService"/>'s greedy (observer + logger) constructor stays the one
/// Microsoft DI picks even when nothing registers <see cref="ILogger{TCategoryName}"/> for it — e.g. a
/// composition root that wires <see cref="UserService"/> directly rather than through
/// <c>AddThemiaIdentityCore</c> (which self-registers logging). Before the fix, an unresolvable
/// <c>ILogger&lt;UserService&gt;</c> made DI silently fall back to the 8-parameter back-compat
/// constructor, and every <see cref="IIdentityEventObserver.OnUserMutatedAsync"/> notification
/// disappeared with no error.</summary>
public sealed class UserServiceConstructorSelectionTests
{
    [Fact]
    public async Task UserService_wires_registered_observers_even_without_ILogger_registered()
    {
        var store = new List<User>();
        var observer = new RecordingIdentityEventObserver();

        var services = new ServiceCollection();
        services.AddSingleton<IRepository<User, Guid>>(
            new FakeRepository<User>(store, u => u.Id) { AmbientTenant = new TenantId("acme") });
        services.AddSingleton<IUnitOfWork>(new FakeUnitOfWork());
        services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new IdentityModuleOptions());
        services.AddSingleton<IDataFilterScope>(new DataFilterScope());
        services.AddSingleton<IPhoneNumberNormalizer, FormattingOnlyPhoneNumberNormalizer>();
        services.AddSingleton<IUserLifecycleHooks>(new RecordingUserLifecycleHooks());
        services.AddSingleton<IIdentityEventObserver>(observer);
        services.AddScoped<IUserService, UserService>();
        // Deliberately no services.AddLogging(): the fix must not rely on logging being registered.

        await using var provider = services.BuildServiceProvider();
        var sut = provider.GetRequiredService<IUserService>();

        var created = await sut.CreateAsync("dana", "pw");
        await sut.SetPasswordAsync(created.UserId!.Value, "new-pw");

        Assert.Contains(nameof(IIdentityEventObserver.OnUserMutatedAsync), observer.Calls);
    }
}
