namespace Api.Rbac;

/// <summary>
/// Code-defined role catalog and default role→permission assignments. Role names land
/// verbatim in the access token's <c>role</c> claim; the API joins them against the
/// in-memory <see cref="RolePermissionCatalog"/> (loaded from the <c>role_permissions</c>
/// table) to derive the user's effective permission set on each request.
/// <para>
/// On startup, the seeder upserts each role in <see cref="Catalog"/> and then ensures
/// every <c>(role, permission)</c> pair in <see cref="DefaultPermissions"/> exists in
/// <c>role_permissions</c>. It does NOT delete rows that aren't listed here — that's the
/// seam that lets an admin add OR remove role↔permission assignments at runtime without
/// the next startup clobbering their edits.
/// </para>
/// </summary>
public static class Roles
{
    /// <summary>
    /// Has every defined permission. The only role that can mutate the auth structure
    /// itself (users, roles, permission assignments).
    /// </summary>
    public const string GlobalAdmin = "GlobalAdmin";

    /// <summary>
    /// Day-to-day admin — full CRUD on business resources, read-only on the auth structure.
    /// Cannot delete users or change role↔permission rows; that's <see cref="GlobalAdmin"/>.
    /// </summary>
    public const string Admin = "Admin";

    /// <summary>
    /// Default authenticated user. Can read posts and create new ones.
    /// </summary>
    public const string User = "User";

    /// <summary>
    /// Role definitions in seeding order, with descriptions stored on <c>AspNetRoles</c>.
    /// </summary>
    public static IReadOnlyList<(string Name, string Description)> Catalog { get; } =
    [
        (GlobalAdmin, "Full access including auth-structure changes."),
        (Admin, "Full access except auth-structure changes."),
        (User, "Read posts; create posts."),
    ];

    /// <summary>
    /// Default permission assignments. Authoritative on first seed. On subsequent seeds,
    /// MISSING rows (new permission constants added to <see cref="Permissions.Catalog"/>)
    /// are inserted, but EXTRA rows (admin-added assignments) are preserved.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultPermissions { get; } =
        new Dictionary<string, IReadOnlyList<string>>
        {
            // GlobalAdmin: every permission in the catalog. Computed so adding a new
            // permission constant automatically grants it to GlobalAdmin without an edit.
            [GlobalAdmin] = Permissions.Catalog.Select(p => p.Name).ToArray(),

            // Admin: everything business-side + read on auth structure. No mutating auth.
            [Admin] =
            [
                Permissions.Posts.Read,
                Permissions.Posts.Create,
                Permissions.Posts.Update,
                Permissions.Posts.Delete,
                Permissions.Users.Read,
                Permissions.Users.Update,
                Permissions.RolesAdmin.Read,
                Permissions.PermissionsAdmin.Read,
            ],

            // User: authenticated read + create. (Ownership scoping isn't wired yet —
            // that's a per-endpoint concern downstream of "can read at all".)
            [User] =
            [
                Permissions.Posts.Read,
                Permissions.Posts.Create,
            ],
        };
}
