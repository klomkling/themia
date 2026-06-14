using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Themia.Modules.Identity.Principal;
using Xunit;

namespace Themia.Modules.Identity.Tests.Principal;

public class CurrentUserTests
{
    private static IHttpContextAccessor Accessor(ClaimsPrincipal? user)
    {
        var ctx = new DefaultHttpContext();
        if (user is not null)
        {
            ctx.User = user;
        }
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static ClaimsPrincipal Authenticated(Guid id, string? tenant, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id.ToString()),
            new(ClaimTypes.Name, "alice"),
        };
        if (tenant is not null)
        {
            claims.Add(new Claim(IdentityClaimTypes.TenantId, tenant));
        }
        else
        {
            // A platform principal carries the positive marker, mirroring ClaimsPrincipalFactory.
            claims.Add(new Claim(IdentityClaimTypes.IsPlatform, "true"));
        }
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role));
    }

    private static ClaimsPrincipal AuthenticatedWithoutMarkers(Guid id)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id.ToString()),
            new(ClaimTypes.Name, "alice"),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role));
    }

    [Fact]
    public void Unauthenticated_when_no_user()
    {
        var sut = new CurrentUser(Accessor(null));
        Assert.False(sut.IsAuthenticated);
        Assert.Null(sut.UserId);
    }

    [Fact]
    public void Reads_tenant_user_identity_and_roles()
    {
        var id = Guid.NewGuid();
        var sut = new CurrentUser(Accessor(Authenticated(id, "acme", "Admin", "Editor")));

        Assert.True(sut.IsAuthenticated);
        Assert.Equal(id, sut.UserId);
        Assert.Equal("acme", sut.TenantId);
        Assert.False(sut.IsPlatform);
        Assert.True(sut.IsInRole("Admin"));
        Assert.Contains("Editor", sut.Roles);
    }

    [Fact]
    public void Platform_user_has_null_tenant_and_is_platform()
    {
        var sut = new CurrentUser(Accessor(Authenticated(Guid.NewGuid(), tenant: null)));
        Assert.True(sut.IsAuthenticated);
        Assert.Null(sut.TenantId);
        Assert.True(sut.IsPlatform);
    }

    [Fact]
    public void Principal_without_platform_claim_is_not_platform()
    {
        // Authenticated, no tenant claim, no is_platform marker — must NOT read as platform (fail-closed).
        var sut = new CurrentUser(Accessor(AuthenticatedWithoutMarkers(Guid.NewGuid())));
        Assert.True(sut.IsAuthenticated);
        Assert.Null(sut.TenantId);
        Assert.False(sut.IsPlatform);
    }

    [Fact]
    public void Audit_accessor_returns_subject_id_string()
    {
        var id = Guid.NewGuid();
        var accessor = new IdentityCurrentUserAccessor(Accessor(Authenticated(id, "acme")));
        Assert.Equal(id.ToString(), accessor.UserId);
    }

    [Fact]
    public void Audit_accessor_returns_null_when_unauthenticated()
    {
        var accessor = new IdentityCurrentUserAccessor(Accessor(null));
        Assert.Null(accessor.UserId);
    }
}
