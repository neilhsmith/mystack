namespace MyStack.Auth.Seeding;

// The config-declared half of architecture §3.4: which clients and accounts exist, where the
// clients redirect, their secrets — the values that genuinely differ per environment. What a
// client may *do* is fixed in code (AuthSeeder builds every descriptor); there is deliberately no
// knob here for grant types, so no configuration can reintroduce the password grant.
internal sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public IList<SeedClient> Clients { get; init; } = [];

    public IList<SeedUser> Users { get; init; } = [];
}

internal sealed class SeedClient
{
    public string ClientId { get; init; } = "";

    // Shown on the sign-in page's "continue to …"; defaults to the client id.
    public string? DisplayName { get; init; }

    // Public and Confidential are browser clients (authorization code + PKCE + refresh);
    // Machine is client credentials only, with no browser anywhere in its life; Device is the
    // device authorization grant — a browserless client that polls for tokens while the user
    // approves from a real browser somewhere else.
    public SeedClientType Type { get; init; }

    // Required for a confidential or machine client, forbidden for a public or device one —
    // every misconfiguration fails startup.
    public string? Secret { get; init; }

    // Browser clients only: when true the client is refused plain front-channel authorize URLs
    // and must push its parameters through /connect/par first (RFC 9126).
    public bool RequirePushedAuthorizationRequests { get; init; }

    // Required for a browser client, forbidden for a machine or device one.
    public IList<string> RedirectUris { get; init; } = [];

    public IList<string> PostLogoutRedirectUris { get; init; } = [];

    // Validated against the scopes the server registers; `openid` and `offline_access` are
    // granted implicitly and must not be listed.
    public IList<string> Scopes { get; init; } = [];
}

// Public is the zero value on purpose: a confidential or machine client that forgets to say so
// still fails loudly, because its Secret is then forbidden.
internal enum SeedClientType
{
    Public,
    Confidential,
    Machine,
    Device,
}

internal sealed class SeedUser
{
    public string Email { get; init; } = "";

    // Development convenience only. Absent — the production posture — the account is created
    // with no usable password and activated through the forgot-password flow (architecture §3.4).
    public string? Password { get; init; }

    // Validated against AuthRoles.All: roles are fixed in code, and a typo here would seed a row
    // that grants nothing.
    public IList<string> Roles { get; init; } = [];
}
