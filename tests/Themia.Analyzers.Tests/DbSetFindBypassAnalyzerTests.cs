using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Themia.Analyzers.Tests;

public class DbSetFindBypassAnalyzerTests
{
    // Minimal stubs so the analyzer can bind Microsoft.EntityFrameworkCore.DbSet<T> and a guarded
    // DbContext.FindAsync<T> without referencing the EF packages.
    // Return Task<T> (not ValueTask<T>) so the inline compilation resolves against the netstandard2.0
    // reference assemblies the test harness uses by default — ValueTask<T> is not in netstandard2.0.
    private const string EfStubs = @"
namespace Microsoft.EntityFrameworkCore {
    public class DbSet<T> {
        public T Find(params object[] keyValues) => default!;
        public System.Threading.Tasks.Task<T> FindAsync(params object[] keyValues) => default!;
    }
    public class DbContext {
        public DbSet<T> Set<T>() => new DbSet<T>();
        public System.Threading.Tasks.Task<T> FindAsync<T>(params object[] keyValues) => default!;
    }
}";

    [Fact]
    public async Task DbSetFind_Flagged()
    {
        var src = EfStubs + @"
public class Repo {
    private Microsoft.EntityFrameworkCore.DbSet<string> _set = new();
    public string Get() => {|#0:_set.Find(""id"")|};
}";
        var expected = new DiagnosticResult("THEMIA104", DiagnosticSeverity.Warning).WithLocation(0);
        await new Verify<DbSetFindBypassAnalyzer>.Test { TestCode = src, ExpectedDiagnostics = { expected } }.RunAsync();
    }

    [Fact]
    public async Task DbSetFindAsync_Flagged()
    {
        var src = EfStubs + @"
using System.Threading.Tasks;
public class Repo {
    private Microsoft.EntityFrameworkCore.DbSet<string> _set = new();
    public Task<string> Get() => {|#0:_set.FindAsync(""id"")|};
}";
        var expected = new DiagnosticResult("THEMIA104", DiagnosticSeverity.Warning).WithLocation(0);
        await new Verify<DbSetFindBypassAnalyzer>.Test
        {
            TestCode = src,
            ExpectedDiagnostics = { expected },
            CompilerDiagnostics = CompilerDiagnostics.None,
        }.RunAsync();
    }

    [Fact]
    public async Task SetGenericFind_Flagged()
    {
        var src = EfStubs + @"
public class Repo {
    private Microsoft.EntityFrameworkCore.DbContext _ctx = new();
    public string Get() => {|#0:_ctx.Set<string>().Find(""id"")|};
}";
        var expected = new DiagnosticResult("THEMIA104", DiagnosticSeverity.Warning).WithLocation(0);
        await new Verify<DbSetFindBypassAnalyzer>.Test { TestCode = src, ExpectedDiagnostics = { expected } }.RunAsync();
    }

    [Fact]
    public async Task NonOverridingSubclassFind_Flagged()
    {
        // A subclass that INHERITS Find without overriding it: the invocation's TargetMethod.ContainingType
        // is still DbSet<T> (the declaring type), so .OriginalDefinition matches and THEMIA104 fires. This
        // pins the documented behavior and the reason the match uses OriginalDefinition.
        var src = EfStubs + @"
public class TenantSet<T> : Microsoft.EntityFrameworkCore.DbSet<T> { }
public class Repo {
    private TenantSet<string> _set = new();
    public string Get() => {|#0:_set.Find(""id"")|};
}";
        var expected = new DiagnosticResult("THEMIA104", DiagnosticSeverity.Warning).WithLocation(0);
        await new Verify<DbSetFindBypassAnalyzer>.Test { TestCode = src, ExpectedDiagnostics = { expected } }.RunAsync();
    }

    [Fact]
    public async Task GuardedContextFindAsync_NotFlagged()
    {
        // DbContext.FindAsync<T> is the guarded path (member of DbContext, not DbSet<T>).
        var src = EfStubs + @"
using System.Threading.Tasks;
public class Repo {
    private Microsoft.EntityFrameworkCore.DbContext _ctx = new();
    public Task<string> Get() => _ctx.FindAsync<string>(""id"");
}";
        var test = new Verify<DbSetFindBypassAnalyzer>.Test
        {
            TestCode = src,
            CompilerDiagnostics = CompilerDiagnostics.None,
        };
        await test.RunAsync();
    }

    [Fact]
    public async Task UnrelatedFind_NotFlagged()
    {
        // A user type with its own Find is not DbSet<T> — not flagged.
        var src = @"
public class Cache { public string Find(string k) => k; }
public class Repo {
    private Cache _c = new();
    public string Get() => _c.Find(""id"");
}";
        await new Verify<DbSetFindBypassAnalyzer>.Test { TestCode = src }.RunAsync();
    }

    [Fact]
    public async Task InDataLayerAssembly_NotFlagged()
    {
        var src = EfStubs + @"
public class Repo {
    private Microsoft.EntityFrameworkCore.DbSet<string> _set = new();
    public string Get() => _set.Find(""id"");
}";
        var test = new Verify<DbSetFindBypassAnalyzer>.Test { TestCode = src };
        test.SolutionTransforms.Add((solution, projectId) =>
            solution.WithProjectAssemblyName(projectId, "Themia.Framework.Data.EFCore"));
        await test.RunAsync();
    }

    [Fact]
    public async Task Suppressed_NotFlagged()
    {
        var src = EfStubs + @"
public class Repo {
    private Microsoft.EntityFrameworkCore.DbSet<string> _set = new();
#pragma warning disable THEMIA104
    public string Get() => _set.Find(""id"");
#pragma warning restore THEMIA104
}";
        await new Verify<DbSetFindBypassAnalyzer>.Test { TestCode = src }.RunAsync();
    }
}
