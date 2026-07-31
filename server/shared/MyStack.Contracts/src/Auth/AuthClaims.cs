namespace MyStack.Contracts.Auth;

// The claim shape architecture §3.1 fixes: per-user overrides ride the access token as extra
// grants (`perm`) and revocations (`perm_deny`). Auth mints them; every resource server reads
// them for its `effective = expand(roles) ∪ granted − denied` arithmetic.
public static class AuthClaims
{
    public const string Permission = "perm";
    public const string PermissionDeny = "perm_deny";
}
