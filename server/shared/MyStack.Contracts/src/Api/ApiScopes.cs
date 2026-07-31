namespace MyStack.Contracts.Api;

// The scope layer of architecture §3.1: what a client *application* may do against the api
// resource. Spelled by both sides of the wire — auth registers, seeds and mints these (an api.*
// scope stamps the `api` audience); `server/api`'s endpoint policies and JWT validation enforce
// them. A future resource server adds its own directory here (Billing/…), never entries in this
// one.
public static class ApiScopes
{
    public const string Read = "api.read";
    public const string Write = "api.write";

    // The audience an api.* token is minted for — the value `server/api` will accept as `aud`.
    public const string Resource = "api";
}
