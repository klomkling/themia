using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing.Verifiers;

namespace Themia.Analyzers.Tests;

// Thin alias so each test reads Verify<TAnalyzer>.Test.
public static class Verify<TAnalyzer> where TAnalyzer : DiagnosticAnalyzer, new()
{
    public sealed class Test : CSharpAnalyzerTest<TAnalyzer, XUnitVerifier> { }
}
