using System.Runtime.CompilerServices;

// Grants Themia.AI.Tests access to TranslationService (Internal) so its Fakes.cs can construct it
// directly (Task 7), without pulling Task 8's DI registration forward.
[assembly: InternalsVisibleTo("Themia.AI.Tests")]
