using Api.Identity;

namespace Api.Rbac;

/// <summary>
/// Join entity: role ↔ permission. The pair is the primary key — a role either has a
/// permission or it doesn't, no extra metadata. Editing a role's permissions at runtime
/// means inserting / deleting rows here. The catalog refresher
/// (<see cref="RolePermissionCatalogRefresher"/>) re-reads this table on a timer so API
/// permission checks pick up admin edits without a restart.
/// </summary>
public sealed class RolePermission
{
    public required Guid RoleId { get; init; }

    public required string PermissionName { get; init; }

    public ApplicationRole? Role { get; init; }

    public Permission? Permission { get; init; }
}
