namespace MyStack.Auth.Oidc;

// The scope layer of architecture §3.1: what a client *application* may do, enforced by
// `server/api`'s endpoint policies. Auth only issues them.
internal static class ApiScopes
{
    public const string Read = "api.read";
    public const string Write = "api.write";

    // The audience an api.* token is minted for — the value `server/api` will accept as `aud`.
    public const string Resource = "api";
}
