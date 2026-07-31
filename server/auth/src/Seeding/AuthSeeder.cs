using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MyStack.Auth.Data;
using MyStack.Contracts.Api;
using MyStack.Contracts.Auth;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Seeding;

// Every item is ensured by its natural key — client id, scope name, role name, email — never by
// "is the table empty", so an item added to the list later still seeds (architecture §3.4).
// Everything reconciles: config/code win on real drift, an unchanged item is never rewritten.
// The one nuance is passwords, which reconcile only where config declares one — production
// declares addresses alone, so a password a human set through the reset flow is never touched
// there. Seeding never deletes rows, and never touches accounts config doesn't declare.
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

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Users.Any(user => user.Roles.Contains(AuthRoles.GlobalAdmin)))
        {
            // The §3.4 guarantee: seeding leaves somebody able to administrate. A config that
            // can't is a mistake, not a choice — the deliberate opt-out is Database:Seed off.
            throw new InvalidOperationException(
                $"No Seed:Users entry carries the '{AuthRoles.GlobalAdmin}' role, so seeding "
                    + "would leave nobody able to administrate. Declare one, or set "
                    + "Database:Seed to false."
            );
        }

        await EnsureRolesAsync();
        await ReconcileScopesAsync(cancellationToken);
        await ReconcileClientsAsync(cancellationToken);

        foreach (var user in options.Value.Users)
        {
            await ReconcileUserAsync(user);
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

    private async Task ReconcileUserAsync(SeedUser seed)
    {
        if (string.IsNullOrWhiteSpace(seed.Email))
        {
            throw new InvalidOperationException("Every Seed:Users entry needs an Email.");
        }

        foreach (var role in seed.Roles)
        {
            if (!AuthRoles.All.Contains(role, StringComparer.Ordinal))
            {
                // Roles are fixed in code; a typo here would seed a row that grants nothing.
                throw new InvalidOperationException(
                    $"Seed user '{seed.Email}' declares unknown role '{role}'. "
                        + $"Roles are code-declared: {string.Join(", ", AuthRoles.All)}."
                );
            }
        }

        var existing = await users.FindByEmailAsync(seed.Email);
        if (existing is null)
        {
            await CreateUserAsync(seed);
            return;
        }

        var changed = false;

        // Config owns a declared account's memberships, exactly — a role granted out of band to
        // a *seeded* user does not survive a boot. Accounts config doesn't declare are never
        // touched.
        var currentRoles = await users.GetRolesAsync(existing);
        var missingRoles = seed.Roles.Except(currentRoles, StringComparer.Ordinal).ToList();
        var extraRoles = currentRoles.Except(seed.Roles, StringComparer.Ordinal).ToList();
        if (missingRoles.Count > 0)
        {
            ThrowIfFailed(
                await users.AddToRolesAsync(existing, missingRoles),
                $"user '{seed.Email}'"
            );
            changed = true;
        }

        if (extraRoles.Count > 0)
        {
            ThrowIfFailed(
                await users.RemoveFromRolesAsync(existing, extraRoles),
                $"user '{seed.Email}'"
            );
            changed = true;
        }

        // A declared password reconciles; no declared password is no opinion, not "remove it" —
        // production declares addresses alone, so a password a human set through the reset flow
        // is never touched there.
        if (
            seed.Password is { Length: > 0 } password
            && !await users.CheckPasswordAsync(existing, password)
        )
        {
            if (await users.HasPasswordAsync(existing))
            {
                ThrowIfFailed(await users.RemovePasswordAsync(existing), $"user '{seed.Email}'");
            }

            ThrowIfFailed(await users.AddPasswordAsync(existing, password), $"user '{seed.Email}'");
            changed = true;
        }

        if (!existing.EmailConfirmed)
        {
            existing.EmailConfirmed = true;
            ThrowIfFailed(await users.UpdateAsync(existing), $"user '{seed.Email}'");
            changed = true;
        }

        if (changed)
        {
            LogUserReconciled(logger, seed.Email);
        }
    }

    private async Task CreateUserAsync(SeedUser seed)
    {
        var user = new ApplicationUser
        {
            UserName = seed.Email,
            Email = seed.Email,
            EmailConfirmed = true,
        };

        // Without a configured password the account has no usable one: activation goes through
        // the ordinary forgot-password flow, so production never carries a password in
        // configuration.
        ThrowIfFailed(
            seed.Password is { Length: > 0 } password
                ? await users.CreateAsync(user, password)
                : await users.CreateAsync(user),
            $"user '{seed.Email}'"
        );
        if (seed.Roles.Count > 0)
        {
            ThrowIfFailed(await users.AddToRolesAsync(user, seed.Roles), $"user '{seed.Email}'");
        }

        LogUserCreated(logger, seed.Email);
    }

    // The two shapes a seeded client can take, both fixed in code: a browser client — public or
    // confidential — gets authorization code + PKCE + refresh with implicit consent (D17 — every
    // v1 client is first-party); a machine client gets client credentials and the back-channel
    // endpoints, nothing a browser would ever touch. There is still no grant-type knob in config.
    private static OpenIddictApplicationDescriptor DescriptorFor(SeedClient client)
    {
        if (string.IsNullOrWhiteSpace(client.ClientId))
        {
            throw new InvalidOperationException("Every Seed:Clients entry needs a ClientId.");
        }

        return client.Type == SeedClientType.Machine
            ? MachineDescriptorFor(client)
            : BrowserDescriptorFor(client);
    }

    private static OpenIddictApplicationDescriptor BrowserDescriptorFor(SeedClient client)
    {
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

        if (confidential)
        {
            // Introspection demands an authenticated caller (RFC 7662), so the permission goes
            // to confidential clients only — the BFF can ask whether a token it presented is
            // still active without holding auth's key material.
            descriptor.Permissions.Add(Permissions.Endpoints.Introspection);
        }

        foreach (var uri in client.RedirectUris)
        {
            descriptor.RedirectUris.Add(ParseUri(client.ClientId, uri));
        }

        foreach (var uri in client.PostLogoutRedirectUris)
        {
            descriptor.PostLogoutRedirectUris.Add(ParseUri(client.ClientId, uri));
        }

        AddGrantedScopes(descriptor, client);

        return descriptor;
    }

    private static OpenIddictApplicationDescriptor MachineDescriptorFor(SeedClient client)
    {
        if (string.IsNullOrWhiteSpace(client.Secret))
        {
            throw new InvalidOperationException(
                $"Machine client '{client.ClientId}' has no Secret. There is no fallback: "
                    + "a silently-defaulted secret would ship in a public repo (§3.4)."
            );
        }

        if (client.RedirectUris.Count > 0 || client.PostLogoutRedirectUris.Count > 0)
        {
            throw new InvalidOperationException(
                $"Machine client '{client.ClientId}' declares redirect URIs, but a machine "
                    + "client never sees a browser. Declare it Public or Confidential, or "
                    + "remove them."
            );
        }

        // Always confidential — the secret is the entire authentication story — and no consent
        // type: consent is a user concept, and no user ever appears in this flow.
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = client.ClientId,
            DisplayName = client.DisplayName ?? client.ClientId,
            ClientType = ClientTypes.Confidential,
            ClientSecret = client.Secret,
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Introspection,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.ClientCredentials,
            },
        };

        AddGrantedScopes(descriptor, client);

        return descriptor;
    }

    private static void AddGrantedScopes(
        OpenIddictApplicationDescriptor descriptor,
        SeedClient client
    )
    {
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Created seed user {Email}.")]
    private static partial void LogUserCreated(ILogger logger, string email);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciled seed user {Email}.")]
    private static partial void LogUserReconciled(ILogger logger, string email);
}
