using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.Framework.Data.EFCore;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.EFCore;

/// <summary>
/// Verifies that a <c>DbContext</c> model actually carries the Themia Identity entities — that
/// <c>modelBuilder.ApplyThemiaIdentity()</c> was called, on the context Themia resolves.
/// </summary>
/// <remarks>
/// Without this the EF Core leg keeps exactly the failure mode the engine split removed from the Dapper
/// leg: registration succeeds, the module happily migrates the <c>identity</c> schema into existence, and
/// the mistake first surfaces as a query failure on the first user operation, with nothing pointing back
/// at the missing <c>OnModelCreating</c> line. <c>AddThemiaIdentityEFCore</c> registers this as a hosted
/// service so it runs at startup.
/// </remarks>
public static class IdentityModelValidation
{
    private const string Schema = "identity";
    private const string Table = "users";

    /// <summary>Throws when <paramref name="model"/> does not map the Identity entities to the <c>identity</c> schema.</summary>
    /// <param name="model">The built EF Core model to check.</param>
    /// <exception cref="InvalidOperationException">
    /// The model has no <see cref="User"/> entity, or maps it to a table other than <c>identity.users</c>.
    /// </exception>
    public static void Validate(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var entity = model.FindEntityType(typeof(User));
        if (entity is null)
        {
            throw new InvalidOperationException(
                "The registered ThemiaDbContext's model contains no Themia Identity entities. Call "
                + "modelBuilder.ApplyThemiaIdentity() from OnModelCreating (before base.OnModelCreating) "
                + "on the context Themia resolves — the one passed to AddThemiaDataRepositories<TContext>. "
                + "Without it every identity query runs against a table EF Core has never been told about.");
        }

        var schema = entity.GetSchema();
        var table = entity.GetTableName();
        if (!string.Equals(schema, Schema, StringComparison.Ordinal)
            || !string.Equals(table, Table, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The registered ThemiaDbContext maps Themia's User entity to '{schema ?? "(default schema)"}.{table}' "
                + $"instead of '{Schema}.{Table}'. The Identity schema migration creates {Schema}.{Table}, so the "
                + "context and the schema disagree and every identity query targets a table that does not exist. "
                + "Apply ApplyThemiaIdentity() rather than configuring the User entity yourself.");
        }
    }
}

/// <summary>Runs <see cref="IdentityModelValidation.Validate"/> against the registered context at startup.</summary>
internal sealed class IdentityEFCoreModelValidator(IServiceProvider serviceProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetService<ThemiaDbContext>()
            ?? throw new InvalidOperationException(
                "AddThemiaIdentityEFCore found no ThemiaDbContext registered. Register the EF Core data "
                + "peer — AddThemiaDbContext<TContext>(...) and AddThemiaDataRepositories<TContext>() — so "
                + "the identity stores have a context to run against.");

        IdentityModelValidation.Validate(context.Model);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
