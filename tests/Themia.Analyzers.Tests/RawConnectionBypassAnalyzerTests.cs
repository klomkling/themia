using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Themia.Analyzers.Tests;

public class RawConnectionBypassAnalyzerTests
{
    // Minimal stubs mirroring the real IDapperConnectionContext + ITenantQueryFactory so the analyzer
    // binds the symbols by metadata name without referencing Themia.Framework.Data.Dapper.
    // IMPORTANT: C# requires `using` directives before namespace declarations. Test code below uses
    // fully-qualified type names so no `using` directive is needed after the stubs namespace block.
    // The concrete FakeConnectionContext is included so tests can use `new FakeConnectionContext()`
    // without `null!` or `default!` (both of which can produce CS8625 nullable warnings that would
    // cause EqualException crashes via the test harness when CompilerDiagnostics is not None).
    private const string DapperStubs = @"
namespace Themia.Framework.Data.Dapper.Connection {
    public interface IDapperConnectionContext {
        System.Threading.Tasks.Task<object> GetOpenConnectionAsync(System.Threading.CancellationToken ct);
        System.Threading.Tasks.Task<object> BeginTransactionAsync(System.Threading.CancellationToken ct);
    }
    public class FakeConnectionContext : IDapperConnectionContext {
        public System.Threading.Tasks.Task<object> GetOpenConnectionAsync(System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult<object>(new object());
        public System.Threading.Tasks.Task<object> BeginTransactionAsync(System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult<object>(new object());
    }
}
namespace Themia.Framework.Data.Dapper.Tenancy {
    public interface ITenantQueryFactory { object For<T>(); }
    public class FakeTenantQueryFactory : ITenantQueryFactory { public object For<T>() => new object(); }
}";

    [Fact]
    public async Task GetOpenConnectionAsync_Flagged()
    {
        // Fully qualified type names — no `using` after namespace block (CS1529 guard).
        var src = DapperStubs + @"
public class Service {
    private Themia.Framework.Data.Dapper.Connection.IDapperConnectionContext _ctx
        = new Themia.Framework.Data.Dapper.Connection.FakeConnectionContext();
    public System.Threading.Tasks.Task<object> Get()
        => {|#0:_ctx.GetOpenConnectionAsync(System.Threading.CancellationToken.None)|};
}";
        var expected = new DiagnosticResult("THEMIA103", DiagnosticSeverity.Warning).WithLocation(0);
        await new Verify<RawConnectionBypassAnalyzer>.Test { TestCode = src, ExpectedDiagnostics = { expected } }.RunAsync();
    }

    [Fact]
    public async Task BeginTransactionAsync_NotFlagged()
    {
        var src = DapperStubs + @"
public class Service {
    private Themia.Framework.Data.Dapper.Connection.IDapperConnectionContext _ctx
        = new Themia.Framework.Data.Dapper.Connection.FakeConnectionContext();
    public System.Threading.Tasks.Task<object> Get()
        => _ctx.BeginTransactionAsync(System.Threading.CancellationToken.None);
}";
        await new Verify<RawConnectionBypassAnalyzer>.Test { TestCode = src }.RunAsync();
    }

    [Fact]
    public async Task TenantQueryFactory_NotFlagged()
    {
        var src = DapperStubs + @"
public class Service {
    private Themia.Framework.Data.Dapper.Tenancy.ITenantQueryFactory _f
        = new Themia.Framework.Data.Dapper.Tenancy.FakeTenantQueryFactory();
    public object Get() => _f.For<string>();
}";
        await new Verify<RawConnectionBypassAnalyzer>.Test { TestCode = src }.RunAsync();
    }

    [Fact]
    public async Task UnrelatedGetOpenConnectionAsync_NotFlagged()
    {
        // A user type with its own GetOpenConnectionAsync is not IDapperConnectionContext — not flagged.
        var src = @"
public class FakeCtx {
    public System.Threading.Tasks.Task<object> GetOpenConnectionAsync(System.Threading.CancellationToken ct)
        => System.Threading.Tasks.Task.FromResult<object>(new object());
}
public class Service {
    private FakeCtx _ctx = new FakeCtx();
    public System.Threading.Tasks.Task<object> Get()
        => _ctx.GetOpenConnectionAsync(System.Threading.CancellationToken.None);
}";
        await new Verify<RawConnectionBypassAnalyzer>.Test { TestCode = src }.RunAsync();
    }

    [Fact]
    public async Task InDataLayerAssembly_NotFlagged()
    {
        var src = DapperStubs + @"
public class Service {
    private Themia.Framework.Data.Dapper.Connection.IDapperConnectionContext _ctx
        = new Themia.Framework.Data.Dapper.Connection.FakeConnectionContext();
    public System.Threading.Tasks.Task<object> Get()
        => _ctx.GetOpenConnectionAsync(System.Threading.CancellationToken.None);
}";
        var test = new Verify<RawConnectionBypassAnalyzer>.Test { TestCode = src };
        test.SolutionTransforms.Add((solution, projectId) =>
            solution.WithProjectAssemblyName(projectId, "Themia.Framework.Data.Dapper"));
        await test.RunAsync();
    }

    [Fact]
    public async Task Suppressed_NotFlagged()
    {
        var src = DapperStubs + @"
public class Service {
    private Themia.Framework.Data.Dapper.Connection.IDapperConnectionContext _ctx
        = new Themia.Framework.Data.Dapper.Connection.FakeConnectionContext();
#pragma warning disable THEMIA103
    public System.Threading.Tasks.Task<object> Get()
        => _ctx.GetOpenConnectionAsync(System.Threading.CancellationToken.None);
#pragma warning restore THEMIA103
}";
        await new Verify<RawConnectionBypassAnalyzer>.Test { TestCode = src }.RunAsync();
    }
}
