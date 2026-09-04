using Themia.Framework.Data.Sequences;
using Xunit;

namespace Themia.Framework.Data.Sequences.Tests;

public sealed class SequenceOptionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsBlankConnectionString(string connectionString)
    {
        var options = new SequenceOptions { ConnectionString = connectionString, Engine = SequenceEngine.Postgres };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("ConnectionString", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsUndefinedEngine()
    {
        var options = new SequenceOptions { ConnectionString = "Host=x", Engine = (SequenceEngine)99 };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Engine", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsAConfiguredPair() =>
        new SequenceOptions { ConnectionString = "Host=x", Engine = SequenceEngine.Postgres }.Validate();

    [Fact]
    public void Validate_AcceptsACustomDialectWithNoKnownEngine()
    {
        // The reason ISequenceDialect is public: an adopter on an engine Themia does not ship supplies
        // one rather than forking. Without this the public interface has no way in and is decoration.
        var options = new SequenceOptions
        {
            ConnectionString = "whatever",
            Engine = (SequenceEngine)99,
            Dialect = new FakeDialect(),
        };

        options.Validate();
    }

    private sealed class FakeDialect : ISequenceDialect
    {
        public System.Data.Common.DbConnection CreateConnection(string connectionString) =>
            throw new NotSupportedException("not opened in this test");

        public string SelectForUpdateSql => "SELECT next_value ... @tenant ... @key";
        public string UpdateNextValueSql => "UPDATE ... @tenant ... @key ... @val";
        public string InsertIfMissingSql => "INSERT ... @tenant ... @key ... @val";
    }
}
