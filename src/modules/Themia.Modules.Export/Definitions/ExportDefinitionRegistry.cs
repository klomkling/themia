namespace Themia.Modules.Export.Definitions;

/// <summary>Resolves a registered <see cref="IExportDefinition"/> by key.</summary>
public interface IExportDefinitionRegistry
{
    /// <summary>Returns the definition for <paramref name="key"/>, or <see langword="null"/> if none is registered.</summary>
    IExportDefinition? Find(string key);
}

internal sealed class ExportDefinitionRegistry(IEnumerable<IExportDefinition> definitions) : IExportDefinitionRegistry
{
    private readonly Dictionary<string, IExportDefinition> map =
        definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public IExportDefinition? Find(string key) => map.GetValueOrDefault(key);
}
