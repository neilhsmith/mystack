using Microsoft.AspNetCore.Identity;

namespace Api.Identity;

/// <summary>
/// The role entity. Roles are the unit of authorization assignment: users get roles,
/// roles get permissions (via <see cref="Api.Rbac.RolePermission"/>). The role name lands
/// in the access token's <c>role</c> claim; the resource server (this same app) joins
/// roles against <see cref="Api.Rbac.RolePermissionCatalog"/> to derive the effective
/// permission set on each request.
/// </summary>
public sealed class ApplicationRole : IdentityRole<Guid>
{
    public string? Description { get; set; }
}
