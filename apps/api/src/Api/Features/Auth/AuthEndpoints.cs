using System.Collections.Immutable;
using System.Security.Claims;
using Api.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
// `using Microsoft.AspNetCore;` exposes OpenIddict's extension methods on HttpContext
// (GetOpenIddictServerRequest etc.) — they live on Microsoft.AspNetCore.OpenIddictServerAspNetCoreHelpers
// by design so they're auto-discoverable in ASP.NET Core projects.
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;

namespace Api.Features.Auth;

/// <summary>
/// Maps the OAuth 2.0 / OpenID Connect endpoints required by OpenIddict's pass-through
/// mode (<c>EnableAuthorizationEndpointPassthrough</c>, <c>EnableTokenEndpointPassthrough</c>,
/// <c>EnableEndSessionEndpointPassthrough</c>, <c>EnableUserInfoEndpointPassthrough</c>).
/// OpenIddict handles protocol-level concerns (parameter parsing, PKCE verification,
/// client authentication) and hands us a populated <see cref="OpenIddictRequest"/>; we
/// produce a <see cref="ClaimsPrincipal"/> via <see cref="AuthClaimsBuilder"/> and return
/// <see cref="Results.SignIn"/>, leaving token serialisation / encryption / signing to
/// OpenIddict.
/// <para>
/// Routes match OpenIddict's defaults registered in <c>Program.cs</c>:
/// <c>/connect/authorize</c>, <c>/connect/token</c>, <c>/connect/userinfo</c>,
/// <c>/connect/endsession</c>.
/// </para>
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // MapMethods with a Task<IResult>-returning handler triggers ASP0016 unless we
        // tell the router to treat it as a Delegate (so the returned IResult is written
        // to the response). Cast at the call site.
        // Authorize endpoint — interactive. Anonymous because we challenge the cookie
        // scheme ourselves if the user isn't signed in.
        app.MapMethods("/connect/authorize", [HttpMethods.Get, HttpMethods.Post], (Delegate)Authorize)
            .WithTags("Auth")
            .AllowAnonymous();

        // Token endpoint — handles authorization_code, refresh_token, client_credentials.
        // POST only per RFC 6749 §3.2. Anonymous because client authentication happens
        // through OpenIddict's pipeline.
        app.MapPost("/connect/token", Token)
            .WithTags("Auth")
            .AllowAnonymous();

        // Logout endpoint — interactive. Anonymous so we can sign anyone out without an
        // already-valid session being required.
        app.MapMethods("/connect/endsession", [HttpMethods.Get, HttpMethods.Post], (Delegate)EndSession)
            .WithTags("Auth")
            .AllowAnonymous();

        // Userinfo — protected by the bearer access token (validated by OpenIddict.Validation).
        app.MapMethods("/connect/userinfo", [HttpMethods.Get, HttpMethods.Post], (Delegate)UserInfo)
            .WithTags("Auth")
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
            });

        return app;
    }

    // ---------- /connect/authorize ----------

    private static async Task<IResult> Authorize(
        HttpContext context,
        UserManager<ApplicationUser> users,
        AuthClaimsBuilder claimsBuilder,
        CancellationToken ct)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException(
                "The OpenIddict request could not be retrieved on /connect/authorize.");

        // Identity's application cookie scheme — kicks the user to the login page when
        // they're not yet authenticated, returning here after sign-in via the configured
        // RedirectUri on AuthenticationProperties.
        var authResult = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (!authResult.Succeeded || authResult.Principal is null)
        {
            return Results.Challenge(
                authenticationSchemes: [IdentityConstants.ApplicationScheme],
                properties: new AuthenticationProperties
                {
                    RedirectUri = context.Request.PathBase + context.Request.Path + QueryString.Create(
                        context.Request.HasFormContentType
                            ? context.Request.Form.ToList()
                            : context.Request.Query.ToList()).Value,
                });
        }

        var user = await users.GetUserAsync(authResult.Principal)
            ?? throw new InvalidOperationException("Authenticated principal did not resolve to a user.");

        var grantedScopes = (request.GetScopes()).ToImmutableArray();
        var identity = await claimsBuilder.BuildForUserAsync(user, grantedScopes, ct);

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            properties: null,
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    // ---------- /connect/token ----------

    private static async Task<IResult> Token(
        HttpContext context,
        UserManager<ApplicationUser> users,
        AuthClaimsBuilder claimsBuilder,
        IOpenIddictApplicationManager applications,
        CancellationToken ct)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException(
                "The OpenIddict request could not be retrieved on /connect/token.");

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            // The bearer principal here is the one OpenIddict reconstructed from the auth
            // code / refresh token; trust it but rebuild role + scope claims so changes
            // since the previous exchange (e.g. role removed) take effect.
            var authResult = await context.AuthenticateAsync(
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            if (authResult.Principal is null)
            {
                return Forbid("The token is no longer valid.");
            }

            var subject = authResult.Principal.GetClaim(OpenIddictConstants.Claims.Subject);
            if (string.IsNullOrEmpty(subject))
            {
                return Forbid("The token is missing the subject claim.");
            }

            var user = await users.FindByIdAsync(subject);
            if (user is null)
            {
                return Forbid("The user no longer exists.");
            }
            if (await users.IsLockedOutAsync(user))
            {
                return Forbid("The user account is locked out.");
            }

            var grantedScopes = authResult.Principal.GetScopes();
            var identity = await claimsBuilder.BuildForUserAsync(user, grantedScopes, ct);

            return Results.SignIn(
                new ClaimsPrincipal(identity),
                properties: null,
                authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        if (request.IsClientCredentialsGrantType())
        {
            // Resolve the requesting application — OpenIddict already authenticated the
            // client (secret check or alternative client_assertion). We just decide what
            // claims go into the token.
            var clientId = request.ClientId
                ?? throw new InvalidOperationException("The client_id is missing.");
            var application = await applications.FindByClientIdAsync(clientId, ct)
                ?? throw new InvalidOperationException("The OpenIddict application cannot be found.");

            var displayName = await applications.GetDisplayNameAsync(application, ct) ?? clientId;
            var clientRoles = await ClientRoles.GetAsync(applications, application, ct);

            var grantedScopes = request.GetScopes();
            var identity = await claimsBuilder.BuildForClientAsync(clientId, grantedScopes, clientRoles, ct);

            // For client_credentials we expose the application name in the access token
            // so log lines on the API side can identify the caller.
            identity.SetClaim(OpenIddictConstants.Claims.Name, displayName);

            return Results.SignIn(
                new ClaimsPrincipal(identity),
                properties: null,
                authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return Results.BadRequest(new OpenIddictResponse
        {
            Error = OpenIddictConstants.Errors.UnsupportedGrantType,
            ErrorDescription = "The specified grant type is not supported.",
        });
    }

    // ---------- /connect/endsession ----------

    private static async Task<IResult> EndSession(HttpContext context)
    {
        // Sign out of the application cookie first so the local session ends, then let
        // OpenIddict produce the OIDC sign-out response (redirect to post_logout_redirect_uri
        // if registered).
        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.SignOut(
            authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme],
            properties: new AuthenticationProperties { RedirectUri = "/" });
    }

    // ---------- /connect/userinfo ----------

    private static async Task<IResult> UserInfo(
        HttpContext context,
        UserManager<ApplicationUser> users)
    {
        var subject = context.User.GetClaim(OpenIddictConstants.Claims.Subject);
        if (string.IsNullOrEmpty(subject))
        {
            return Forbid("The access token does not carry a subject claim.");
        }

        var user = await users.FindByIdAsync(subject);
        if (user is null)
        {
            return Forbid("The user no longer exists.");
        }

        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [OpenIddictConstants.Claims.Subject] = user.Id.ToString(),
        };

        if (context.User.HasScope(OpenIddictConstants.Scopes.Email))
        {
            claims[OpenIddictConstants.Claims.Email] = user.Email ?? string.Empty;
            claims[OpenIddictConstants.Claims.EmailVerified] = user.EmailConfirmed;
        }

        if (context.User.HasScope(OpenIddictConstants.Scopes.Profile))
        {
            claims[OpenIddictConstants.Claims.PreferredUsername] = user.UserName ?? string.Empty;
            claims[OpenIddictConstants.Claims.Name] = user.DisplayName ?? user.UserName ?? string.Empty;
        }

        if (context.User.HasScope(OpenIddictConstants.Scopes.Roles))
        {
            var roles = await users.GetRolesAsync(user);
            claims[OpenIddictConstants.Claims.Role] = roles.ToArray();
        }

        return Results.Ok(claims);
    }

    // ---------- helpers ----------

    private static IResult Forbid(string description) =>
        Results.Forbid(
            authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme],
            properties: new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] =
                    OpenIddictConstants.Errors.InvalidGrant,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
            }));
}
