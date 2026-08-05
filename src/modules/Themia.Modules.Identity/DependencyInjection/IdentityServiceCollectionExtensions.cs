using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Hashing;
using Themia.Modules.Identity.Principal;
using Themia.Modules.Identity.Services;

namespace Themia.Modules.Identity.DependencyInjection;

/// <summary>Registers Themia Identity services and authorization integration.</summary>
public static class IdentityServiceCollectionExtensions
{
    private const string RenamedMessage =
        "AddThemiaIdentityServices no longer wires a data peer and has been renamed to "
        + "AddThemiaIdentityCore. Dapper adopters must call AddThemiaIdentityDapper "
        + "(package Themia.Modules.Identity.Dapper) and EF Core adopters AddThemiaIdentityEFCore "
        + "(package Themia.Modules.Identity.EFCore); either one calls the core for you. Call "
        + "AddThemiaIdentityCore directly only when you supply your own IRepository implementations. "
        + "See MIGRATION.md, 'Themia.Modules.Identity splits into engine packages'.";

    /// <summary>Registers the Identity stores, services, password hasher, and options.</summary>
    /// <remarks>
    /// <b>Engine-agnostic: this registers nothing that knows about Dapper or EF Core.</b> Your data peer's
    /// wiring comes from the matching engine package — <c>AddThemiaIdentityDapper</c> in
    /// <c>Themia.Modules.Identity.Dapper</c>, or <c>AddThemiaIdentityEFCore</c> in
    /// <c>Themia.Modules.Identity.EFCore</c> — and each of those calls this method for you. Call this
    /// directly only if you are supplying your own <c>IRepository</c> implementations.
    /// <para>
    /// <b>It also applies no schema.</b> The core carries the FluentMigrator migration classes (see
    /// <see cref="Migrations.IdentityMigrations"/>) but no runner, so calling this on its own creates no
    /// <c>identity</c> tables. Either engine module runs them on startup; a caller that takes neither must
    /// run <see cref="Migrations.IdentityMigrations.Assembly"/> through a migration runner of its own.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddThemiaIdentityCore(this IServiceCollection services, Action<IdentityModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new IdentityModuleOptions();
        configure?.Invoke(options);
        options.Validate();
        services.TryAddSingleton(options);

        return RegisterCoreServices(services);
    }

    /// <summary>
    /// Same as the public overload but taking a pre-built options instance. Internal rather than public so
    /// the options-instance form does not become a second public overload — the analyzer requires the
    /// overload carrying optional parameters to have the most parameters (RS0027), and adopters have the
    /// lambda. The engine packages reach it through <c>InternalsVisibleTo</c>.
    /// </summary>
    internal static IServiceCollection AddThemiaIdentityCore(this IServiceCollection services, IdentityModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();
        services.TryAddSingleton(options);

        return RegisterCoreServices(services);
    }

    /// <summary>Renamed to <see cref="AddThemiaIdentityCore(IServiceCollection, Action{IdentityModuleOptions})"/>.</summary>
    /// <remarks>
    /// A compile error rather than a silent forward, deliberately. Until the engine split this method
    /// contributed the Identity mappings to a Dapper <c>EntityMappingRegistry</c> if it found one; it no
    /// longer does. Keeping the name callable would let an existing Dapper bootstrap recompile clean and
    /// then query unqualified <c>users</c> instead of <c>identity.users</c> — no error, no log, an auth
    /// outage on first login. The rename is the mechanical signal that the call site must change.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configuration.</param>
    /// <returns>The same service collection.</returns>
    [Obsolete(RenamedMessage, error: true)]
    public static IServiceCollection AddThemiaIdentityServices(this IServiceCollection services, Action<IdentityModuleOptions>? configure = null)
        => services.AddThemiaIdentityCore(configure);

    /// <summary>Renamed to <see cref="AddThemiaIdentityCore(IServiceCollection, IdentityModuleOptions)"/>.</summary>
    /// <remarks>See the other overload for why this is an error rather than a forward.</remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The validated module options to register.</param>
    /// <returns>The same service collection.</returns>
    [Obsolete(RenamedMessage, error: true)]
    public static IServiceCollection AddThemiaIdentityServices(this IServiceCollection services, IdentityModuleOptions options)
        => services.AddThemiaIdentityCore(options);

    private static IServiceCollection RegisterCoreServices(IServiceCollection services)
    {
        // Services here depend on ILogger<T>; ensure logging is resolvable even on a bare
        // ServiceCollection (no generic host). AddLogging is idempotent/TryAdd-based.
        services.AddLogging();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPasswordHasher, Argon2idPasswordHasher>();

        // TryAdd so an adopter can register their own BEFORE this call and keep it. The default only
        // strips formatting — it deliberately does not decide that 08… and +668… are the same number,
        // because that is only true given a region the framework cannot know. See the interface.
        services.TryAddSingleton<IPhoneNumberNormalizer, FormattingOnlyPhoneNumberNormalizer>();

        services.TryAddScoped<IUserService, UserService>();
        services.TryAddScoped<IRoleService, RoleService>();
        services.TryAddScoped<IClaimService, ClaimService>();
        services.TryAddScoped<IUserTokenService, UserTokenService>();
        services.TryAddScoped<IRefreshTokenService, RefreshTokenService>();
        services.TryAddScoped<IExternalLoginService, ExternalLoginService>();
        services.TryAddScoped<IClaimsPrincipalFactory, ClaimsPrincipalFactory>();

        return services;
    }

    /// <summary>Registers the current-user principal and replaces the framework's null audit-user accessor.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddThemiaIdentityAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.TryAddScoped<ICurrentUser, CurrentUser>();

        // Override the framework's NullCurrentUserAccessor so audit columns capture the real user.
        services.RemoveAll<ICurrentUserAccessor>();
        services.AddScoped<ICurrentUserAccessor, IdentityCurrentUserAccessor>();
        return services;
    }
}
