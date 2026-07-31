using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MyStack.Auth.Data;
using MyStack.Auth.Oidc;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Seeding;

// Every item is ensured by its natural key — client id, scope name, role name, email — never by
// "is the table empty", so an item added to the list later still seeds (architecture §3.4). The
// write policy is declared per item: clients and scopes reconcile (config/code win on real drift,
// an unchanged item is never rewritten), users are create-only (never reset a password a human
// may have changed). Seeding never deletes.
internal sealed partial class AuthSeeder(
    RoleManager<ApplicationRole> roles,
    UserManager<ApplicationUser> users,
    IOpenIddictScopeManager scopes,
    IOpenIddictApplicationManager applications,
    IOptions<SeedOptions> options,
    ILogger<AuthSeeder> logger
)
{
    private static readonly (string Name, string DisplayName)[] ApiScopeList =
    [
        (ApiScopes.Read, "Read access to the API"),
        (ApiScopes.Write, "Write access to the API"),
    ];

    // The scopes a client may be granted — the same list OidcExtensions registers. `openid` and
    // `offline_access` are absent because OpenIddict grants them without a per-client permission.
    private static readonly string[] GrantableScopes =
    [
        Scopes.Email,
        Scopes.Profile,
        Scopes.Roles,
        ApiScopes.Read,
        ApiScopes.Write,
    ];

    public async Task SeedReferenceAsync(CancellationToken cancellationToken)
    {
        await EnsureRolesAsync();
        await ReconcileScopesAsync(cancellationToken);
        await ReconcileClientsAsync(cancellationToken);
        await EnsureAdminAsync();
    }

    public async Task SeedSampleAsync()
    {
        foreach (var sample in options.Value.Sample.Users)
        {
            await EnsureSampleUserAsync(sample);
        }
    }

    private async Task EnsureRolesAsync()
    {
        foreach (var role in AuthRoles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                ThrowIfFailed(await roles.CreateAsync(new ApplicationRole(role)), $"role '{role}'");
                LogRoleCreated(logger, role);
            }
        }
    }

    private async Task ReconcileScopesAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, displayName) in ApiScopeList)
        {
            var descriptor = new OpenIddictScopeDescriptor
            {
                Name = name,
                DisplayName = displayName,
                Resources = { ApiScopes.Resource },
            };

            var existing = await scopes.FindByNameAsync(name, cancellationToken);
            if (existing is null)
            {
                await scopes.CreateAsync(descriptor, cancellationToken);
                LogScopeCreated(logger, name);
            }
            else if (
                await scopes.GetDisplayNameAsync(existing, cancellationToken)
                    != descriptor.DisplayName
                || !SetEquals(
                    await scopes.GetResourcesAsync(existing, cancellationToken),
                    descriptor.Resources
                )
            )
            {
                await scopes.UpdateAsync(existing, descriptor, cancellationToken);
                LogScopeUpdated(logger, name);
            }
        }
    }

    private async Task ReconcileClientsAsync(CancellationToken cancellationToken)
    {
        foreach (var client in options.Value.Clients)
        {
            var descriptor = DescriptorFor(client);

            var existing = await applications.FindByClientIdAsync(
                client.ClientId,
                cancellationToken
            );
            if (existing is null)
            {
                await applications.CreateAsync(descriptor, cancellationToken);
                LogClientCreated(logger, client.ClientId);
            }
            else if (!await MatchesAsync(existing, descriptor, cancellationToken))
            {
                await applications.UpdateAsync(existing, descriptor, cancellationToken);
                LogClientUpdated(logger, client.ClientId);
            }
        }
    }

    private async Task EnsureAdminAsync()
    {
        var admin = options.Value.Admin;
        if (string.IsNullOrWhiteSpace(admin.Email))
        {
            // No fallback address: a default admin identity baked into the binary is a credential
            // shipped in a public repo (architecture §3.4).
            throw new InvalidOperationException(
                "Seed:Admin:Email is not configured. Reference seeding creates the bootstrap "
                    + "admin and needs its address — or set Database:Seed:Reference to false."
            );
        }

        if (await users.FindByEmailAsync(admin.Email) is not null)
        {
            return;
        }

        var user = new ApplicationUser
        {
            UserName = admin.Email,
            Email = admin.Email,
            EmailConfirmed = true,
        };

        // Without a configured password the account has no usable one: activation goes through
        // the ordinary forgot-password flow, so production never carries an admin password in
        // configuration. Recovery for a lost admin is pointing Seed:Admin:Email at a new address.
        ThrowIfFailed(
            admin.Password is { Length: > 0 } password
                ? await users.CreateAsync(user, password)
                : await users.CreateAsync(user),
            $"bootstrap admin '{admin.Email}'"
        );
        ThrowIfFailed(
            await users.AddToRoleAsync(user, AuthRoles.Admin),
            $"bootstrap admin '{admin.Email}'"
        );
        LogAdminCreated(logger, admin.Email);
    }

    private async Task EnsureSampleUserAsync(SampleUser sample)
    {
        if (string.IsNullOrWhiteSpace(sample.Email) || string.IsNullOrWhiteSpace(sample.Password))
        {
            throw new InvalidOperationException(
                "Every Seed:Sample:Users entry needs an Email and a Password."
            );
        }

        foreach (var role in sample.Roles)
        {
            if (!AuthRoles.All.Contains(role, StringComparer.Ordinal))
            {
                // Roles are fixed in code; a typo here would seed a row that grants nothing.
                throw new InvalidOperationException(
                    $"Sample user '{sample.Email}' declares unknown role '{role}'. "
                        + $"Roles are code-declared: {string.Join(", ", AuthRoles.All)}."
                );
            }
        }

        if (await users.FindByEmailAsync(sample.Email) is not null)
        {
            return;
        }

        var user = new ApplicationUser
        {
            UserName = sample.Email,
            Email = sample.Email,
            EmailConfirmed = true,
        };

        ThrowIfFailed(
            await users.CreateAsync(user, sample.Password),
            $"sample user '{sample.Email}'"
        );
        foreach (var role in sample.Roles)
        {
            ThrowIfFailed(await users.AddToRoleAsync(user, role), $"sample user '{sample.Email}'");
        }

        LogSampleUserCreated(logger, sample.Email);
    }

    // The only shape a seeded client can take: authorization code + PKCE + refresh, every
    // endpoint this server exposes, implicit consent (D17 — every v1 client is first-party).
    // Machine clients arrive with the client-credentials grant in auth-track step 10.
    private static OpenIddictApplicationDescriptor DescriptorFor(SeedClient client)
    {
        if (string.IsNullOrWhiteSpace(client.ClientId))
        {
            throw new InvalidOperationException("Every Seed:Clients entry needs a ClientId.");
        }

        if (client.RedirectUris.Count == 0)
        {
            throw new InvalidOperationException(
                $"Client '{client.ClientId}' has no RedirectUris; the authorization-code flow "
                    + "cannot complete without one."
            );
        }

        var confidential = client.Type == SeedClientType.Confidential;
        if (confidential && string.IsNullOrWhiteSpace(client.Secret))
        {
            throw new InvalidOperationException(
                $"Confidential client '{client.ClientId}' has no Secret. There is no fallback: "
                    + "a silently-defaulted secret would ship in a public repo (§3.4)."
            );
        }

        if (!confidential && !string.IsNullOrEmpty(client.Secret))
        {
            throw new InvalidOperationException(
                $"Public client '{client.ClientId}' declares a Secret, but a public client "
                    + "cannot keep one. Declare it Confidential, or remove the secret."
            );
        }

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = client.ClientId,
            DisplayName = client.DisplayName ?? client.ClientId,
            ClientType = confidential ? ClientTypes.Confidential : ClientTypes.Public,
            ClientSecret = confidential ? client.Secret : null,
            ConsentType = ConsentTypes.Implicit,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

        foreach (var uri in client.RedirectUris)
        {
            descriptor.RedirectUris.Add(ParseUri(client.ClientId, uri));
        }

        foreach (var uri in client.PostLogoutRedirectUris)
        {
            descriptor.PostLogoutRedirectUris.Add(ParseUri(client.ClientId, uri));
        }

        foreach (var scope in client.Scopes)
        {
            if (!GrantableScopes.Contains(scope, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Client '{client.ClientId}' requests unknown scope '{scope}'. Grantable "
                        + $"scopes: {string.Join(", ", GrantableScopes)} — `openid` and "
                        + "`offline_access` are granted implicitly and must not be listed."
                );
            }

            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        }

        return descriptor;
    }

    // The reconcile comparison. The secret goes through ValidateClientSecretAsync because it is
    // stored hashed — an unchanged client is never rewritten, so an out-of-band edit survives
    // reboots until config really does change.
    private async Task<bool> MatchesAsync(
        object existing,
        OpenIddictApplicationDescriptor descriptor,
        CancellationToken cancellationToken
    )
    {
        if (
            await applications.GetDisplayNameAsync(existing, cancellationToken)
                != descriptor.DisplayName
            || await applications.GetClientTypeAsync(existing, cancellationToken)
                != descriptor.ClientType
            || await applications.GetConsentTypeAsync(existing, cancellationToken)
                != descriptor.ConsentType
            || !SetEquals(
                await applications.GetRedirectUrisAsync(existing, cancellationToken),
                descriptor.RedirectUris.Select(uri => uri.ToString())
            )
            || !SetEquals(
                await applications.GetPostLogoutRedirectUrisAsync(existing, cancellationToken),
                descriptor.PostLogoutRedirectUris.Select(uri => uri.ToString())
            )
            || !SetEquals(
                await applications.GetPermissionsAsync(existing, cancellationToken),
                descriptor.Permissions
            )
            || !SetEquals(
                await applications.GetRequirementsAsync(existing, cancellationToken),
                descriptor.Requirements
            )
        )
        {
            return false;
        }

        return descriptor.ClientSecret is null
            || await applications.ValidateClientSecretAsync(
                existing,
                descriptor.ClientSecret,
                cancellationToken
            );
    }

    private static bool SetEquals(IEnumerable<string> stored, IEnumerable<string> wanted) =>
        stored.Order(StringComparer.Ordinal).SequenceEqual(wanted.Order(StringComparer.Ordinal));

    private static Uri ParseUri(string clientId, string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException(
                $"Client '{clientId}' declares '{value}', which is not an absolute URI."
            );

    private static void ThrowIfFailed(IdentityResult result, string what)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Seeding {what} failed: "
                    + string.Join("; ", result.Errors.Select(error => error.Description))
            );
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded role {Role}.")]
    private static partial void LogRoleCreated(ILogger logger, string role);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded scope {Scope}.")]
    private static partial void LogScopeCreated(ILogger logger, string scope);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciled scope {Scope}.")]
    private static partial void LogScopeUpdated(ILogger logger, string scope);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeded client {ClientId}.")]
    private static partial void LogClientCreated(ILogger logger, string clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciled client {ClientId}.")]
    private static partial void LogClientUpdated(ILogger logger, string clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created bootstrap admin {Email}.")]
    private static partial void LogAdminCreated(ILogger logger, string email);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created sample user {Email}.")]
    private static partial void LogSampleUserCreated(ILogger logger, string email);
}
