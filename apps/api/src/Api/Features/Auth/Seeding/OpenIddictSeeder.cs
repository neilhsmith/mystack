using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Api.Features.Auth.Seeding;

/// <summary>
/// Idempotently registers OpenIddict scopes (the two custom <see cref="Scopes"/> on top
/// of the standard OIDC ones) and applications (the <c>mystack-web</c> public client for
/// authorization code + PKCE, and the <c>mystack-service</c> confidential client for
/// client_credentials).
/// <para>
/// On subsequent runs: existing scopes/applications are NOT updated — admin edits stick.
/// Adding a new client to the seeder will create it on next startup; removing one from
/// the seeder leaves the existing DB row alone (so a misconfigured client doesn't
/// disappear silently).
/// </para>
/// </summary>
public sealed class OpenIddictSeeder
{
    private readonly IOpenIddictScopeManager _scopes;
    private readonly IOpenIddictApplicationManager _applications;
    private readonly AuthSeedOptions _options;
    private readonly ILogger<OpenIddictSeeder> _logger;

    public OpenIddictSeeder(
        IOpenIddictScopeManager scopes,
        IOpenIddictApplicationManager applications,
        IOptions<AuthSeedOptions> options,
        ILogger<OpenIddictSeeder> logger)
    {
        _scopes = scopes;
        _applications = applications;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct)
    {
        await SeedScopesAsync(ct);
        await SeedWebClientAsync(ct);
        await SeedServiceClientAsync(ct);
    }

    private async Task SeedScopesAsync(CancellationToken ct)
    {
        foreach (var (name, description) in Auth.Scopes.Catalog)
        {
            if (await _scopes.FindByNameAsync(name, ct) is null)
            {
                _logger.LogInformation("Registering OpenIddict scope {Scope}.", name);
                var descriptor = new OpenIddictScopeDescriptor
                {
                    Name = name,
                    DisplayName = description,
                };
                // OpenIddict's local-server validation enforces audience matching: a token
                // is only accepted on an endpoint registered against one of its `aud`
                // claims. Registering the API audience here ensures issued tokens carry
                // `aud = mystack-api`, which `AddAudiences` on the validation side accepts.
                descriptor.Resources.Add(AuthAudiences.ApiAudience);
                await _scopes.CreateAsync(descriptor, ct);
            }
        }
    }

    private async Task SeedWebClientAsync(CancellationToken ct)
    {
        var clientId = _options.Clients.WebClientId;

        if (await _applications.FindByClientIdAsync(clientId, ct) is not null)
        {
            return;
        }

        _logger.LogInformation("Registering OpenIddict client {ClientId} (public, auth code + PKCE).", clientId);

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            // No ClientSecret: public client (browser/SPA). PKCE is required by
            // OpenIddict.Server config (RequireProofKeyForCodeExchange).
            ClientType = ClientTypes.Public,
            DisplayName = "mystack Web",
            ConsentType = ConsentTypes.Implicit, // first-party client; skip explicit consent UI
            RedirectUris =
            {
                new Uri(_options.Clients.WebRedirectUri),
            },
            PostLogoutRedirectUris =
            {
                new Uri(_options.Clients.WebPostLogoutRedirectUri),
            },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.EndSession,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Email,
                Permissions.Scopes.Profile,
                Permissions.Scopes.Roles,
                Permissions.Prefixes.Scope + Auth.Scopes.Read,
                Permissions.Prefixes.Scope + Auth.Scopes.Write,
            },
            Requirements =
            {
                Requirements.Features.ProofKeyForCodeExchange,
            },
        };

        await _applications.CreateAsync(descriptor, ct);
    }

    private async Task SeedServiceClientAsync(CancellationToken ct)
    {
        var clientId = _options.Clients.ServiceClientId;
        var secret = _options.Clients.ServiceClientSecret;

        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogInformation(
                "Skipping {ClientId} — no client secret configured. " +
                "Set Seed:Clients:ServiceClientSecret to register the client_credentials demo client.",
                clientId);
            return;
        }

        if (await _applications.FindByClientIdAsync(clientId, ct) is not null)
        {
            return;
        }

        _logger.LogInformation("Registering OpenIddict client {ClientId} (confidential, client_credentials).", clientId);

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = secret,
            ClientType = ClientTypes.Confidential,
            DisplayName = "mystack Service",
            ConsentType = ConsentTypes.Implicit,
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
                Permissions.Prefixes.Scope + Auth.Scopes.Read,
                Permissions.Prefixes.Scope + Auth.Scopes.Write,
            },
        };

        // Service tokens carry a `role` claim derived from this list — exposed via the
        // OpenIddict application's Properties bag (see ClientRoles helper). GlobalAdmin
        // by default because a confidential service client is highly trusted; a real
        // deployment can swap to Admin or a narrower service-specific role here.
        ClientRoles.Set(descriptor, Rbac.Roles.GlobalAdmin);

        await _applications.CreateAsync(descriptor, ct);
    }
}
