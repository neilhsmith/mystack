using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Api.Rbac;

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> entity)
    {
        entity.ToTable("role_permissions");

        entity.HasKey(rp => new { rp.RoleId, rp.PermissionName });

        entity.Property(rp => rp.PermissionName)
            .HasMaxLength(100);

        entity.HasOne(rp => rp.Role)
            .WithMany()
            .HasForeignKey(rp => rp.RoleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Permission side configured by PermissionConfiguration so the navigation lives
        // on Permission too (RolePermissions collection).
    }
}
