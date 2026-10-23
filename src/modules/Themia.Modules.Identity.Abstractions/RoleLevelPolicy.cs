namespace Themia.Modules.Identity.Abstractions;

/// <summary>The act-on and grant rules over <see cref="RoleLevel"/>. Pure functions: read the levels from the database
/// (<see cref="IUserLevelReader"/>) at request time, never from token claims.</summary>
public static class RoleLevelPolicy
{
    /// <summary>Whether an actor may set the password of, disable, delete or change the roles of a target user:
    /// only a strictly lower target, except that top-level users may act on each other.</summary>
    /// <param name="actor">The actor's level.</param>
    /// <param name="target">The target user's level.</param>
    public static bool CanActOn(RoleLevel actor, RoleLevel target) =>
        actor < target || (actor == RoleLevel.Level1 && target == RoleLevel.Level1);

    /// <summary>Whether an actor may grant or remove a role: only a role strictly below the actor's own level, except
    /// that a top-level actor may grant any role.</summary>
    /// <param name="actor">The actor's level.</param>
    /// <param name="role">The level of the role being granted or removed.</param>
    public static bool CanGrant(RoleLevel actor, RoleLevel role) =>
        actor == RoleLevel.Level1 || actor < role;
}
