using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Mapping;

/// <summary>Registers Themia Identity entity mappings (schema-qualified table names) into a Dapper <see cref="EntityMappingRegistry"/>.</summary>
public static class IdentityDapperMappings
{
    /// <summary>Registers the Identity entity mappings. The snake_case column convention is kept; only the table names are schema-qualified.</summary>
    /// <param name="registry">The registry to populate.</param>
    public static void Apply(EntityMappingRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register<User>(EntityMapping.ForConvention<User>("identity.users", null));
        registry.Register<Role>(EntityMapping.ForConvention<Role>("identity.roles", null));
        registry.Register<UserRole>(EntityMapping.ForConvention<UserRole>("identity.user_roles", null));
        registry.Register<UserClaim>(EntityMapping.ForConvention<UserClaim>("identity.user_claims", null));
        registry.Register<RoleClaim>(EntityMapping.ForConvention<RoleClaim>("identity.role_claims", null));
        registry.Register<UserToken>(EntityMapping.ForConvention<UserToken>("identity.user_tokens", null));
    }
}
