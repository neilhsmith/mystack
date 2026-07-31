using System.Collections.Immutable;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MyStack.Auth.Data;
using MyStack.Contracts.Api;
using MyStack.Contracts.Auth;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyStack.Auth.Oidc;

internal static class TokenPrincipals
{
    /// <summary>
    /// The principal OpenIddict serializes into tokens: Identity's claims for the user, the
    /// permission overrides live at this instant, the granted scopes, when the user
    /// authenticated, and a destination per claim deciding which token carries it.
    /// </summary>
    public static async Task<ClaimsPrincipal> CreateAsync(
        SignInManager<ApplicationUser> signInManager,
        AuthDbContext database,
        ApplicationUser user,
        ImmutableArray<string> scopes,
        DateTimeOffset? authenticatedAt
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

        // OIDC's auth_time: required in the id token whenever the client sent max_age, minted
        // always because a numeric timestamp costs nothing and clients use it for freshness
        // decisions. Null (an old stored principal without one) simply omits the claim.
        identity.SetClaim(Claims.AuthenticationTime, (long?)authenticatedAt?.ToUnixTimeSeconds());

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
            // Scope gates both copies: a token granted only `api.read` carries no name, email
            // or role anywhere. Access tokens are unencrypted JWTs, so they are not exempt from
            // data minimization just because the API is first-party — a client that wants the
            // claims asks for the scopes.
            Claims.Name => WhenScoped(principal, Scopes.Profile),
            Claims.Email => WhenScoped(principal, Scopes.Email),
            Claims.Role => WhenScoped(principal, Scopes.Roles),
            // The id token is where OIDC requires auth_time, and RFC 9068 lists it among a JWT
            // access token's standard claims — the access-token copy is also what lets userinfo
            // (which rebuilds from the presented token) carry it forward consistently.
            Claims.AuthenticationTime => [Destinations.AccessToken, Destinations.IdentityToken],
            AuthClaims.Permission or AuthClaims.PermissionDeny => [Destinations.AccessToken],
            // Deny by default: whatever Identity adds to the cookie principal — the security
            // stamp, today — stays out of every token unless a case above says otherwise.
            _ => [],
        };

    private static ImmutableArray<string> WhenScoped(ClaimsPrincipal principal, string scope) =>
        principal.HasScope(scope) ? [Destinations.AccessToken, Destinations.IdentityToken] : [];

    /// <summary>
    /// Reads <c>auth_time</c> back off a stored principal — an authorization code, refresh
    /// token or device code — so re-issuance carries the original authentication time forward
    /// instead of minting a fresher one.
    /// </summary>
    public static DateTimeOffset? AuthenticatedAt(ClaimsPrincipal? principal) =>
        long.TryParse(principal?.GetClaim(Claims.AuthenticationTime), out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
