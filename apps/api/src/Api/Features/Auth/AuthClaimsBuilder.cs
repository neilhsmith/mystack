using System.Collections.Immutable;
using System.Security.Claims;
using Api.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;

namespace Api.Features.Auth;

/// <summary>
/// Builds the <see cref="ClaimsIdentity"/> that OpenIddict signs into tokens.
/// <para>
/// Two paths, same output shape:
/// </para>
/// <list type="bullet">
///   <item><see cref="BuildForUserAsync"/> — interactive flows (authorization code, refresh
///   token). Subject is the user id; role claims are added one-per-role from Identity;
///   email / name come from the user record when requested via the matching OIDC scope.</item>
///   <item><see cref="BuildForClient"/> — non-interactive (client_credentials). Subject is
///   the client id; no user-specific claims.</item>
/// </list>
/// <para>
/// Destinations are set per-claim so the JSON Web Token only carries claims the consuming
/// scope actually justifies — e.g. <c>email</c> only lands in tokens when the request was
/// granted the <c>email</c> scope. Role is always destined for the access token (the API
/// needs it to resolve permissions); identity-token presence depends on the OIDC
/// <c>profile</c> scope, matching the spec.
/// </para>
/// </summary>
public sealed class AuthClaimsBuilder
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly IOpenIddictScopeManager _scopes;

    public AuthClaimsBuilder(
        UserManager<ApplicationUser> users,
        IOpenIddictScopeManager scopes)
    {
        _users = users;
        _scopes = scopes;
    }

    public async Task<ClaimsIdentity> BuildForUserAsync(
        ApplicationUser user,
        ImmutableArray<string> grantedScopes,
        CancellationToken ct = default)
    {
        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role);

        identity
            .SetClaim(OpenIddictConstants.Claims.Subject, user.Id.ToString())
            .SetClaim(OpenIddictConstants.Claims.Email, user.Email)
            .SetClaim(OpenIddictConstants.Claims.EmailVerified, user.EmailConfirmed)
            .SetClaim(OpenIddictConstants.Claims.PreferredUsername, user.UserName)
            .SetClaim(OpenIddictConstants.Claims.Name, user.DisplayName ?? user.UserName);

        // SetClaims(string, ImmutableArray<string>) drops any existing claims of the
        // same type before adding the new values — exactly what we want for role.
        var roles = (await _users.GetRolesAsync(user)).ToImmutableArray();
        identity.SetClaims(OpenIddictConstants.Claims.Role, roles);

        identity.SetScopes(grantedScopes);
        await SetResourcesFromScopesAsync(identity, ct);
        identity.SetDestinations(GetDestinations);

        return identity;
    }

    public async Task<ClaimsIdentity> BuildForClientAsync(
        string clientId,
        ImmutableArray<string> grantedScopes,
        IEnumerable<string> clientRoles,
        CancellationToken ct = default)
    {
        var identity = new ClaimsIdentity(
            authenticationType: TokenValidationParameters.DefaultAuthenticationType,
            nameType: OpenIddictConstants.Claims.Name,
            roleType: OpenIddictConstants.Claims.Role);

        identity
            .SetClaim(OpenIddictConstants.Claims.Subject, clientId)
            .SetClaim(OpenIddictConstants.Claims.Name, clientId);

        // Service clients get role claims from their OpenIddict application registration
        // (see ClientRoles helper). Permissions are then derived from those roles by the
        // resource server, exactly like for user tokens — no separate code path.
        identity.SetClaims(OpenIddictConstants.Claims.Role, clientRoles.ToImmutableArray());

        identity.SetScopes(grantedScopes);
        await SetResourcesFromScopesAsync(identity, ct);
        identity.SetDestinations(GetDestinations);

        return identity;
    }

    /// <summary>
    /// Join the granted scopes against the configured resources (each scope row carries
    /// the audiences it grants access to) and stamp them onto the identity as
    /// <c>aud</c> claims. The validation pipeline matches <c>aud</c> against
    /// <c>AuthAudiences.ApiAudience</c> — without this call, issued tokens carry no
    /// audience and get rejected by <c>OpenIddict.Validation</c> with <c>invalid_token</c>.
    /// </summary>
    private async Task SetResourcesFromScopesAsync(ClaimsIdentity identity, CancellationToken ct)
    {
        var scopes = identity.GetScopes();
        var resources = new List<string>();
        await foreach (var resource in _scopes.ListResourcesAsync(scopes, ct))
        {
            resources.Add(resource);
        }
        identity.SetResources(resources.ToImmutableArray());
    }

    /// <summary>
    /// Decide which token(s) each claim should land in. Access token always gets the
    /// identity-bearing claims the API needs (subject, role, email, scope); identity token
    /// gets the claims OIDC says it should when the matching scope was granted.
    /// </summary>
    private static IEnumerable<string> GetDestinations(Claim claim)
    {
        switch (claim.Type)
        {
            case OpenIddictConstants.Claims.Name:
            case OpenIddictConstants.Claims.PreferredUsername:
                yield return OpenIddictConstants.Destinations.AccessToken;
                if (claim.Subject?.HasScope(OpenIddictConstants.Scopes.Profile) == true)
                {
                    yield return OpenIddictConstants.Destinations.IdentityToken;
                }
                yield break;

            case OpenIddictConstants.Claims.Email:
            case OpenIddictConstants.Claims.EmailVerified:
                yield return OpenIddictConstants.Destinations.AccessToken;
                if (claim.Subject?.HasScope(OpenIddictConstants.Scopes.Email) == true)
                {
                    yield return OpenIddictConstants.Destinations.IdentityToken;
                }
                yield break;

            case OpenIddictConstants.Claims.Role:
                yield return OpenIddictConstants.Destinations.AccessToken;
                // OIDC-style role propagation gates identity-token presence on the
                // (non-standard but widely supported) `roles` scope. Match how
                // OpenIddict's reference sample handles it.
                if (claim.Subject?.HasScope(OpenIddictConstants.Scopes.Roles) == true)
                {
                    yield return OpenIddictConstants.Destinations.IdentityToken;
                }
                yield break;

            // Never expose security-sensitive claims (e.g. AspNetSecurityStamp) to clients.
            case "AspNet.Identity.SecurityStamp":
                yield break;

            default:
                yield return OpenIddictConstants.Destinations.AccessToken;
                yield break;
        }
    }
}
