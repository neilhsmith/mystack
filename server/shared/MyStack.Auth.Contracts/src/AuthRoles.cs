namespace MyStack.Auth.Contracts;

// The role layer of architecture §3.1: fixed in code, never editable rows, one spelling for the
// whole system. Auth seeds this list into `auth.roles` and mints memberships as `role` claims;
// every resource server keys its role→permission map off the same names.
public static class AuthRoles
{
    public const string GlobalAdmin = "globaladmin";
    public const string Admin = "admin";
    public const string User = "user";

    public static readonly IReadOnlyList<string> All = [GlobalAdmin, Admin, User];
}
