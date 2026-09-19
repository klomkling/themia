using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>The one (provider, subject) → link lookup, shared so that "who owns this identity" has the same
/// answer whether a sign-in or a caller of <c>FindUserByLoginAsync</c> is asking.</summary>
internal static class ExternalLoginLookup
{
    /// <summary>The link for (provider, subject): the tenant-scoped link first, then — when
    /// <see cref="IdentityModuleOptions.AllowPlatformLogin"/> is set — the platform (global) link. Returns
    /// <see langword="null"/> when no link exists. The platform fallback matters on a data layer that does
    /// not surface global (<c>tenant_id IS NULL</c>) rows to a tenant scope (e.g. Dapper with the default
    /// <c>IncludeGlobalRecordsForTenants=false</c>).</summary>
    public static async Task<(ExternalLoginLink Link, bool IsPlatform)?> FindLinkAsync(
        IReadRepository<ExternalLoginLink, Guid> links,
        IdentityModuleOptions options,
        string provider,
        string subject,
        CancellationToken cancellationToken)
    {
        var link = await links
            .FirstOrDefaultAsync(new ExternalLoginByProviderKeySpec(provider, subject), cancellationToken)
            .ConfigureAwait(false);
        if (link is not null)
        {
            return (link, false);
        }

        if (!options.AllowPlatformLogin)
        {
            return null;
        }

        var platformLink = await links
            .FirstOrDefaultAsync(new PlatformExternalLoginByProviderKeySpec(provider, subject), cancellationToken)
            .ConfigureAwait(false);
        return platformLink is null ? null : (platformLink, true);
    }

    /// <summary>The user a (provider, subject) link belongs to, resolved the same way the link was found.
    /// Throws when a link exists but its user does not resolve — that is corrupt data, not "unknown".</summary>
    public static async Task<User?> FindUserAsync(
        IReadRepository<User, Guid> users,
        IReadRepository<ExternalLoginLink, Guid> links,
        IdentityModuleOptions options,
        string provider,
        string subject,
        CancellationToken cancellationToken)
    {
        if (await FindLinkAsync(links, options, provider, subject, cancellationToken).ConfigureAwait(false)
            is not { } found)
        {
            return null;
        }

        var (link, isPlatform) = found;
        var user = isPlatform
            ? await users.FirstOrDefaultAsync(new PlatformUserByIdSpec(link.UserId), cancellationToken).ConfigureAwait(false)
            : await IdentityScope.ResolveUserAsync(users, link.UserId, cancellationToken).ConfigureAwait(false);

        return user ?? throw new InvalidOperationException(
            $"External link '{provider}:{subject}' references user '{link.UserId}', which does not resolve in scope.");
    }
}
