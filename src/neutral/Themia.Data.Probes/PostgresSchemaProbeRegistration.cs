using System.Data;

namespace Themia.Data.Probes;

/// <summary>One package's probe registration: what to open, what to check, and whether it applies.</summary>
internal sealed class PostgresSchemaProbeRegistration(
    string componentName,
    Func<IServiceProvider, IDbConnection> connectionFactory,
    IReadOnlyList<string> tables,
    Func<IServiceProvider, bool>? appliesTo)
{
    public string ComponentName { get; } = componentName;

    public Func<IServiceProvider, IDbConnection> ConnectionFactory { get; } = connectionFactory;

    public IReadOnlyList<string> Tables { get; } = tables;

    public Func<IServiceProvider, bool>? AppliesTo { get; } = appliesTo;
}
