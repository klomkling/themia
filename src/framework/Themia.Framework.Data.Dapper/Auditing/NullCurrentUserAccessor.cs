using Themia.Framework.Data.Abstractions.Auditing;

namespace Themia.Framework.Data.Dapper.Auditing;

/// <summary>Default audit-user source: no user. Replace via DI to stamp CreatedBy/etc.</summary>
public sealed class NullCurrentUserAccessor : ICurrentUserAccessor
{
    /// <inheritdoc />
    public string? UserId => null;
}
