namespace MyStack.Auth.Security;

/// <summary>
/// Role names are code-owned (docs/architecture.md §3.4) — fixed here, materialized into rows by
/// the seeder, never edited as data.
/// </summary>
internal static class AuthRoles
{
    public const string Admin = "admin";
}
