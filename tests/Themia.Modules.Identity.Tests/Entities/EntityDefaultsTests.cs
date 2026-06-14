using Themia.Modules.Identity.Abstractions.Entities;
using Xunit;

namespace Themia.Modules.Identity.Tests.Entities;

public class EntityDefaultsTests
{
    [Fact]
    public void SetId_assigns_identifier()
    {
        var user = new User();
        var id = Guid.NewGuid();

        user.SetId(id);

        Assert.Equal(id, user.Id);
    }

    [Fact]
    public void New_user_defaults_are_safe()
    {
        var user = new User();

        Assert.False(user.IsDeleted);
        Assert.True(user.IsActive);            // created enabled
        Assert.Equal(0, user.AccessFailedCount);
        Assert.Null(user.LockoutEnd);
        Assert.False(user.EmailConfirmed);
        Assert.False(user.TwoFactorEnabled);
        Assert.Null(user.TenantId);            // null == platform until a tenant is stamped
    }

    [Fact]
    public void UserToken_purpose_roundtrips()
    {
        var token = new UserToken { Purpose = TokenPurpose.PasswordReset };
        Assert.Equal(TokenPurpose.PasswordReset, token.Purpose);
    }
}
