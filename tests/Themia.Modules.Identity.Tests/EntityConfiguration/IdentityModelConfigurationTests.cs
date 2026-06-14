using Microsoft.EntityFrameworkCore;
using Themia.Framework.Data.EFCore;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.EntityConfiguration;
using Xunit;

namespace Themia.Modules.Identity.Tests.EntityConfiguration;

public class IdentityModelConfigurationTests
{
    // A minimal ThemiaDbContext-derived context that registers the Identity model.
    private sealed class TestIdentityDbContext(DbContextOptions options) : ThemiaDbContext(options, null, null)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyThemiaIdentity();
            base.OnModelCreating(modelBuilder);
        }
    }

    private static TestIdentityDbContext BuildContext()
    {
        // UseNpgsql builds the model without opening a connection.
        var options = new DbContextOptionsBuilder<TestIdentityDbContext>()
            .UseNpgsql("Host=localhost;Database=identity_model_test")
            .Options;
        return new TestIdentityDbContext(options);
    }

    [Fact]
    public void User_maps_to_identity_users_with_snake_case_columns()
    {
        using var ctx = BuildContext();
        var entity = ctx.Model.FindEntityType(typeof(User))!;

        Assert.Equal("identity", entity.GetSchema());
        Assert.Equal("users", entity.GetTableName());
        Assert.Equal("normalized_user_name", entity.FindProperty(nameof(User.NormalizedUserName))!.GetColumnName());
        Assert.Equal("tenant_id", entity.FindProperty(nameof(User.TenantId))!.GetColumnName());     // framework-mapped
        Assert.Equal("is_deleted", entity.FindProperty(nameof(User.IsDeleted))!.GetColumnName());   // framework-mapped
    }

    [Fact]
    public void UserRole_maps_all_columns_to_identity_user_roles()
    {
        using var ctx = BuildContext();
        var entity = ctx.Model.FindEntityType(typeof(UserRole))!;

        Assert.Equal("identity", entity.GetSchema());
        Assert.Equal("user_roles", entity.GetTableName());
        Assert.Equal("user_id", entity.FindProperty(nameof(UserRole.UserId))!.GetColumnName());
        Assert.Equal("role_id", entity.FindProperty(nameof(UserRole.RoleId))!.GetColumnName());
    }

    [Fact]
    public void UserToken_purpose_maps_to_int_column()
    {
        using var ctx = BuildContext();
        var entity = ctx.Model.FindEntityType(typeof(UserToken))!;
        Assert.Equal("user_tokens", entity.GetTableName());
        Assert.Equal("purpose", entity.FindProperty(nameof(UserToken.Purpose))!.GetColumnName());
    }
}
