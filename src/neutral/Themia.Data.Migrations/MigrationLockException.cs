namespace Themia.Data.Migrations;

/// <summary>
/// A failure in the migration lock itself — the lock connection, the acquire, or the release — as opposed to
/// a failure applying the migrations. Kept distinct so <see cref="ThemiaMigrations"/> does not report
/// "verify DDL permissions" for a problem that never reached any DDL.
/// </summary>
internal sealed class MigrationLockException(string message, Exception? innerException = null)
    : Exception(message, innerException);
