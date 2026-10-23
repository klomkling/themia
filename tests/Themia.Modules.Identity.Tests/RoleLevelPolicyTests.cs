using Themia.Modules.Identity.Abstractions;
using Xunit;

namespace Themia.Modules.Identity.Tests;

public class RoleLevelPolicyTests
{
    [Theory]
    [InlineData(RoleLevel.Level1, RoleLevel.Level2, true)]
    [InlineData(RoleLevel.Level2, RoleLevel.Level5, true)]
    [InlineData(RoleLevel.Level3, RoleLevel.Level3, false)]   // equal level is refused...
    [InlineData(RoleLevel.Level1, RoleLevel.Level1, true)]    // ...except between top-level users
    [InlineData(RoleLevel.Level4, RoleLevel.Level2, false)]
    [InlineData(RoleLevel.Level5, RoleLevel.Level1, false)]
    public void CanActOn_allows_only_strictly_lower_targets_except_between_top_level_users(RoleLevel actor, RoleLevel target, bool expected) =>
        Assert.Equal(expected, RoleLevelPolicy.CanActOn(actor, target));

    [Theory]
    [InlineData(RoleLevel.Level1, RoleLevel.Level1, true)]    // top level may grant any role
    [InlineData(RoleLevel.Level1, RoleLevel.Level5, true)]
    [InlineData(RoleLevel.Level2, RoleLevel.Level3, true)]
    [InlineData(RoleLevel.Level2, RoleLevel.Level2, false)]   // own level is not below own level
    [InlineData(RoleLevel.Level3, RoleLevel.Level1, false)]
    [InlineData(RoleLevel.Level5, RoleLevel.Level5, false)]
    public void CanGrant_allows_only_roles_below_the_actor_unless_the_actor_is_top_level(RoleLevel actor, RoleLevel role, bool expected) =>
        Assert.Equal(expected, RoleLevelPolicy.CanGrant(actor, role));

    [Fact]
    public void Level1_is_the_highest_level_and_has_the_smallest_number()
    {
        Assert.Equal(1, (int)RoleLevel.Level1);
        Assert.Equal(5, (int)RoleLevel.Level5);
    }
}
