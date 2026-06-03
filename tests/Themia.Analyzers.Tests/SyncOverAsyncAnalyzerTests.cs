using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Themia.Analyzers.Tests;

public class SyncOverAsyncAnalyzerTests
{
    [Fact]
    public async Task FromResultOfCall_Flagged()
    {
        var src = @"
using System.Threading.Tasks;
public class S {
    private int Compute() => 1;
    public Task<int> {|#0:GetAsync|}() => Task.FromResult(Compute());
}";
        var expected = new DiagnosticResult("THEMIA102", DiagnosticSeverity.Warning).WithLocation(0).WithArguments("GetAsync");
        await new Verify<SyncOverAsyncAnalyzer>.Test { TestCode = src, ExpectedDiagnostics = { expected } }.RunAsync();
    }

    [Fact]
    public async Task BlockBodySingleReturn_Flagged()
    {
        var src = @"
using System.Threading.Tasks;
public class S {
    private int Compute() => 1;
    public Task<int> {|#0:GetAsync|}() { return Task.FromResult(Compute()); }
}";
        var expected = new DiagnosticResult("THEMIA102", DiagnosticSeverity.Warning).WithLocation(0).WithArguments("GetAsync");
        await new Verify<SyncOverAsyncAnalyzer>.Test { TestCode = src, ExpectedDiagnostics = { expected } }.RunAsync();
    }

    [Fact]
    public async Task MultiStatementBody_NotFlagged()
    {
        // The single-return guard is intentional: a method doing other work before
        // returning Task.FromResult is out of scope.
        var src = @"
using System.Threading.Tasks;
public class S {
    private int Compute() => 1;
    public Task<int> GetAsync() { var x = Compute(); return Task.FromResult(x); }
}";
        await new Verify<SyncOverAsyncAnalyzer>.Test { TestCode = src }.RunAsync();
    }

    [Fact]
    public async Task FromResultOfValue_NotFlagged()
    {
        var src = @"
using System.Threading.Tasks;
public class S {
    public Task<int> GetAsync(int x) => Task.FromResult(x);
}";
        await new Verify<SyncOverAsyncAnalyzer>.Test { TestCode = src }.RunAsync();
    }

    [Fact]
    public async Task GenuinelyAsync_NotFlagged()
    {
        var src = @"
using System.Threading.Tasks;
public class S {
    public async Task<int> GetAsync() { await Task.Delay(1); return 1; }
}";
        await new Verify<SyncOverAsyncAnalyzer>.Test { TestCode = src }.RunAsync();
    }

    [Fact]
    public async Task UserTypeNamedTask_NotFlagged()
    {
        // A user-defined type named Task (not System.Threading.Tasks.Task) must not be matched.
        var src = @"
namespace Custom {
    public class Task<T> { public static Task<T> FromResult(T v) => new Task<T>(); }
    public class S {
        private int Compute() => 1;
        public Task<int> GetAsync() => Task<int>.FromResult(Compute());
    }
}";
        await new Verify<SyncOverAsyncAnalyzer>.Test { TestCode = src }.RunAsync();
    }
}
