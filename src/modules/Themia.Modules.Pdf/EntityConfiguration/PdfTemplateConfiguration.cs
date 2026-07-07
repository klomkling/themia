using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Themia.Modules.Pdf.EntityConfiguration;

internal sealed class PdfTemplateConfiguration : IEntityTypeConfiguration<PdfTemplate>
{
    public void Configure(EntityTypeBuilder<PdfTemplate> b)
    {
        b.ToTable("pdf_templates");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).HasMaxLength(100);
        // Adopter column names map to snake_case explicitly so the EF and Dapper peers agree.
        b.Property(x => x.Key).IsRequired().HasMaxLength(200).HasColumnName("key");
        b.Property(x => x.Body).IsRequired().HasColumnName("body");
        b.Property(x => x.Name).HasMaxLength(400).HasColumnName("name");
        b.Property(x => x.Description).HasColumnName("description");
    }
}
