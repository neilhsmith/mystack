namespace MyStack.Auth.Oidc;

// The claim shape architecture §3.1 fixes: per-user overrides ride the access token as extra
// grants (`perm`) and revocations (`perm_deny`). The override store lands in auth-track step 9;
// the names live here now so the destination mapping never has to reopen token generation.
internal static class AuthClaims
{
    public const string Permission = "perm";
    public const string PermissionDeny = "perm_deny";
}
