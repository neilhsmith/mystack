namespace Api.Rbac;

/// <summary>
/// A single named action that endpoint authorization checks for. Permission names are
/// short, dotted strings — <c>posts.read</c>, <c>users.delete</c>. The complete list of
/// permissions the system knows about is code-defined in <see cref="Permissions"/>; this
/// entity is the DB-side projection so roles can be associated with permissions (via
/// <see cref="RolePermission"/>) and edited at runtime by an admin without a code change.
/// </summary>
public sealed class Permission
{
    /// <summary>
    /// The permission identifier (e.g. <c>posts.read</c>). Primary key — the dotted
    /// string is the stable contract, so we use it directly rather than a surrogate id.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Human description shown in admin UIs. Maintained by the seeder from the code-defined
    /// catalog; admin edits at runtime would overwrite, but that's fine — it's only ever a
    /// label.
    /// </summary>
    public string? Description { get; set; }

    public ICollection<RolePermission> RolePermissions { get; init; } = [];
}
