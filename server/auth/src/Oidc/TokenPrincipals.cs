using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MyStack.Auth.Data;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Oidc;

internal static class TokenPrincipals
{
    /// <summary>
    /// The principal OpenIddict serializes into tokens: Identity's claims for the user, the
    /// permission overrides live at this instant, the granted scopes, and a destination per
    /// claim deciding which token carries it.
    /// </summary>
    public static async Task<ClaimsPrincipal> CreateAsync(
        SignInManager<ApplicationUser> signInManager,
        AuthDbContext database,
        ApplicationUser user,
        ImmutableArray<string> scopes
    )
    {
        var principal = await signInManager.CreateUserPrincipalAsync(user);

        // Read fresh on every issuance — refresh included — so removing an override takes effect
        // on the next token (§3.1's revocation-latency bound) and an expired row silently stops
        // minting. The strings go in verbatim: auth never interprets a permission.
        var now = DateTimeOffset.UtcNow;
        var overrides = await database
            .PermissionOverrides.Where(row =>
                row.UserId == user.Id && (row.ExpiresAt == null || row.ExpiresAt > now)
            )
            .Select(row => new { row.Kind, row.Permission })
            .ToListAsync();

        var identity = (ClaimsIdentity)principal.Identity!;
        foreach (var entry in overrides)
        {
            identity.AddClaim(
                new Claim(
                    entry.Kind == PermissionOverrideKind.Deny
                        ? AuthClaims.PermissionDeny
                        : AuthClaims.Permission,
                    entry.Permission
                )
            );
        }

        principal.SetScopes(scopes);
        SetApiResource(principal, scopes);

        principal.SetDestinations(claim => DestinationsFor(claim, principal));

        return principal;
    }

    /// <summary>
    /// The principal behind a machine client's token (client credentials): the client's own
    /// identity and scopes, and deliberately nothing resembling a user.
    /// </summary>
    public static ClaimsPrincipal CreateForClient(
        string clientId,
        string? displayName,
        ImmutableArray<string> scopes
    )
    {
        var identity = new ClaimsIdentity(
            TokenValidationParameters.DefaultAuthenticationType,
            Claims.Name,
            Claims.Role
        );

        identity.SetClaim(Claims.Subject, clientId);
        identity.SetClaim(Claims.Name, displayName);

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        SetApiResource(principal, scopes);

        // This flow mints no identity token, so every claim rides the access token.
        principal.SetDestinations(claim => [Destinations.AccessToken]);

        return principal;
    }

    // The one `aud` rule, shared by user and client tokens: an api.* scope means the token is
    // minted for the API's audience.
    private static void SetApiResource(ClaimsPrincipal principal, ImmutableArray<string> scopes)
    {
        if (scopes.Contains(ApiScopes.Read) || scopes.Contains(ApiScopes.Write))
        {
            principal.SetResources(ApiScopes.Resource);
        }
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
