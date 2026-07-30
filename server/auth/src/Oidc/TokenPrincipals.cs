using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using MyStack.Auth.Data;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Oidc;

internal static class TokenPrincipals
{
    /// <summary>
    /// The principal OpenIddict serializes into tokens: Identity's claims for the user, the
    /// granted scopes, and a destination per claim deciding which token carries it.
    /// </summary>
    public static async Task<ClaimsPrincipal> CreateAsync(
        SignInManager<ApplicationUser> signInManager,
        ApplicationUser user,
        ImmutableArray<string> scopes
    )
    {
        var principal = await signInManager.CreateUserPrincipalAsync(user);

        principal.SetScopes(scopes);

        if (scopes.Contains(ApiScopes.Read) || scopes.Contains(ApiScopes.Write))
        {
            principal.SetResources(ApiScopes.Resource);
        }

        principal.SetDestinations(claim => DestinationsFor(claim, principal));

        return principal;
    }

    // `sub` is not listed: OpenIddict itself puts the subject in both tokens.
    private static ImmutableArray<string> DestinationsFor(Claim claim, ClaimsPrincipal principal) =>
        claim.Type switch
        {
            Claims.Name => WithIdentityTokenWhen(principal, Scopes.Profile),
            Claims.Email => WithIdentityTokenWhen(principal, Scopes.Email),
            Claims.Role => WithIdentityTokenWhen(principal, Scopes.Roles),
            AuthClaims.Permission or AuthClaims.PermissionDeny => [Destinations.AccessToken],
            // Deny by default: whatever Identity adds to the cookie principal — the security
            // stamp, today — stays out of every token unless a case above says otherwise.
            _ => [],
        };

    private static ImmutableArray<string> WithIdentityTokenWhen(
        ClaimsPrincipal principal,
        string scope
    ) =>
        principal.HasScope(scope)
            ? [Destinations.AccessToken, Destinations.IdentityToken]
            : [Destinations.AccessToken];
}
