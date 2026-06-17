namespace Themia.Modules.Storage;

/// <summary>The result of a successful store operation.</summary>
/// <param name="Id">The metadata row id.</param>
/// <param name="Key">The logical key.</param>
/// <param name="SizeBytes">The stored size.</param>
/// <param name="ContentType">The stored content type.</param>
public sealed record StoredObject(Guid Id, string Key, long SizeBytes, string ContentType);
