// Polyfill: netstandard2.0 lacks System.Runtime.CompilerServices.IsExternalInit, which the
// compiler requires to emit `init`-only setters (used by records). Defining it internally lets
// this netstandard2.0 source-generator project use records for the equatable pipeline model.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
