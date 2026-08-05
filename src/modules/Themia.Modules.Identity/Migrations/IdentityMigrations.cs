using System.Reflection;

namespace Themia.Modules.Identity.Migrations;

/// <summary>The assembly carrying the Themia Identity FluentMigrator migrations.</summary>
/// <remarks>
/// The core package defines the <c>identity</c> schema but ships no runner — running migrations needs
/// <c>Themia.Data.Migrations</c>, which carries a database driver for every supported engine, and the core
/// stays driver-free (coord #0058). Both engine modules run these on startup. An adopter who references
/// only the core, supplying their own <c>IRepository</c> implementations, owns applying the schema and can
/// hand <see cref="Assembly"/> to <c>ThemiaMigrations.Run</c> or to a runner of their own.
/// </remarks>
public static class IdentityMigrations
{
    /// <summary>The assembly to scan for the Identity migrations.</summary>
    public static Assembly Assembly { get; } = typeof(IdentitySchemaMigration).Assembly;
}
