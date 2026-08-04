namespace Themia.Challenges.Internal;

/// <summary>
/// Materializes one row of the <c>challenges</c> table. Property names are PascalCase while the shared
/// schema (<see cref="Migrations.ChallengeSchemaMigration"/>) is snake_case, and every shipped dialect's
/// <c>SELECT *</c> statements return the raw column names verbatim — <see cref="ChallengeService"/>'s
/// static constructor registers a Dapper type map that folds out underscores so <c>tenant_id</c> binds
/// to <see cref="TenantId"/> without every dialect having to alias its columns.
/// </summary>
internal sealed class ChallengeRow
{
    public Guid Id { get; set; }

    public string? TenantId { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Purpose { get; set; } = string.Empty;

    public string SecretHash { get; set; } = string.Empty;

    public string SecretSalt { get; set; } = string.Empty;

    public string? TokenHash { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }
}
