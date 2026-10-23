namespace Themia.Modules.Identity.Abstractions;

/// <summary>The shared rank of a role. A lower number is a higher level, so <see cref="Level1"/> is the top.</summary>
public enum RoleLevel
{
    /// <summary>Super admin.</summary>
    Level1 = 1,

    /// <summary>Admin.</summary>
    Level2 = 2,

    /// <summary>Back-office staff, such as support or accounting.</summary>
    Level3 = 3,

    /// <summary>A member with management rights, such as a manager.</summary>
    Level4 = 4,

    /// <summary>An ordinary member, such as an agent, viewer or customer. The default for a role.</summary>
    Level5 = 5,
}
