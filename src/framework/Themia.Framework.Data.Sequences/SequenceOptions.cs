namespace Themia.Framework.Data.Sequences;

/// <summary>Configuration for <see cref="ISequenceProvider"/>.</summary>
public sealed class SequenceOptions
{
    /// <summary>
    /// Connection string the allocator opens its OWN connection with. Normally the same one the app gives
    /// the migration runner.
    /// </summary>
    /// <remarks>
    /// Deliberately a separate setting rather than borrowing the data peer's connection: borrowing would
    /// put the allocation inside the caller's ambient transaction, and a rollback would then reissue the
    /// number to the next caller.
    /// </remarks>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>The database engine. No default is assumed — an unset value fails validation.</summary>
    /// <remarks>Ignored when <see cref="Dialect"/> is set.</remarks>
    public SequenceEngine Engine { get; set; }

    /// <summary>
    /// A custom dialect, for an engine Themia does not ship. When set, <see cref="Engine"/> is not used.
    /// </summary>
    /// <remarks>
    /// This is what makes <see cref="ISequenceDialect"/> worth being public: an adopter on an unsupported
    /// engine supplies one here instead of forking the package. Null for the three built-in engines.
    /// </remarks>
    public ISequenceDialect? Dialect { get; set; }

    /// <summary>Throws when the options cannot be used.</summary>
    /// <exception cref="InvalidOperationException">A required value is missing or out of range.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                "SequenceOptions.ConnectionString is required. Set it to the database the "
                + "themia_sequences table lives in — normally the same connection string you pass to "
                + "ThemiaMigrations.Run.");
        }

        // A custom dialect replaces the engine entirely, so the enum is not consulted in that case.
        if (Dialect is null && !Enum.IsDefined(Engine))
        {
            throw new InvalidOperationException(
                $"SequenceOptions.Engine is not a supported engine ({(int)Engine}). Themia sequences "
                + "support PostgreSQL, MySQL 8.0.13+ and SQL Server. Set SequenceOptions.Dialect to run "
                + "against another engine.");
        }
    }
}
