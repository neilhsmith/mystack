using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Rbac;

internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> entity)
    {
        entity.ToTable("permissions");

        entity.HasKey(p => p.Name);

        entity.Property(p => p.Name)
            .HasMaxLength(100);

        entity.Property(p => p.Description)
            .HasMaxLength(256);

        entity.HasMany(p => p.RolePermissions)
            .WithOne(rp => rp.Permission!)
            .HasForeignKey(rp => rp.PermissionName)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
