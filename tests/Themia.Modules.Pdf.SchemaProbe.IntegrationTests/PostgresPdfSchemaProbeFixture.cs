using Testcontainers.PostgreSql;

using Xunit;

namespace Themia.Modules.Pdf.SchemaProbe.IntegrationTests;

/// <summary>
/// One PostgreSQL container shared by every test class in this assembly via xUnit's collection-fixture
/// mechanism (<see cref="PostgresPdfSchemaProbeCollection"/>), instead of each test class/method starting
/// its own. Unlike the Exceptional/DataProtection fixtures, this one runs no migration up front: the only
/// test that touches the database (<see cref="PdfSchemaProbeTests"/>'s
/// <c>Host_ShouldFailToStart_WhenPdfTemplatesIsOffTheSearchPath</c>) applies its own migration inline, and
/// the other two tests in that class never open a connection to this container at all.
/// </summary>
public sealed class PostgresPdfSchemaProbeFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    /// <summary>The live container's connection string, valid once <see cref="InitializeAsync"/> has
    /// completed.</summary>
    public string ConnectionString => container.GetConnectionString();

    /// <inheritdoc />
    public Task InitializeAsync() => container.StartAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

/// <summary>xUnit collection tying every test class in this assembly to one shared
/// <see cref="PostgresPdfSchemaProbeFixture"/> container instance.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresPdfSchemaProbeCollection : ICollectionFixture<PostgresPdfSchemaProbeFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "Postgres Pdf SchemaProbe";
}
