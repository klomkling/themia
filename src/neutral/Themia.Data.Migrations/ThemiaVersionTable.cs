using System.Globalization;
using System.Reflection;
using System.Text;
using FluentMigrator.Runner.VersionTableInfo;

namespace Themia.Data.Migrations;

/// <summary>
/// The version ledger for one Themia migration assembly — <c>themia_version_&lt;assembly&gt;</c>, never
/// FluentMigrator's shared <c>VersionInfo</c>.
/// </summary>
/// <remarks>
/// <b>Two runners against one database must not share a ledger.</b> Until this existed,
/// <see cref="ThemiaMigrations"/> configured no version table, so every Themia migration recorded itself
/// in the default <c>VersionInfo</c> — the same table the consumer's own FluentMigrator runner uses.
/// FluentMigrator skips any version already listed and neither runner can see the other's assemblies, so
/// a duplicate number made one migration of the pair <b>a silent no-op</b>: no exception, no log line, no
/// failed deploy, and a missing table discovered whenever something first touched it. ezy-assets lost
/// <c>data_protection_keys</c> to this in production for fifteen days (coord #0078).
/// <para>
/// Numbering is <c>yyyyMMddNNNN</c> on both sides, so a collision needs only two teams writing a
/// migration on the same day — and a deployed number is frozen, because migrations are forward-only. By
/// the time anyone observes it, neither side can renumber.
/// </para>
/// <para>
/// <b>Themia also collided with itself.</b> <c>Themia.Exceptional</c>'s <c>AddRequestContextColumn</c>
/// and <c>Themia.Modules.Notifications</c>' <c>NotificationsSchemaMigration</c> both carried
/// <c>202606220001</c>, so a host taking both packages silently lost one of them with no consumer
/// involved at all. A ledger per assembly makes a repeated number across modules mean nothing, which is
/// what it should always have meant.
/// </para>
/// </remarks>
public sealed class ThemiaVersionTable : IVersionTableMetaData
{
    // PostgreSQL caps an identifier at 63 characters and the longest Themia assembly leaves room, but a
    // future one might not — so the token is truncated rather than left to be silently cut by the engine
    // into a name that collides with its neighbour.
    private const int MaxTokenLength = 40;

    /// <summary>The prefix every Themia version table carries.</summary>
    public const string Prefix = "themia_version_";

    /// <summary>Creates the metadata for one migration assembly's ledger.</summary>
    /// <param name="migrationAssembly">The assembly whose migrations this ledger records.</param>
    /// <exception cref="ArgumentNullException"><paramref name="migrationAssembly"/> is <see langword="null"/>.</exception>
    public ThemiaVersionTable(Assembly migrationAssembly)
    {
        ArgumentNullException.ThrowIfNull(migrationAssembly);
        TableName = Prefix + Tokenize(migrationAssembly.GetName().Name ?? "unknown");
    }

    /// <inheritdoc />
    public string TableName { get; }

    /// <inheritdoc />
    public string SchemaName => string.Empty;

    /// <inheritdoc />
    public string ColumnName => "Version";

    /// <inheritdoc />
    public string DescriptionColumnName => "Description";

    /// <inheritdoc />
    public string UniqueIndexName => TableName + "_unique";

    /// <inheritdoc />
    public string AppliedOnColumnName => "AppliedOn";

    /// <inheritdoc />
    public object ApplicationContext { get; set; } = new();

    /// <inheritdoc />
    public bool OwnsSchema => false;

    /// <summary>
    /// Whether the version table gets a primary key on its version column. <see langword="false"/>, which
    /// is FluentMigrator's own default and what the shared <c>VersionInfo</c> has always used.
    /// </summary>
    /// <remarks>
    /// This shipped as <see langword="true"/> for one revision, on the reasoning that a duplicate version
    /// inside one module's ledger should be a constraint violation rather than a silent second row. It
    /// does not work: with a primary key, FluentMigrator still emits the unique index as well, and the
    /// second run of the same migration assembly fails with "an index with name … already exists" — on
    /// both engines, for every assembly. The replay suite caught it, which is the entire reason that
    /// suite exists. The unique index alone gives the same protection.
    /// </remarks>
    public bool CreateWithPrimaryKey => false;

    /// <summary>Renders an assembly name as the table-name token it contributes.</summary>
    /// <param name="assemblyName">The assembly's simple name.</param>
    /// <returns>A lowercase, underscore-separated token.</returns>
    public static string Tokenize(string assemblyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);

        var builder = new StringBuilder(assemblyName.Length);
        foreach (var character in assemblyName)
        {
            builder.Append(char.IsLetterOrDigit(character)
                ? char.ToLower(character, CultureInfo.InvariantCulture)
                : '_');
        }

        var token = builder.ToString();
        return token.Length <= MaxTokenLength ? token : token[^MaxTokenLength..];
    }
}
