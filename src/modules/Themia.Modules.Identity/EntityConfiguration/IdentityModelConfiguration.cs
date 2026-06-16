using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.EntityConfiguration;

/// <summary>Applies the Themia Identity entity configurations to an EF model. Call inside your <c>ThemiaDbContext</c>-derived <c>OnModelCreating</c>, before <c>base.OnModelCreating</c>.</summary>
public static class ModelBuilderExtensions
{
    private const string Schema = "identity";

    /// <summary>Registers the Identity entities (users, roles, memberships, claims, tokens) into the model.</summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <returns>The same model builder, for chaining.</returns>
    public static ModelBuilder ApplyThemiaIdentity(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new RoleConfiguration());
        modelBuilder.ApplyConfiguration(new UserRoleConfiguration());
        modelBuilder.ApplyConfiguration(new UserClaimConfiguration());
        modelBuilder.ApplyConfiguration(new RoleClaimConfiguration());
        modelBuilder.ApplyConfiguration(new UserTokenConfiguration());
        modelBuilder.ApplyConfiguration(new RefreshTokenConfiguration());
        modelBuilder.ApplyConfiguration(new ExternalLoginLinkConfiguration());
        return modelBuilder;
    }

    private sealed class UserConfiguration : IEntityTypeConfiguration<User>
    {
        public void Configure(EntityTypeBuilder<User> b)
        {
            b.ToTable("users", Schema);
            b.HasKey(u => u.Id);
            // Framework maps id/tenant_id/audit/soft-delete columns; map the identity-specific columns here.
            b.Property(u => u.UserName).HasColumnName("user_name").HasMaxLength(256).IsRequired();
            b.Property(u => u.NormalizedUserName).HasColumnName("normalized_user_name").HasMaxLength(256).IsRequired();
            b.Property(u => u.Email).HasColumnName("email").HasMaxLength(256);
            b.Property(u => u.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(256);
            b.Property(u => u.EmailConfirmed).HasColumnName("email_confirmed");
            b.Property(u => u.PhoneNumber).HasColumnName("phone_number").HasMaxLength(64);
            b.Property(u => u.PhoneNumberConfirmed).HasColumnName("phone_number_confirmed");
            b.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(1024);
            b.Property(u => u.SecurityStamp).HasColumnName("security_stamp").HasMaxLength(128).IsRequired();
            b.Property(u => u.IsActive).HasColumnName("is_active");
            b.Property(u => u.AccessFailedCount).HasColumnName("access_failed_count");
            b.Property(u => u.LockoutEnd).HasColumnName("lockout_end");
            b.Property(u => u.LockoutEnabled).HasColumnName("lockout_enabled");
            b.Property(u => u.TwoFactorEnabled).HasColumnName("two_factor_enabled");
        }
    }

    private sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
    {
        public void Configure(EntityTypeBuilder<Role> b)
        {
            b.ToTable("roles", Schema);
            b.HasKey(r => r.Id);
            b.Property(r => r.Name).HasColumnName("name").HasMaxLength(256).IsRequired();
            b.Property(r => r.NormalizedName).HasColumnName("normalized_name").HasMaxLength(256).IsRequired();
            b.Property(r => r.Description).HasColumnName("description").HasMaxLength(512);
        }
    }

    private sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
    {
        public void Configure(EntityTypeBuilder<UserRole> b)
        {
            b.ToTable("user_roles", Schema);
            b.HasKey(ur => ur.Id);
            b.Property(ur => ur.Id).HasColumnName("id");
            b.Property(ur => ur.UserId).HasColumnName("user_id");
            b.Property(ur => ur.RoleId).HasColumnName("role_id");
        }
    }

    private sealed class UserClaimConfiguration : IEntityTypeConfiguration<UserClaim>
    {
        public void Configure(EntityTypeBuilder<UserClaim> b)
        {
            b.ToTable("user_claims", Schema);
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).HasColumnName("id");
            b.Property(c => c.UserId).HasColumnName("user_id");
            b.Property(c => c.ClaimType).HasColumnName("claim_type").HasMaxLength(256).IsRequired();
            b.Property(c => c.ClaimValue).HasColumnName("claim_value").HasMaxLength(1024).IsRequired();
        }
    }

    private sealed class RoleClaimConfiguration : IEntityTypeConfiguration<RoleClaim>
    {
        public void Configure(EntityTypeBuilder<RoleClaim> b)
        {
            b.ToTable("role_claims", Schema);
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).HasColumnName("id");
            b.Property(c => c.RoleId).HasColumnName("role_id");
            b.Property(c => c.ClaimType).HasColumnName("claim_type").HasMaxLength(256).IsRequired();
            b.Property(c => c.ClaimValue).HasColumnName("claim_value").HasMaxLength(1024).IsRequired();
        }
    }

    private sealed class UserTokenConfiguration : IEntityTypeConfiguration<UserToken>
    {
        public void Configure(EntityTypeBuilder<UserToken> b)
        {
            b.ToTable("user_tokens", Schema);
            b.HasKey(t => t.Id);
            b.Property(t => t.Id).HasColumnName("id");
            b.Property(t => t.UserId).HasColumnName("user_id");
            b.Property(t => t.Purpose).HasColumnName("purpose");                 // enum → int by convention
            b.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(256).IsRequired();
            b.Property(t => t.ExpiresAt).HasColumnName("expires_at");
            b.Property(t => t.ConsumedAt).HasColumnName("consumed_at");
        }
    }

    private sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
    {
        public void Configure(EntityTypeBuilder<RefreshToken> b)
        {
            b.ToTable("refresh_tokens", Schema);
            b.HasKey(t => t.Id);
            b.Property(t => t.Id).HasColumnName("id");
            b.Property(t => t.UserId).HasColumnName("user_id");
            b.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(256).IsRequired();
            b.Property(t => t.FamilyId).HasColumnName("family_id");
            b.Property(t => t.ExpiresAt).HasColumnName("expires_at");
            b.Property(t => t.ConsumedAt).HasColumnName("consumed_at");
            b.Property(t => t.RevokedAt).HasColumnName("revoked_at");
            b.Property(t => t.ReplacedById).HasColumnName("replaced_by_id");
            b.Property(t => t.ReplacedTokenId).HasColumnName("replaced_token_id");
            b.Property(t => t.CreatedAt).HasColumnName("created_at");
        }
    }

    private sealed class ExternalLoginLinkConfiguration : IEntityTypeConfiguration<ExternalLoginLink>
    {
        public void Configure(EntityTypeBuilder<ExternalLoginLink> b)
        {
            b.ToTable("external_logins", Schema);
            b.HasKey(l => l.Id);
            b.Property(l => l.Id).HasColumnName("id");
            // Framework maps tenant_id (tenant entity); map the link-specific columns here.
            b.Property(l => l.UserId).HasColumnName("user_id");
            b.Property(l => l.Provider).HasColumnName("provider").HasMaxLength(64).IsRequired();
            b.Property(l => l.ExternalId).HasColumnName("external_id").HasMaxLength(256).IsRequired();
            b.Property(l => l.CreatedAt).HasColumnName("created_at");
        }
    }
}
