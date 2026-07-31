namespace MyStack.Auth.Seeding;

// The config-declared half of architecture §3.4: which clients exist, where they redirect, their
// secrets, and the accounts — the values that genuinely differ per environment. What a client may
// *do* is fixed in code (AuthSeeder builds every descriptor); there is deliberately no knob here
// for grant types, so no configuration can reintroduce the password grant.
internal sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public IList<SeedClient> Clients { get; init; } = [];

    public SeedAdmin Admin { get; init; } = new();

    public SeedSample Sample { get; init; } = new();
}

internal sealed class SeedClient
{
    public string ClientId { get; init; } = "";

    // Shown on the sign-in page's "continue to …"; defaults to the client id.
    public string? DisplayName { get; init; }

    public SeedClientType Type { get; init; }

    // Required for a confidential client, forbidden for a public one — both misconfigurations
    // fail startup.
    public string? Secret { get; init; }

    public IList<string> RedirectUris { get; init; } = [];

    public IList<string> PostLogoutRedirectUris { get; init; } = [];

    // Validated against the scopes the server registers; `openid` and `offline_access` are
    // granted implicitly and must not be listed.
    public IList<string> Scopes { get; init; } = [];
}

// Public is the zero value on purpose: a confidential client that forgets to say so still fails
// loudly, because its Secret is then forbidden.
internal enum SeedClientType
{
    Public,
    Confidential,
}

internal sealed class SeedAdmin
{
    public string? Email { get; init; }

    // Development convenience only. Absent — the production posture — the admin is created with
    // no usable password and activated through the forgot-password flow (architecture §3.4).
    public string? Password { get; init; }
}

internal sealed class SeedSample
{
    public IList<SampleUser> Users { get; init; } = [];
}

internal sealed class SampleUser
{
    public string Email { get; init; } = "";

    public string Password { get; init; } = "";

    public IList<string> Roles { get; init; } = [];
}
