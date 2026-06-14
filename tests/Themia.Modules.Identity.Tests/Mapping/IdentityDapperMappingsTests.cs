using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Mapping;
using Xunit;

namespace Themia.Modules.Identity.Tests.Mapping;

public class IdentityDapperMappingsTests
{
    [Fact]
    public void Maps_user_to_schema_qualified_table_with_snake_case_columns()
    {
        var registry = new EntityMappingRegistry();
        IdentityDapperMappings.Apply(registry);

        var mapping = registry.For<User>();
        Assert.Equal("identity.users", mapping.Table);
        Assert.Equal("normalized_user_name", mapping.Column(nameof(User.NormalizedUserName)));
        Assert.Equal("tenant_id", mapping.Column(nameof(User.TenantId)));
    }

    [Fact]
    public void Maps_all_identity_entities()
    {
        var registry = new EntityMappingRegistry();
        IdentityDapperMappings.Apply(registry);

        Assert.Equal("identity.roles", registry.For<Role>().Table);
        Assert.Equal("identity.user_roles", registry.For<UserRole>().Table);
        Assert.Equal("identity.user_claims", registry.For<UserClaim>().Table);
        Assert.Equal("identity.role_claims", registry.For<RoleClaim>().Table);
        Assert.Equal("identity.user_tokens", registry.For<UserToken>().Table);
    }
}
