namespace MyStack.Auth.Data;

// The role layer of architecture §3.1: fixed in code, never editable rows. Seeding materializes
// this list into `auth.roles`; `server/api`'s permission map will key off the same names — that
// duplication is deliberate (two short string lists beat a shared library).
public static class AuthRoles
{
    public const string GlobalAdmin = "globaladmin";
    public const string Admin = "admin";
    public const string User = "user";

    public static readonly IReadOnlyList<string> All = [GlobalAdmin, Admin, User];
}
