using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Themia.Modules.Storage.Entities;

namespace Themia.Modules.Storage.EntityConfiguration;

/// <summary>Applies the Themia Storage entity configurations to an EF model. Call inside your <c>ThemiaDbContext</c>-derived <c>OnModelCreating</c>, before <c>base.OnModelCreating</c>.</summary>
public static class StorageModelBuilderExtensions
{
    private const string Schema = "storage";

    /// <summary>Registers the Storage entities (storage objects) into the model.</summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <returns>The same model builder, for chaining.</returns>
    public static ModelBuilder ApplyThemiaStorage(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new StorageObjectConfiguration());
        return modelBuilder;
    }

    private sealed class StorageObjectConfiguration : IEntityTypeConfiguration<StorageObject>
    {
        public void Configure(EntityTypeBuilder<StorageObject> b)
        {
            b.ToTable("storage_objects", Schema);
            b.HasKey(o => o.Id);
            // Framework maps id/tenant_id/audit/soft-delete columns; map the storage-specific columns here.
            b.Property(o => o.Key).HasColumnName("key").HasMaxLength(1024).IsRequired();
            b.Property(o => o.ContentType).HasColumnName("content_type").HasMaxLength(256).IsRequired();
            b.Property(o => o.SizeBytes).HasColumnName("size_bytes");
            b.Property(o => o.ETag).HasColumnName("etag").HasMaxLength(256);
        }
    }
}
