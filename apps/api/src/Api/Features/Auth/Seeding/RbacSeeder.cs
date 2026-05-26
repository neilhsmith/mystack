using Api.Data;
using Api.Identity;
using Api.Rbac;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Api.Features.Auth.Seeding;

/// <summary>
/// Brings the database's RBAC catalog (<c>permissions</c>, <c>aspnet_roles</c>,
/// <c>role_permissions</c>) in line with the code-defined <see cref="Permissions.Catalog"/>
/// and <see cref="Roles.Catalog"/> on every startup. Behaviour:
/// <list type="bullet">
///   <item>Permissions: new constants get inserted; existing names get their description
///   refreshed (descriptions are display copy, not data, so it's fine to overwrite).</item>
///   <item>Roles: new entries are inserted via <see cref="RoleManager{TRole}"/>. Existing
///   entries are left alone — admins might have edited the description.</item>
///   <item>Role permissions: pairs in <see cref="Roles.DefaultPermissions"/> are inserted
///   if missing. Pairs NOT in the defaults are NEVER deleted — that's the seam letting
///   admins add/remove assignments at runtime without losing them on next deploy.</item>
/// </list>
/// </summary>
public sealed class RbacSeeder
{
    private readonly AppDbContext _db;
    private readonly RoleManager<ApplicationRole> _roles;
    private readonly ILogger<RbacSeeder> _logger;

    public RbacSeeder(
        AppDbContext db,
        RoleManager<ApplicationRole> roles,
        ILogger<RbacSeeder> logger)
    {
        _db = db;
        _roles = roles;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct)
    {
        await SeedPermissionsAsync(ct);
        await SeedRolesAsync();
        await SeedRolePermissionsAsync(ct);
    }

    private async Task SeedPermissionsAsync(CancellationToken ct)
    {
        var existing = await _db.Permissions.ToDictionaryAsync(p => p.Name, ct);

        foreach (var (name, description) in Permissions.Catalog)
        {
            if (existing.TryGetValue(name, out var current))
            {
                if (!string.Equals(current.Description, description, StringComparison.Ordinal))
                {
                    current.Description = description;
                }
            }
            else
            {
                _db.Permissions.Add(new Permission
                {
                    Name = name,
                    Description = description,
                });
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task SeedRolesAsync()
    {
        foreach (var (name, description) in Roles.Catalog)
        {
            var role = await _roles.FindByNameAsync(name);
            if (role is null)
            {
                _logger.LogInformation("Creating role {Role}.", name);
                var result = await _roles.CreateAsync(new ApplicationRole
                {
                    Id = Guid.CreateVersion7(),
                    Name = name,
                    Description = description,
                });
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Failed to create role {name}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
                }
            }
        }
    }

    private async Task SeedRolePermissionsAsync(CancellationToken ct)
    {
        // Map role names → ids once so the upsert loop doesn't re-query.
        var rolesByName = await _db.Set<ApplicationRole>()
            .Where(r => r.Name != null)
            .ToDictionaryAsync(r => r.Name!, r => r.Id, ct);

        var existingAssignments = await _db.RolePermissions
            .Select(rp => new { rp.RoleId, rp.PermissionName })
            .ToListAsync(ct);
        var existingSet = existingAssignments
            .Select(a => (a.RoleId, a.PermissionName))
            .ToHashSet();

        foreach (var (roleName, permissions) in Roles.DefaultPermissions)
        {
            if (!rolesByName.TryGetValue(roleName, out var roleId))
            {
                _logger.LogWarning(
                    "Role {Role} not found in DB; default permissions will not be seeded for it.",
                    roleName);
                continue;
            }

            foreach (var permission in permissions)
            {
                if (!existingSet.Contains((roleId, permission)))
                {
                    _db.RolePermissions.Add(new RolePermission
                    {
                        RoleId = roleId,
                        PermissionName = permission,
                    });
                }
            }
        }

        await _db.SaveChangesAsync(ct);
    }
}
