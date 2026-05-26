namespace Api.Rbac;

/// <summary>
/// Code-defined catalog of every permission the system knows about. Reference these
/// constants from endpoint authorization (<see cref="Authorization.AuthorizationEndpointConventions.RequirePermission"/>)
/// rather than typing the dotted strings inline — the compiler then catches typos, and
/// rename-refactor works.
/// <para>
/// The seeder upserts every value here into the <c>permissions</c> table at startup so
/// the DB-side catalog stays in lockstep with the code. Roles are then mapped to subsets
/// of these in <see cref="Roles.DefaultPermissions"/>; that mapping is also seeded into
/// <c>role_permissions</c>, but admins may freely edit role↔permission rows at runtime —
/// the seeder does NOT delete unrecognised assignments (see <c>RbacSeeder</c>).
/// </para>
/// <para>
/// Format is <c>{resource}.{verb}</c> — keep it that way so a permission name maps 1:1
/// to "a thing the API exposes". Adding a permission means (1) adding a constant here,
/// (2) adding a row to <see cref="Catalog"/>, (3) adding it to whichever role(s) in
/// <see cref="Roles.DefaultPermissions"/> should have it by default. The next startup
/// inserts the new rows.
/// </para>
/// </summary>
public static class Permissions
{
    public static class Posts
    {
        public const string Read = "posts.read";
        public const string Create = "posts.create";
        public const string Update = "posts.update";
        public const string Delete = "posts.delete";
    }

    public static class Users
    {
        public const string Read = "users.read";
        public const string Update = "users.update";
        public const string Delete = "users.delete";
    }

    public static class RolesAdmin
    {
        public const string Read = "roles.read";
        public const string Assign = "roles.assign";
    }

    public static class PermissionsAdmin
    {
        public const string Read = "permissions.read";
        public const string Assign = "permissions.assign";
    }

    /// <summary>
    /// Every defined permission with a one-line description. Order is the seeded ordering;
    /// each <c>(Name, Description)</c> entry becomes a row in the <c>permissions</c> table.
    /// </summary>
    public static IReadOnlyList<(string Name, string Description)> Catalog { get; } =
    [
        (Posts.Read, "View posts"),
        (Posts.Create, "Create posts"),
        (Posts.Update, "Update posts"),
        (Posts.Delete, "Delete posts"),
        (Users.Read, "View users"),
        (Users.Update, "Update users (profile, role assignments)"),
        (Users.Delete, "Delete users"),
        (RolesAdmin.Read, "View roles"),
        (RolesAdmin.Assign, "Assign roles to users"),
        (PermissionsAdmin.Read, "View permission catalog"),
        (PermissionsAdmin.Assign, "Assign permissions to roles"),
    ];
}
