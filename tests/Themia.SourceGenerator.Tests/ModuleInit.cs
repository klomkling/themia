using System.Runtime.CompilerServices;
using VerifyTests;

namespace Themia.SourceGenerator.Tests;

/// <summary>
/// Single Verify.SourceGenerators initialization for the whole test assembly.
/// Per-class static constructors caused "Already Initialized" failures under
/// parallel test execution; ModuleInitializer runs exactly once.
/// </summary>
public static class ModuleInit
{
    [ModuleInitializer]
    public static void Init() => VerifySourceGenerators.Initialize();
}
