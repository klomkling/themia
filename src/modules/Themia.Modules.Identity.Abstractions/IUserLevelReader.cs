namespace Themia.Modules.Identity.Abstractions;

/// <summary>Reads a user's effective <see cref="RoleLevel"/> from the database.</summary>
public interface IUserLevelReader
{
    /// <summary>The highest level (smallest number) among the user's roles, read from the database. A user with no
    /// roles, an unknown user, or one outside the ambient tenant and platform scope is <see cref="RoleLevel.Level5"/>,
    /// so resolve the user first when "not found" must be told apart.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    Task<RoleLevel> GetLevelAsync(Guid userId, CancellationToken cancellationToken = default);
}
